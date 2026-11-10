using System.Diagnostics;
using FrooxEngine;
using FrooxEngine.Store;
using StashLight.Core;

namespace StashLight;

/// <summary>
/// Walks the user's own inventory tree (RecordDirectory) on the thread pool. Links are NOT followed here;
/// each link subdirectory is returned as a seed for the (future) LinkCrawler.
/// Safe off the world thread: RecordDirectory replaces its lists wholesale when loading, so a snapshot
/// of Subdirectories/Records is stable.
/// </summary>
internal static class PersonalIndexer
{
    public sealed record WalkResult(List<IndexEntry> Entries, List<Record> LinkSeeds, int Folders, int Failures,
                                    int CacheHits, int CloudLoads, TimeSpan Elapsed);

    /// <param name="cloudRefresh">false = local cache only (no network); folders with no cache are left unloaded.</param>
    public static async Task<WalkResult> WalkAsync(RecordDirectory root, bool cloudRefresh, int concurrency, CancellationToken ct,
                                                    Source source = Source.Personal, Action<Record>? onRecord = null)
    {
        var sw = Stopwatch.StartNew();
        var entries = new List<IndexEntry>();
        var seeds = new List<Record>();
        int folders = 0, failures = 0, cacheHits = 0, cloudLoads = 0;
        var gate = new SemaphoreSlim(Math.Max(1, concurrency));
        var gather = new object();

        async Task Visit(RecordDirectory dir)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    // Going to the cloud anyway? Skip the per-folder cache query (≈85 ms each in LiteDB).
                    if (!cloudRefresh && dir.CurrentLoadState == RecordDirectory.LoadState.NotLoaded)
                    {
                        if (await dir.TryLocalCacheLoad().ConfigureAwait(false)) Interlocked.Increment(ref cacheHits);
                    }
                    if (cloudRefresh && dir.CurrentLoadState != RecordDirectory.LoadState.FullyLoaded)
                    {
                        await dir.EnsureFullyLoaded().ConfigureAwait(false);
                        Interlocked.Increment(ref cloudLoads);
                    }
                }
                finally { gate.Release(); }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                ResoniteModLoader.ResoniteMod.Warn($"[StashLight] load failed for '{dir.Path}': {ex.Message}");
                return;
            }

            var subs = Snapshot(() => dir.Subdirectories.ToArray());
            var recs = Snapshot(() => dir.Records.ToArray());

            lock (gather)
            {
                folders++;
                foreach (var r in recs)
                {
                    var e = RecordMapper.ToEntry(r, source, null, 0);
                    if (e != null) { entries.Add(e); onRecord?.Invoke(r); }
                }
                foreach (var s in subs)
                {
                    var rec = s.EntryRecord;               // DirectoryRecord, or LinkRecord for links
                    if (rec == null) continue;
                    var e = RecordMapper.ToEntry(rec, source, null, 0);
                    if (e != null) onRecord?.Invoke(rec);
                    if (e != null) entries.Add(e);
                    if (s.IsLink) seeds.Add(rec);
                }
            }

            var children = subs.Where(s => !s.IsLink).Select(Visit).ToArray();
            await Task.WhenAll(children).ConfigureAwait(false);
        }

        await Visit(root).ConfigureAwait(false);
        return new WalkResult(entries, seeds, folders, failures, cacheHits, cloudLoads, sw.Elapsed);
    }

    /// <summary>Retries if an in-place mutation (AddItem/AddSubdirectory on the world thread) races the copy.</summary>
    private static T[] Snapshot<T>(Func<T[]> copy)
    {
        for (int i = 0; i < 5; i++)
        {
            try { return copy(); }
            catch (InvalidOperationException) { Thread.Yield(); }
            catch (ArgumentException) { Thread.Yield(); }
        }
        return Array.Empty<T>();
    }
}
