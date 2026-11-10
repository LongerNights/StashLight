using System.Diagnostics;
using System.Net;
using FrooxEngine;
using InventorySearch.Core;
using ResoniteModLoader;
using Record = FrooxEngine.Store.Record;

namespace InventorySearch;

/// <summary>
/// M4: indexes the content of the folders YOUR links point to (other people's / groups' folders), so it can be searched.
/// Opt-in: it only runs while the "Linked" toggle is on, so the network work is opt-in too.
///
/// Designed around what the Worlds-tab/slowness investigation measured:
///  * reads only, via direct cloud listings (never RecordDirectory, which makes the engine write every record into its
///    database and clogged its queue); nothing here touches the engine's local database;
///  * polite: one request at a time through a rate gate (default 5/s), starts only after the index is "ready" (so after the
///    startup hold), stops at once when the toggle is turned off, and backs off after repeated failures;
///  * incremental and resumable: each link is crawled separately, its result is published and saved as soon as it is done,
///    and a link is not crawled again until it is stale (default 24 h), so later launches usually do no network work at all;
///  * bounded: link-hop depth, folders per link, folders per pass.
/// The decisions (what to crawl, in what order, what to drop) are in Core's CrawlPlanner and are unit tested.
/// </summary>
internal static class LinkCrawler
{
    private static int _running;
    private static CancellationTokenSource? _cts;
    private static volatile bool _wake;
    private static DateTime _nextCheckUtc = DateTime.MinValue;

    /// <summary>Shown in the status line while a pass is running ("" otherwise).</summary>
    public static string Progress { get; private set; } = "";
    public static bool Active => Volatile.Read(ref _running) == 1;

    private static bool Enabled => InventorySearchMod.IncludeLinked;

    /// <summary>
    /// Forces a re-crawl: of one link (its key), or of all links (null). Forgets when they were last crawled and, if a pass is
    /// running, ends it so a fresh pass starts within a couple of seconds. Items already found stay searchable meanwhile.
    /// Also retries links that were marked unavailable. Returns how many links were reset.
    /// </summary>
    public static int Recrawl(string? key = null)
    {
        int n = IndexService.Parts.ForgetCrawl(key);
        _nextCheckUtc = DateTime.MinValue;
        _wake = true;
        if (Active) Cancel();            // the running pass stops at its next step; Kick starts the new one
        IndexService.Note(key == null ? $"link crawl: forced a re-crawl of all links ({n} reset)" : $"link crawl: forced a re-crawl of {key}");
        return n;
    }

    /// <summary>
    /// After a link was switched off or on in the Links panel: drop the items of links that are now off (right away, no pass
    /// needed), and look for work so one switched back on is crawled.
    /// </summary>
    public static void ApplyDisabledChange()
    {
        var disabled = CrawlPlanner.ParseDisabled(InventorySearchMod.Get(InventorySearchMod.DisabledLinksKey, ""));
        var seeds = CrawlPlanner.SeedsFrom(IndexService.Index.Entries);
        if (seeds.Count > 0)
        {
            int dropped = IndexService.Parts.RetainLinked(CrawlPlanner.KeepKeys(seeds, disabled));
            if (dropped > 0) { IndexService.RequestRepublish(); IndexService.RequestSave(5); }
        }
        Wake();
    }

    /// <summary>Check for work now instead of waiting for the next periodic check (used when the toggle is switched on).</summary>
    public static void Wake() { _wake = true; }

    public static void Cancel() { try { _cts?.Cancel(); } catch { /* already disposed */ } }

    /// <summary>Called from the index loop every ~2 s. Cheap unless it is time to plan; never blocks.</summary>
    public static void Kick(Engine engine)
    {
        if (!Enabled) { if (Active) Cancel(); return; }
        if (IndexService.Phase != "ready" || IndexService.UserId == null) return;
        if (!_wake && DateTime.UtcNow < _nextCheckUtc) return;
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;

        _wake = false;
        _nextCheckUtc = DateTime.UtcNow.AddMinutes(10);        // look again later: a 24 h TTL may have expired, new links may exist
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(() => RunAsync(engine, ct));
    }

    private sealed record SeedResult(List<IndexEntry> Entries, int Folders, bool Truncated, bool Unavailable, bool Transient);

    private static async Task RunAsync(Engine engine, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var limits = new CrawlLimits(
                MaxDepth: InventorySearchMod.Get(InventorySearchMod.LinkMaxDepthKey, 3),
                MaxFoldersPerSeed: InventorySearchMod.Get(InventorySearchMod.LinkMaxFoldersPerSeedKey, 500),
                MaxFoldersTotal: InventorySearchMod.Get(InventorySearchMod.LinkMaxFoldersTotalKey, 5000),
                RequestsPerSecond: InventorySearchMod.Get(InventorySearchMod.LinkRequestsPerSecondKey, 5),
                Ttl: TimeSpan.FromHours(Math.Max(1, InventorySearchMod.Get(InventorySearchMod.LinkRefreshHoursKey, 24))));
            var disabled = CrawlPlanner.ParseDisabled(InventorySearchMod.Get(InventorySearchMod.DisabledLinksKey, ""));
            var parts = IndexService.Parts;

            var seeds = CrawlPlanner.SeedsFrom(IndexService.Index.Entries);
            // Only prune when we can actually see your links: an empty list (index not loaded yet?) must never be read as
            // "every link was deleted", which would throw away everything crawled so far.
            int dropped = seeds.Count > 0 ? parts.RetainLinked(CrawlPlanner.KeepKeys(seeds, disabled)) : 0;
            if (dropped > 0)
            {
                IndexService.Note($"link crawl: dropped {dropped} items of links that were deleted or switched off");
                IndexService.RequestRepublish(); IndexService.RequestSave(5);
            }

            var plan = CrawlPlanner.Plan(seeds, parts.CrawlState(), disabled, DateTime.UtcNow, limits);
            if (plan.Count == 0) return;

            LocalDbProbe.Start(3, "link crawl");
            IndexService.Note($"link crawl: {plan.Count} of {seeds.Count} links to crawl, one request at a time, at most {limits.RequestsPerSecond:0.#}/s (runs in the background; switch Linked off to stop)");
            var gate = new MinIntervalGate(limits.RequestsPerSecond);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int folders = 0, entries = 0, done = 0, unavailable = 0, truncated = 0, transientInRow = 0;
            var lastSave = Stopwatch.StartNew();

            foreach (var seed in plan)
            {
                if (ct.IsCancellationRequested || !Enabled) { IndexService.Note("link crawl: stopped (Linked switched off or logged out)"); break; }
                if (folders >= limits.MaxFoldersTotal)
                {
                    IndexService.Note($"link crawl: reached the {limits.MaxFoldersTotal}-folder limit for this pass; the remaining links continue on the next pass");
                    _nextCheckUtc = DateTime.UtcNow.AddMinutes(2);
                    break;
                }
                Progress = $"crawling links {done + 1}/{plan.Count}: {seed.Name}";

                var r = await CrawlSeedAsync(engine, seed, limits, gate, visited, limits.MaxFoldersTotal - folders, ct).ConfigureAwait(false);
                if (r.Transient)
                {
                    // network trouble: do not record an outcome (so the link is tried again), and back off after a few in a row
                    if (++transientInRow >= 4) { IndexService.Note("link crawl: repeated network errors; pausing for 10 minutes"); break; }
                    await Task.Delay(TimeSpan.FromSeconds(5 * transientInRow), ct).ConfigureAwait(false);
                    continue;
                }
                transientInRow = 0;

                var info = new CrawlInfo(seed.Key, seed.Name, DateTime.UtcNow, r.Folders, r.Entries.Count, r.Truncated, r.Unavailable);
                if (r.Unavailable) { parts.SetCrawlInfo(info); unavailable++; }
                else parts.SetLinked(seed.Key, r.Entries, info);
                folders += r.Folders; entries += r.Entries.Count; done++; if (r.Truncated) truncated++;

                IndexService.RequestRepublish();                       // results appear link by link
                if (lastSave.Elapsed > TimeSpan.FromSeconds(60)) { IndexService.RequestSave(2); lastSave.Restart(); }
            }

            IndexService.Note($"link crawl pass finished in {clock.Elapsed.TotalSeconds:F0}s: {done} links crawled ({folders} folders, {entries} items), {unavailable} unavailable, {truncated} cut at the per-link limit");
            IndexService.RequestRepublish(); IndexService.RequestSave(2);
        }
        catch (OperationCanceledException) { IndexService.Note("link crawl: cancelled"); }
        catch (Exception ex) { IndexService.Note("link crawl failed: " + ex.Message); }
        finally
        {
            Progress = "";
            Interlocked.Exchange(ref _running, 0);
            IndexService.RequestRepublish();                            // refresh the status line
        }
    }

    // ------------------------------------------------------------------ one link

    private static async Task<SeedResult> CrawlSeedAsync(Engine engine, LinkSeed seed, CrawlLimits limits, MinIntervalGate gate,
                                                         HashSet<string> visited, int totalFoldersLeft, CancellationToken ct)
    {
        var entries = new List<IndexEntry>();
        int folders = 0, cap = Math.Max(1, Math.Min(limits.MaxFoldersPerSeed, totalFoldersLeft));
        bool truncated = false;

        // work items: either a link URI still to resolve, or a folder to list; depth = link hops so far
        var queue = new Queue<(string? LinkUri, string? Owner, string? Path, int Depth)>();
        queue.Enqueue((seed.TargetUri, null, null, 1));
        int transient = 0;

        while (queue.Count > 0)
        {
            if (ct.IsCancellationRequested || !Enabled) break;
            var item = queue.Dequeue();

            if (item.LinkUri != null)
            {
                var (rec, state) = await ResolveAsync(engine, item.LinkUri, gate, ct).ConfigureAwait(false);
                if (rec == null)
                {
                    if (IsGone(state))
                    {
                        if (item.Depth == 1) return new SeedResult(entries, folders, truncated, Unavailable: true, Transient: false);
                        continue;                                            // a dead link found inside a folder: just skip it
                    }
                    if (++transient >= 3) return new SeedResult(entries, folders, truncated, false, Transient: true);
                    queue.Enqueue(item);                                     // try this one again later in the pass
                    continue;
                }
                if (rec.IsDeleted) { if (item.Depth == 1) return new SeedResult(entries, folders, truncated, true, false); continue; }
                if (rec.RecordType == "directory") queue.Enqueue((null, rec.OwnerId, rec.Path + "\\" + rec.Name, item.Depth));
                else if (rec.RecordType == "link" && item.Depth < limits.MaxDepth && !string.IsNullOrEmpty(rec.AssetURI))
                    queue.Enqueue((rec.AssetURI, null, null, item.Depth + 1));
                else { var e = RecordMapper.ToEntry(rec, Source.Linked, seed.Name, item.Depth); if (e != null) entries.Add(e); }   // a link straight to one item
                continue;
            }

            // a folder
            string key = item.Owner + "|" + item.Path;
            if (!visited.Add(key)) continue;                                 // already covered (by this or an earlier link this pass)
            if (folders >= cap) { truncated = true; continue; }              // drain the queue, but note that we stopped short

            var (list, listState) = await ListAsync(engine, item.Owner!, item.Path!, gate, ct).ConfigureAwait(false);
            if (list == null)
            {
                if (IsGone(listState)) continue;                             // the folder was deleted or went private
                if (++transient >= 3) return new SeedResult(entries, folders, truncated, false, Transient: true);
                visited.Remove(key); queue.Enqueue(item);
                continue;
            }
            folders++;
            foreach (var r in list)
            {
                var e = RecordMapper.ToEntry(r, Source.Linked, seed.Name, item.Depth);
                if (e != null) entries.Add(e);
                if (r.IsDeleted) continue;
                if (r.RecordType == "directory") queue.Enqueue((null, r.OwnerId ?? item.Owner, r.Path + "\\" + r.Name, item.Depth));
                else if (r.RecordType == "link" && item.Depth < limits.MaxDepth && !string.IsNullOrEmpty(r.AssetURI))
                    queue.Enqueue((r.AssetURI, null, null, item.Depth + 1));
            }
            if (folders % 25 == 0) Progress = $"crawling links: {seed.Name} ({folders} folders)";
        }
        return new SeedResult(entries, folders, truncated, false, false);
    }

    // ------------------------------------------------------------------ requests (all through the rate gate)

    private static bool IsGone(HttpStatusCode s)
        => s is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.Gone;

    private static async Task<(Record? rec, HttpStatusCode state)> ResolveAsync(Engine engine, string uri, MinIntervalGate gate, CancellationToken ct)
    {
        try
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            // GetRecord(Uri) understands both link forms: resrec:///owner/R-id and resrec:///owner/Inventory/Some folder
            var res = await engine.Cloud.Records.GetRecord<Record>(new Uri(uri)).ConfigureAwait(false);
            return res.IsOK && res.Entity != null ? (res.Entity, res.State) : (null, res.State);
        }
        catch (OperationCanceledException) { throw; }
        catch (ArgumentException) { return (null, HttpStatusCode.NotFound); }       // not a record URI at all: nothing to crawl
        catch (UriFormatException) { return (null, HttpStatusCode.NotFound); }
        catch (Exception ex) { ResoniteMod.Warn($"[InventorySearch] resolving a link failed: {ex.Message}"); return (null, HttpStatusCode.RequestTimeout); }
    }

    private static async Task<(List<Record>? list, HttpStatusCode state)> ListAsync(Engine engine, string owner, string path, MinIntervalGate gate, CancellationToken ct)
    {
        try
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            var res = await engine.Cloud.Records.GetRecords<Record>(owner, null, path).ConfigureAwait(false);
            return res.IsOK && res.Entity != null ? (res.Entity, res.State) : (null, res.State);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { ResoniteMod.Warn($"[InventorySearch] listing a linked folder failed: {ex.Message}"); return (null, HttpStatusCode.RequestTimeout); }
    }
}
