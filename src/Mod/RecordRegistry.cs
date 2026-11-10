using System.Collections.Concurrent;
using FrooxEngine.Store;

namespace StashLight;

/// <summary>
/// Keeps the Record objects of inventories that are NOT fetchable from LocalDB or the cloud (the Local Storage
/// mod stores records on disk). Spawning needs the Record, so it looks here first.
/// </summary>
internal static class RecordRegistry
{
    private static readonly ConcurrentDictionary<(string, string), Record> _records = new();

    public static void Add(Record r)
    {
        if (r != null && r.OwnerId != null && r.RecordId != null) _records[(r.OwnerId, r.RecordId)] = r;
    }

    public static bool TryGet(string ownerId, string recordId, out Record? record)
        => _records.TryGetValue((ownerId, recordId), out record);

    public static void Clear() => _records.Clear();
}
