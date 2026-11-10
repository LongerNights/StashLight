using System.Diagnostics;
using System.Text;
using FrooxEngine;
using InventorySearch.Core;
using ResoniteModLoader;
using Record = FrooxEngine.Store.Record;

namespace InventorySearch;

/// <summary>
/// Owns the live index. The index is assembled from independent parts (<see cref="IndexParts"/>): your inventory tree, the
/// engine's cached-records database, and one part per group / Local Storage. At login the parts are restored from the disk
/// cache so search works immediately (groups included); each live source then refreshes its own part in the background.
/// Items you save or delete while the game runs are applied straight away (see ChangeHooks).
/// </summary>
internal static class IndexService
{
    private static volatile SearchIndex _index = SearchIndex.Empty;
    private static readonly IndexParts _parts = new();
    private static int _started;
    private static readonly StringBuilder _timeline = new();
    private static int _republishVersion, _saveVersion;
    private static DateTime _lastTreeRefreshUtc = DateTime.MinValue, _lastGroupRefreshUtc = DateTime.MinValue;

    public static SearchIndex Index => _index;

    /// <summary>Raised (on a pool thread) every time a new index snapshot is published.</summary>
    public static event Action? Published;
    public static string Phase { get; private set; } = "not started";
    public static string? UserId { get; private set; }

    /// <summary>Group inventories (and Local Storage, when installed) the UI can scope to. Updated as they're discovered.</summary>
    public static volatile IReadOnlyList<GroupInventories.Target> Targets = Array.Empty<GroupInventories.Target>();

    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        if (!InventorySearchMod.Get(InventorySearchMod.EnabledKey, true)) { Phase = "disabled"; return; }
        _ = Task.Run(RunAsync);
    }

    private static void Log(string msg)
    {
        lock (_timeline) _timeline.AppendLine($"{DateTime.Now:HH:mm:ss.fff} {msg}");
        ResoniteMod.Msg("[InventorySearch] " + msg);
    }

    public static string Timeline() { lock (_timeline) return _timeline.ToString(); }

    /// <summary>The parts the index is assembled from; the link crawler stores its per-link results here.</summary>
    internal static IndexParts Parts => _parts;
    internal static void RequestRepublish() => ScheduleRepublish();
    internal static void RequestSave(int seconds) => ScheduleSave(seconds);

    /// <summary>Lets other diagnostics write into the same timeline/log.</summary>
    internal static void Note(string msg) => Log(msg);

    private static int Concurrency => InventorySearchMod.Get(InventorySearchMod.LoadConcurrencyKey, 4);

    // ------------------------------------------------------------------ main loop

    private static async Task RunAsync()
    {
        try
        {
            RecordDirectory? lastRoot = null;
            while (true)
            {
                var engine = Engine.Current;
                var root = engine?.Cloud?.InventoryRootDirectory;
                if (engine == null || root == null)
                {
                    if (lastRoot != null)
                    {
                        Log("logged out; clearing index");
                        LinkCrawler.Cancel();
                        _parts.Clear(); RecordRegistry.Clear(); _index = SearchIndex.Empty; Targets = Array.Empty<GroupInventories.Target>();
                        UserId = null; lastRoot = null; Phase = "waiting for login";
                    }
                    else if (Phase == "not started") Phase = "waiting for login";
                    await Task.Delay(2000).ConfigureAwait(false);
                    continue;
                }
                if (!ReferenceEquals(root, lastRoot))
                {
                    lastRoot = root;
                    await BuildAsync(engine, root).ConfigureAwait(false);
                }
                else
                {
                    await MaybeRefreshAsync(engine, root).ConfigureAwait(false);
                    LinkCrawler.Kick(engine);          // starts a background pass only if Linked is on and something is due
                }
                await Task.Delay(2000).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Phase = "failed";
            ResoniteMod.Error("[InventorySearch] indexer crashed: " + ex);
        }
    }

    // ------------------------------------------------------------------ build

    private static async Task BuildAsync(Engine engine, RecordDirectory root)
    {
        var total = Stopwatch.StartNew();
        string me = root.OwnerId;
        UserId = me;
        _parts.Clear();
        RecordRegistry.Clear();
        Targets = Array.Empty<GroupInventories.Target>();
        bool others = InventorySearchMod.Get(InventorySearchMod.IncludeCachedOtherOwnersKey, true);
        bool refresh = InventorySearchMod.Get(InventorySearchMod.RefreshFromCloudKey, true);
        Log($"building index for {me}");

        LocalDbProbe.Start();

        // Phase 0: the disk cache — everything (your items, groups, cached records) is searchable immediately.
        bool restored = LoadDiskCache(me);

        // Local Storage is just a folder on disk (no network), so index it right away: its Record objects are needed to spawn.
        foreach (var t in GroupInventories.Discover().Where(t => t.IsLocal))
            await IndexSharedAsync(engine, t).ConfigureAwait(false);

        // With the index already usable from the cache, there is no hurry: let the game finish its own start-up
        // (world assets, session list, contacts) before we touch the network or the engine's database.
        int holdSeconds = InventorySearchMod.Get(InventorySearchMod.StartupRefreshDelaySecondsKey, 60);
        if (restored && holdSeconds > 0)
        {
            Phase = "ready";
            Log($"index restored from the disk cache; holding the refresh for {holdSeconds}s so game start-up isn't competed with");
            await Task.Delay(TimeSpan.FromSeconds(holdSeconds)).ConfigureAwait(false);
        }

        // Phase 1: the engine's local record DB, zero network.
        Phase = "local database";
        try
        {
            var db = await LocalDbBootstrap.LoadAsync(me, others, GroupIds()).ConfigureAwait(false);
            _parts.SetDb(db.Entries);
            Log($"LocalDB: {db.TotalRecordsInDb} records total (count {db.CountMs} ms); own inventory {db.OwnRecords} in {db.OwnMs} ms; "
              + (db.OtherSkipped ? "other owners SKIPPED (too large)" : $"other owners {db.OtherRecords} in {db.OtherMs} ms"));
            Publish("local database");
        }
        catch (Exception ex) { Log("LocalDB phase failed: " + ex.Message); }

        // Phase 2 (optional): walk the inventory tree from the local cache (no network).
        // Off by default: measured 8.5 s for the same records the bulk DB read returns in ~1 s.
        if (InventorySearchMod.Get(InventorySearchMod.WalkCacheFirstKey, false))
        try
        {
            Phase = "tree (cache)";
            var r = await PersonalIndexer.WalkAsync(root, cloudRefresh: false, Concurrency, CancellationToken.None).ConfigureAwait(false);
            _parts.SetTree(r.Entries);
            Log($"tree walk (cache only): {r.Folders} folders, {r.Entries.Count} entries, {r.LinkSeeds.Count} link seeds, "
              + $"{r.CacheHits} cache loads, {r.Failures} failures, {r.Elapsed.TotalMilliseconds:F0} ms");
            Publish("tree (cache)");
        }
        catch (Exception ex) { Log("tree (cache) phase failed: " + ex.Message); }

        // Phase 3: refresh your own inventory from the cloud.
        if (refresh) await RefreshTreeAsync(root).ConfigureAwait(false);

        // Phase 4: group inventories and Local Storage.
        await RefreshGroupsAsync(engine).ConfigureAwait(false);

        Phase = "ready";
        Log($"ready in {total.Elapsed.TotalSeconds:F1}s: {_index.Count} entries ({_index.PersonalCount} personal, {_index.SharedCount} group/local, {_index.LinkedCount} linked)");
        await SaveNowAsync().ConfigureAwait(false);
    }

    private static bool LoadDiskCache(string me)
    {
        if (!InventorySearchMod.Get(InventorySearchMod.UseDiskCacheKey, true)) return false;
        var sw = Stopwatch.StartNew();
        var cache = IndexCache.TryLoad(me);
        if (cache == null) { Log("no usable disk cache (first run, or it was outdated); building from scratch"); return false; }

        _parts.Restore(cache.ToParts());
        var targets = cache.Targets.Select(t => new GroupInventories.Target(t.OwnerId, t.Name, t.IsLocal)).ToList();
        if (!GroupInventories.LocalStorageInstalled())
        {
            // the Local Storage mod was removed since the cache was written: forget its items
            targets.RemoveAll(t => t.IsLocal);
            _parts.RetainGroups(targets.Select(t => t.OwnerId));
        }
        Targets = targets;
        Publish("disk cache");
        Log($"restored {_index.Count} entries from the disk cache (saved {(DateTime.UtcNow - cache.SavedUtc).TotalMinutes:F0} min ago) in {sw.ElapsedMilliseconds} ms");
        return true;
    }

    private static HashSet<string> GroupIds()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try { foreach (var t in GroupInventories.Discover()) if (!t.IsLocal) set.Add(t.OwnerId); } catch { }
        return set;
    }

    // ------------------------------------------------------------------ refreshing parts

    private static async Task RefreshTreeAsync(RecordDirectory root)
    {
        Phase = "tree (cloud refresh)";
        try
        {
            var r = InventorySearchMod.Get(InventorySearchMod.UseEngineWalkerKey, false)
                ? await PersonalIndexer.WalkAsync(root, cloudRefresh: true, Concurrency, CancellationToken.None).ConfigureAwait(false)
                : await CloudWalker.WalkAsync(Engine.Current, root.OwnerId, "Inventory", Concurrency, Source.Personal, CancellationToken.None).ConfigureAwait(false);
            if (IndexParts.LooksLikeAFailedRefresh(r.Entries.Count, _parts.TreeCount, r.Failures))
            {
                Log($"tree refresh looked failed ({r.Entries.Count} entries vs {_parts.TreeCount} cached, {r.Failures} failures); keeping the cached items");
                return;
            }
            _parts.SetTree(r.Entries);
            _lastTreeRefreshUtc = DateTime.UtcNow;
            Log($"tree walk (cloud refresh): {r.Folders} folders, {r.Entries.Count} entries, {r.LinkSeeds.Count} link seeds, "
              + $"{r.CloudLoads} cloud requests, {r.Failures} failures, {r.Elapsed.TotalMilliseconds:F0} ms");
            Publish("tree (cloud refresh)");
        }
        catch (Exception ex) { Log("cloud refresh failed: " + ex.Message); }
    }

    private static async Task RefreshGroupsAsync(Engine engine)
    {
        Phase = "group inventories";
        try
        {
            // memberships load shortly after login; give them a moment before concluding "no groups"
            var targets = GroupInventories.Discover();
            for (int i = 0; i < 10 && targets.Count(t => !t.IsLocal) == 0 && Targets.Count(t => !t.IsLocal) == 0; i++)
            {
                await Task.Delay(1500).ConfigureAwait(false);
                targets = GroupInventories.Discover();
            }
            // Only trust an empty membership list if nothing was cached; otherwise keep cached groups until we know better.
            if (targets.Count(t => !t.IsLocal) == 0 && Targets.Count(t => !t.IsLocal) > 0)
            {
                Log("no group memberships reported; keeping the cached groups");
                targets = Targets.ToList();
            }
            Targets = targets;
            _parts.RetainGroups(targets.Select(t => t.OwnerId));
            Log($"scope targets: {(targets.Count == 0 ? "none" : string.Join(", ", targets.Select(t => t.Name + (t.IsLocal ? " [local]" : ""))))}");

            foreach (var t in targets) await IndexSharedAsync(engine, t).ConfigureAwait(false);
            _lastGroupRefreshUtc = DateTime.UtcNow;
        }
        catch (Exception ex) { Log("group phase failed: " + ex.Message); }
    }

    private static async Task IndexSharedAsync(Engine engine, GroupInventories.Target t)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            PersonalIndexer.WalkResult r;
            if (t.IsLocal || InventorySearchMod.Get(InventorySearchMod.UseEngineWalkerKey, false))
            {
                // Local Storage is a patched RecordDirectory (disk, no network); its Records are needed to spawn, so remember them.
                var groupRoot = new RecordDirectory(t.OwnerId, "Inventory", engine);
                Action<Record>? remember = t.IsLocal ? RecordRegistry.Add : null;
                r = await PersonalIndexer.WalkAsync(groupRoot, cloudRefresh: true, Concurrency, CancellationToken.None,
                                                    Source.Group, remember).ConfigureAwait(false);
            }
            else
            {
                r = await CloudWalker.WalkAsync(engine, t.OwnerId, "Inventory", Concurrency, Source.Group, CancellationToken.None).ConfigureAwait(false);
            }
            int old = _parts.GroupEntryCount(t.OwnerId);
            if (IndexParts.LooksLikeAFailedRefresh(r.Entries.Count, old, r.Failures))
            {
                Log($"{t.Name}: refresh looked failed ({r.Entries.Count} entries vs {old} cached, {r.Failures} failures); keeping the cached items");
                return;
            }
            _parts.SetGroup(t.OwnerId, r.Entries);
            Log($"{t.Name}: {r.Folders} folders, {r.Entries.Count} entries, {r.CloudLoads} cloud requests, {r.Failures} failures, {sw.ElapsedMilliseconds} ms");
            Publish(t.Name);
        }
        catch (Exception ex) { Log($"{t.Name} failed: {ex.Message}"); }
    }

    /// <summary>Periodic background refresh while the game runs (RefreshMinutes setting; 0 turns it off).</summary>
    private static async Task MaybeRefreshAsync(Engine engine, RecordDirectory root)
    {
        double minutes = InventorySearchMod.Get(InventorySearchMod.RefreshMinutesKey, 20);
        if (minutes <= 0 || Phase != "ready") return;
        var now = DateTime.UtcNow;
        bool tree = now - _lastTreeRefreshUtc >= TimeSpan.FromMinutes(minutes);
        bool groups = now - _lastGroupRefreshUtc >= TimeSpan.FromMinutes(Math.Max(60, minutes * 3));   // groups are heavier, refresh less often
        if (!tree && !groups) return;

        Log($"background refresh ({(tree ? "inventory" : "")}{(tree && groups ? " + " : "")}{(groups ? "groups" : "")})");
        if (tree) await RefreshTreeAsync(root).ConfigureAwait(false);
        if (groups) await RefreshGroupsAsync(engine).ConfigureAwait(false);
        Phase = "ready";
        ScheduleSave(2);
    }

    // ------------------------------------------------------------------ publishing and saving

    private static void Publish(string label)
    {
        var sw = Stopwatch.StartNew();
        var idx = _parts.Build();
        _index = idx;
        try { Published?.Invoke(); } catch (Exception ex) { ResoniteMod.Warn("[InventorySearch] Published handler failed: " + ex.Message); }
        Log($"published index [{label}]: {idx.Count} entries ({idx.PersonalCount} personal, {idx.SharedCount} group/local) built in {sw.ElapsedMilliseconds} ms");
    }

    private static void ScheduleRepublish()
    {
        int v = Interlocked.Increment(ref _republishVersion);
        _ = Task.Run(async () =>
        {
            await Task.Delay(300).ConfigureAwait(false);           // many changes at once (deleting a folder) -> one rebuild
            if (v != Volatile.Read(ref _republishVersion)) return;
            Publish("change");
        });
    }

    private static void ScheduleSave(int seconds)
    {
        if (!InventorySearchMod.Get(InventorySearchMod.UseDiskCacheKey, true)) return;
        int v = Interlocked.Increment(ref _saveVersion);
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
            if (v != Volatile.Read(ref _saveVersion)) return;
            await SaveNowAsync().ConfigureAwait(false);
        });
    }

    private static Task SaveNowAsync()
    {
        if (!InventorySearchMod.Get(InventorySearchMod.UseDiskCacheKey, true) || UserId == null) return Task.CompletedTask;
        return Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var targets = Targets.Select(t => new SharedTarget(t.OwnerId, t.Name, t.IsLocal));
            var file = IndexCacheFile.From(UserId!, targets, _parts.Snapshot());
            long bytes = IndexCache.Save(file);
            if (bytes >= 0)
            {
                int linked = file.Linked.Values.Sum(l => l.Count);
                Log($"saved the disk cache: {file.Tree.Count + file.Db.Count + file.Groups.Values.Sum(g => g.Count) + linked} entries ({linked} crawled from {file.Linked.Count} links), {bytes / 1024} KB, {sw.ElapsedMilliseconds} ms");
            }
        });
    }

    // ------------------------------------------------------------------ live changes (called from ChangeHooks, on the world thread)

    private static bool IsSharedOwner(string owner)
    {
        foreach (var t in Targets) if (string.Equals(t.OwnerId, owner, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static void RecordAdded(Record? rec)
    {
        try
        {
            if (rec == null || UserId == null) return;
            var source = string.Equals(rec.OwnerId, UserId, StringComparison.OrdinalIgnoreCase) ? Source.Personal : Source.Group;
            var entry = RecordMapper.ToEntry(rec, source, null, 0);
            if (entry == null || !_parts.Add(entry, UserId, IsSharedOwner)) return;
            if (rec.OwnerId == GroupInventories.LocalOwner) RecordRegistry.Add(rec);
            ScheduleRepublish(); ScheduleSave(20);
        }
        catch (Exception ex) { ResoniteMod.Warn("[InventorySearch] could not index a new item: " + ex.Message); }
    }

    public static void RecordRemoved(Record? rec)
    {
        try
        {
            if (rec == null || UserId == null) return;
            if (_parts.RemoveRecord(rec.OwnerId, rec.RecordId) > 0) { ScheduleRepublish(); ScheduleSave(20); }
        }
        catch (Exception ex) { ResoniteMod.Warn("[InventorySearch] could not drop a deleted item: " + ex.Message); }
    }

    public static void FolderRemoved(RecordDirectory? dir)
    {
        try
        {
            if (dir == null || UserId == null) return;
            int n = 0;
            var entry = dir.EntryRecord;
            if (entry != null) n += _parts.RemoveRecord(entry.OwnerId, entry.RecordId);          // the folder (or link) itself
            if (!dir.IsLink && dir.OwnerId != null && dir.Path != null) n += _parts.RemoveFolder(dir.OwnerId, dir.Path);   // everything inside
            if (n > 0) { ScheduleRepublish(); ScheduleSave(20); }
        }
        catch (Exception ex) { ResoniteMod.Warn("[InventorySearch] could not drop a deleted folder: " + ex.Message); }
    }
}
