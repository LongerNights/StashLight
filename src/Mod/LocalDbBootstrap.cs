using System.Diagnostics;
using FrooxEngine;
using FrooxEngine.Store;
using StashLight.Core;

namespace StashLight;

/// <summary>
/// Reads records straight out of the engine's local LiteDB cache (Engine.LocalDB): zero network.
/// This holds everything the client has ever fetched, including other owners' linked folders you browsed.
/// </summary>
internal static class LocalDbBootstrap
{
    /// <summary>Skip fetching other owners' records if the cache is absurdly large (memory guard).</summary>
    private const int MaxOtherOwnerFetch = 400_000;

    public sealed record Result(
        List<IndexEntry> Entries,
        int TotalRecordsInDb, long CountMs,
        int OwnRecords, long OwnMs,
        int OtherRecords, long OtherMs, bool OtherSkipped, int OwnDropped, int OtherDropped);

    public static async Task<Result> LoadAsync(string myUserId, bool includeOthers, HashSet<string>? groupIds = null)
    {
        var db = Engine.Current.LocalDB;
        var sw = Stopwatch.StartNew();
        int total = await db.GetRecordCount().ConfigureAwait(false);
        long countMs = sw.ElapsedMilliseconds;

        var entries = new List<IndexEntry>();

        sw.Restart();
        var own = await db.FetchRecordsAsync(r => r.OwnerId == myUserId).ConfigureAwait(false) ?? new List<Record>();
        long ownMs = sw.ElapsedMilliseconds;
        int ownKept = 0;
        foreach (var r in own)
        {
            // Only the user's inventory; the same owner id also holds settings/dash workspace/etc records.
            if (r.Path == null || !(r.Path == "Inventory" || r.Path.StartsWith("Inventory\\", StringComparison.OrdinalIgnoreCase))) continue;
            var e = RecordMapper.ToEntry(r, Source.Personal, null, 0);
            if (e != null) { entries.Add(e); ownKept++; }
        }

        int otherKept = 0, otherDropped = 0; long otherMs = 0; bool skipped = false;
        if (includeOthers)
        {
            if (total > MaxOtherOwnerFetch) skipped = true;
            else
            {
                sw.Restart();
                var others = await db.FetchRecordsAsync(r => r.OwnerId != myUserId).ConfigureAwait(false) ?? new List<Record>();
                otherMs = sw.ElapsedMilliseconds;
                foreach (var r in others)
                {
                    // Only records that live in an inventory tree. Skips world/session records (empty Path), settings, etc.
                    if (r.Path == null || !(r.Path == "Inventory" || r.Path.StartsWith("Inventory\\", StringComparison.OrdinalIgnoreCase))) { otherDropped++; continue; }
                    bool mine = groupIds != null && groupIds.Contains(r.OwnerId);   // a group you belong to
                    var e = mine ? RecordMapper.ToEntry(r, Source.Group, null, 0)
                                 : RecordMapper.ToEntry(r, Source.Linked, "cached", 1);
                    if (e != null) { entries.Add(e); otherKept++; }
                }
            }
        }
        return new Result(entries, total, countMs, ownKept, ownMs, otherKept, otherMs, skipped, own.Count - ownKept, otherDropped);
    }
}
