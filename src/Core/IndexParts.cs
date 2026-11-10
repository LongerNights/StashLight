namespace StashLight.Core;

/// <summary>An owner id with a display name, e.g. a group, or the Local Storage mod's inventory.</summary>
public sealed record SharedTarget(string OwnerId, string Name, bool IsLocal);

/// <summary>Immutable copy of every part, used to write the disk cache.</summary>
public sealed record PartsSnapshot(
    List<IndexEntry> Tree,
    List<IndexEntry> Db,
    Dictionary<string, List<IndexEntry>> Groups,
    Dictionary<string, List<IndexEntry>>? Linked = null,      // crawled linked content, one list per link (key = link seed key)
    Dictionary<string, CrawlInfo>? Crawl = null);

/// <summary>
/// The index is assembled from independent parts so each source can refresh without touching the others:
/// your own inventory tree, the engine's cached-records database, and one part per group / Local Storage owner.
/// Thread-safe (a lock guards every operation). Engine-free so the routing and change logic is unit tested.
/// </summary>
public sealed class IndexParts
{
    private readonly object _lock = new();
    private List<IndexEntry> _tree = new();
    private List<IndexEntry> _db = new();
    private readonly Dictionary<string, List<IndexEntry>> _groups = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<IndexEntry>> _linked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CrawlInfo> _crawl = new(StringComparer.OrdinalIgnoreCase);

    // ---- replace whole parts (a refresh finished) ----
    public void SetTree(List<IndexEntry> entries) { lock (_lock) _tree = entries; }
    public void SetDb(List<IndexEntry> entries) { lock (_lock) _db = entries; }
    public void SetGroup(string ownerId, List<IndexEntry> entries) { lock (_lock) _groups[ownerId] = entries; }

    public void Clear()
    {
        lock (_lock) { _tree = new(); _db = new(); _groups.Clear(); _linked.Clear(); _crawl.Clear(); }
    }

    /// <summary>Forget groups that are no longer in <paramref name="ownerIds"/> (left a group, uninstalled Local Storage).</summary>
    public int RetainGroups(IEnumerable<string> ownerIds)
    {
        var keep = new HashSet<string>(ownerIds, StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            int removed = 0;
            foreach (var k in _groups.Keys.Where(k => !keep.Contains(k)).ToList()) { removed += _groups[k].Count; _groups.Remove(k); }
            return removed;
        }
    }

    // ---- crawled linked content (one part per link of yours) ----

    /// <summary>Stores what a crawl of one link found (replacing its previous result) and remembers the crawl's outcome.</summary>
    public void SetLinked(string key, List<IndexEntry> entries, CrawlInfo info)
    {
        lock (_lock) { _linked[key] = entries; _crawl[key] = info; }
    }

    /// <summary>Remembers an outcome without results (e.g. the link target was unavailable).</summary>
    public void SetCrawlInfo(CrawlInfo info) { lock (_lock) _crawl[info.Key] = info; }

    public bool RemoveLinked(string key)
    {
        lock (_lock) { bool had = _linked.Remove(key); had |= _crawl.Remove(key); return had; }
    }

    /// <summary>Drops linked results (and crawl records) of links that were deleted or switched off.</summary>
    public int RetainLinked(IEnumerable<string> keys)
    {
        var keep = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            int removed = 0;
            foreach (var k in _linked.Keys.Where(k => !keep.Contains(k)).ToList()) { removed += _linked[k].Count; _linked.Remove(k); }
            foreach (var k in _crawl.Keys.Where(k => !keep.Contains(k)).ToList()) _crawl.Remove(k);
            return removed;
        }
    }

    /// <summary>
    /// Forgets when links were last crawled (one link, or all when <paramref name="key"/> is null) so the planner treats them as
    /// never crawled and crawls them again. The items already found stay searchable until the new crawl replaces them.
    /// </summary>
    public int ForgetCrawl(string? key = null)
    {
        lock (_lock)
        {
            if (key == null) { int n = _crawl.Count; _crawl.Clear(); return n; }
            return _crawl.Remove(key) ? 1 : 0;
        }
    }

    public IReadOnlyDictionary<string, CrawlInfo> CrawlState()
    {
        lock (_lock) return new Dictionary<string, CrawlInfo>(_crawl, StringComparer.OrdinalIgnoreCase);
    }

    public int LinkedEntryCount { get { lock (_lock) return _linked.Values.Sum(l => l.Count); } }

    public bool HasGroup(string ownerId) { lock (_lock) return _groups.ContainsKey(ownerId); }
    public int TreeCount { get { lock (_lock) return _tree.Count; } }
    public int GroupEntryCount(string ownerId) { lock (_lock) return _groups.TryGetValue(ownerId, out var g) ? g.Count : 0; }

    /// <summary>
    /// A refresh that hit errors (network down, a folder failed to load) can come back nearly empty. Replacing good cached
    /// content with that would silently lose items, so a result with failures that is less than half of what we already
    /// have is rejected and the existing part is kept.
    /// </summary>
    public static bool LooksLikeAFailedRefresh(int newCount, int oldCount, int failures)
        => failures > 0 && oldCount > 0 && newCount * 2 < oldCount;

    // ---- incremental changes (an item was saved / deleted while the game runs) ----

    /// <summary>
    /// Adds (or replaces) an entry in the right part: your own inventory, or the group it belongs to.
    /// Returns false when the owner is neither you nor a known shared owner (nothing to index it under).
    /// </summary>
    public bool Add(IndexEntry entry, string myUserId, Func<string, bool> isSharedOwner)
    {
        lock (_lock)
        {
            List<IndexEntry>? target;
            if (string.Equals(entry.OwnerId, myUserId, StringComparison.OrdinalIgnoreCase)) target = _tree;
            else if (isSharedOwner(entry.OwnerId))
            {
                if (!_groups.TryGetValue(entry.OwnerId, out target)) _groups[entry.OwnerId] = target = new List<IndexEntry>();
            }
            else return false;

            target.RemoveAll(e => SameRecord(e, entry));
            target.Add(entry);
            // a stale cached copy of the same record must not outrank or duplicate it
            _db.RemoveAll(e => SameRecord(e, entry));
            return true;
        }
    }

    /// <summary>Removes a record from every part. Returns how many entries were removed.</summary>
    public int RemoveRecord(string ownerId, string recordId)
    {
        lock (_lock)
        {
            int n = _tree.RemoveAll(e => Is(e, ownerId, recordId)) + _db.RemoveAll(e => Is(e, ownerId, recordId));
            foreach (var g in _groups.Values) n += g.RemoveAll(e => Is(e, ownerId, recordId));
            foreach (var l in _linked.Values) n += l.RemoveAll(e => Is(e, ownerId, recordId));
            // if that record was one of your links, everything crawled through it goes too
            string linkKey = ownerId + "/" + recordId;
            if (_linked.TryGetValue(linkKey, out var through)) { n += through.Count; _linked.Remove(linkKey); }
            _crawl.Remove(linkKey);
            return n;
        }
    }

    /// <summary>Removes everything inside a folder (and its subfolders), e.g. after a folder was deleted.</summary>
    public int RemoveFolder(string ownerId, string folderPath)
    {
        string prefix = folderPath.TrimEnd('\\') + "\\";
        bool Inside(IndexEntry e) =>
            string.Equals(e.OwnerId, ownerId, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(e.Path, folderPath, StringComparison.OrdinalIgnoreCase)
                || e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        lock (_lock)
        {
            int n = _tree.RemoveAll(Inside) + _db.RemoveAll(Inside);
            foreach (var g in _groups.Values) n += g.RemoveAll(Inside);
            foreach (var l in _linked.Values) n += l.RemoveAll(Inside);
            return n;
        }
    }

    private static bool Is(IndexEntry e, string owner, string id)
        => string.Equals(e.OwnerId, owner, StringComparison.OrdinalIgnoreCase) && e.RecordId == id;
    private static bool SameRecord(IndexEntry a, IndexEntry b) => Is(a, b.OwnerId, b.RecordId);

    // ---- output ----

    /// <summary>Builds the searchable index. Order matters only for ties: your tree first, then groups, then cached records.</summary>
    public SearchIndex Build()
    {
        List<IndexEntry> all;
        lock (_lock)
        {
            all = new List<IndexEntry>(_tree.Count + _db.Count + _groups.Values.Sum(g => g.Count) + _linked.Values.Sum(l => l.Count));
            all.AddRange(_tree);
            foreach (var g in _groups.Values) all.AddRange(g);
            foreach (var l in _linked.Values) all.AddRange(l);
            all.AddRange(_db);
        }
        return new SearchIndex(all);
    }

    /// <summary>
    /// A copy of every part for the disk cache. Entries in the record-DB part that the tree or a group already holds are
    /// left out: they would be stored twice, and the live DB read replaces that part at every start anyway.
    /// </summary>
    public PartsSnapshot Snapshot()
    {
        lock (_lock)
        {
            var held = new HashSet<(string, string)>();
            foreach (var e in _tree) held.Add((e.OwnerId.ToLowerInvariant(), e.RecordId));
            foreach (var g in _groups.Values) foreach (var e in g) held.Add((e.OwnerId.ToLowerInvariant(), e.RecordId));
            foreach (var l in _linked.Values) foreach (var e in l) held.Add((e.OwnerId.ToLowerInvariant(), e.RecordId));
            var db = _db.Where(e => !held.Contains((e.OwnerId.ToLowerInvariant(), e.RecordId))).ToList();
            return new PartsSnapshot(new List<IndexEntry>(_tree), db,
                _groups.ToDictionary(kv => kv.Key, kv => new List<IndexEntry>(kv.Value), StringComparer.OrdinalIgnoreCase),
                _linked.ToDictionary(kv => kv.Key, kv => new List<IndexEntry>(kv.Value), StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, CrawlInfo>(_crawl, StringComparer.OrdinalIgnoreCase));
        }
    }

    public void Restore(PartsSnapshot snap)
    {
        lock (_lock)
        {
            _tree = snap.Tree; _db = snap.Db; _groups.Clear(); _linked.Clear(); _crawl.Clear();
            foreach (var kv in snap.Groups) _groups[kv.Key] = kv.Value;
            if (snap.Linked != null) foreach (var kv in snap.Linked) _linked[kv.Key] = kv.Value;
            if (snap.Crawl != null) foreach (var kv in snap.Crawl) _crawl[kv.Key] = kv.Value;
        }
    }

    public int TotalEntries { get { lock (_lock) return _tree.Count + _db.Count + _groups.Values.Sum(g => g.Count) + _linked.Values.Sum(l => l.Count); } }
}
