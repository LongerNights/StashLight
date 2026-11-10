using System.Diagnostics;
using FrooxEngine;
using StashLight.Core;
using ResoniteModLoader;
using Record = FrooxEngine.Store.Record;

namespace StashLight;

/// <summary>
/// Lists an inventory tree straight from the cloud API, one request per folder, WITHOUT going through RecordDirectory.
/// Why: RecordDirectory.EnsureFullyLoaded() also makes the engine write every record it loads into its local database
/// (a lookup + update per record, all through the database's single worker queue). Walking ~520 folders that way made the
/// engine do thousands of database operations at every start, in the same queue its own asset lookups use. This walker only
/// reads, so the engine's database is left alone. (Local Storage still uses RecordDirectory: it is a patched disk read.)
/// </summary>
internal static class CloudWalker
{
    public static async Task<PersonalIndexer.WalkResult> WalkAsync(Engine engine, string ownerId, string rootPath, int concurrency,
                                                                   Source source, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var entries = new List<IndexEntry>();
        var seeds = new List<Record>();
        int folders = 0, failures = 0, requests = 0;
        var gate = new SemaphoreSlim(Math.Max(1, concurrency));
        var gather = new object();

        async Task Visit(string path)
        {
            ct.ThrowIfCancellationRequested();
            List<Record>? list = null;
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                for (int attempt = 0; attempt < 2 && list == null; attempt++)
                {
                    Interlocked.Increment(ref requests);
                    var res = await engine.Cloud.Records.GetRecords<Record>(ownerId, null, path).ConfigureAwait(false);
                    if (res.IsOK && res.Entity != null) list = res.Entity;
                    else if (attempt == 0) await Task.Delay(500, ct).ConfigureAwait(false);   // one polite retry
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { ResoniteMod.Warn($"[StashLight] listing '{path}' failed: {ex.Message}"); }
            finally { gate.Release(); }

            if (list == null) { Interlocked.Increment(ref failures); return; }

            var children = new List<string>();
            lock (gather)
            {
                folders++;
                foreach (var r in list)
                {
                    var e = RecordMapper.ToEntry(r, source, null, 0);
                    if (e != null) entries.Add(e);
                    if (r.IsDeleted) continue;
                    // same child-path rule RecordDirectory uses for a directory record
                    if (r.RecordType == "directory") children.Add(r.Path + "\\" + r.Name);
                    else if (r.RecordType == "link") seeds.Add(r);      // links are noted, not followed (that is M4)
                }
            }
            await Task.WhenAll(children.Select(Visit)).ConfigureAwait(false);
        }

        await Visit(rootPath).ConfigureAwait(false);
        return new PersonalIndexer.WalkResult(entries, seeds, folders, failures, CacheHits: 0, CloudLoads: requests, sw.Elapsed);
    }
}
