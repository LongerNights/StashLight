namespace StashLight.Core;

/// <summary>Immutable snapshot: entries plus precomputed lowercase/normalized search fields.</summary>
public sealed class SearchIndex
{
    public static readonly SearchIndex Empty = new(Array.Empty<IndexEntry>());

    public IndexEntry[] Entries { get; }
    internal string[] Name { get; }        // normalized (rich text stripped, lowercase)
    internal string[] PathN { get; }
    internal string[][] TagsN { get; }
    internal string[] OwnerN { get; }
    internal int[][] Grams { get; }
    /// <summary>Entries with equal (owner, path, name, type) share a group id (repeated saves of the same item).</summary>
    internal int[] GroupId { get; }
    internal int DupGroupCount { get; }

    public int Count => Entries.Length;
    public int PersonalCount { get; }
    public int SharedCount { get; }                       // group + local-storage entries
    public int LinkedCount => Count - PersonalCount - SharedCount;

    private static int Rank(IndexEntry e) => (int)e.Source * 1000 + e.LinkDepth;

    public SearchIndex(IEnumerable<IndexEntry> entries)
    {
        // de-duplicate by (owner, record id): personal beats linked, shallower beats deeper
        var best = new Dictionary<(string, string), IndexEntry>();
        foreach (var e in entries)
        {
            var key = (e.OwnerId.ToLowerInvariant(), e.RecordId);   // owner ids compare case-insensitively everywhere else
            if (!best.TryGetValue(key, out var cur) || Rank(e) < Rank(cur))
                best[key] = e;
        }
        Entries = best.Values.ToArray();
        int n2 = Entries.Length;
        Name = new string[n2]; PathN = new string[n2]; TagsN = new string[n2][]; OwnerN = new string[n2]; Grams = new int[n2][]; GroupId = new int[n2];
        var groups = new Dictionary<(string, string, string, string), int>();
        int personal = 0, shared = 0;
        for (int i = 0; i < n2; i++)
        {
            var e = Entries[i];
            Name[i] = Text.Normalize(e.NameRaw);
            PathN[i] = Text.Normalize(e.Path);
            TagsN[i] = e.Tags.Select(t => t.ToLowerInvariant()).ToArray();
            OwnerN[i] = e.OwnerId.ToLowerInvariant();
            Grams[i] = Text.Trigrams(Name[i]);
            var gk = (OwnerN[i], PathN[i], Name[i], e.RecordType);
            if (!groups.TryGetValue(gk, out int gid)) groups[gk] = gid = groups.Count;
            GroupId[i] = gid;
            if (e.Source == Source.Personal) personal++; else if (e.Source == Source.Group) shared++;
        }
        PersonalCount = personal;
        SharedCount = shared;
        DupGroupCount = groups.Count;
    }
}
