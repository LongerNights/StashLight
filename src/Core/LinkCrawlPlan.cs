namespace InventorySearch.Core;

/// <summary>One of your own links to someone else's folder. Key = "&lt;owner&gt;/&lt;link record id&gt;" (stable across sessions).</summary>
public sealed record LinkSeed(string Key, string Name, string TargetUri);

/// <summary>What the last crawl of a link found. Persisted in the disk cache so later launches crawl nothing unless stale.</summary>
public sealed record CrawlInfo(
    string Key,
    string Name,
    DateTime LastAttemptUtc,
    int Folders,
    int Entries,
    bool Truncated,     // hit the per-link folder cap
    bool Unavailable);  // the link target could not be resolved (deleted, or private)

public sealed record CrawlLimits(
    int MaxDepth = 3,                 // link hops: your link = 1, a link found inside it = 2, ...
    int MaxFoldersPerSeed = 500,
    int MaxFoldersTotal = 5000,       // per crawl pass
    double RequestsPerSecond = 5,
    TimeSpan? Ttl = null,             // re-crawl a link after this long (default 24 h)
    TimeSpan? UnavailableRetry = null)  // retry an unresolvable link after this long (default 24 h)
{
    public TimeSpan EffectiveTtl => Ttl ?? TimeSpan.FromHours(24);
    public TimeSpan EffectiveUnavailableRetry => UnavailableRetry ?? TimeSpan.FromHours(24);
}

/// <summary>Decides which links to crawl, in what order, and which crawled results to keep. Pure, so it is unit tested.</summary>
public static class CrawlPlanner
{
    /// <summary>True if the user switched this link off: by its name, its key, or its link record id.</summary>
    public static bool IsDisabled(LinkSeed seed, ISet<string> disabled)
    {
        if (disabled.Count == 0) return false;
        if (disabled.Contains(seed.Key) || disabled.Contains(seed.Name)) return true;
        int slash = seed.Key.IndexOf('/');
        return slash >= 0 && disabled.Contains(seed.Key[(slash + 1)..]);
    }

    /// <summary>Parses the DisabledLinks setting: names or ids separated by commas or semicolons (case-insensitive).</summary>
    public static HashSet<string> ParseDisabled(string? setting)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(setting)) return set;
        foreach (var part in setting.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (part.Length > 0) set.Add(part);
        return set;
    }

    /// <summary>
    /// The links that need crawling now: never crawled first (in the order given), then stale ones, oldest first.
    /// Skips disabled links, links crawled within the TTL, unavailable links still inside their retry delay, and links whose
    /// target another link already covers (two links to the same folder are crawled once).
    /// </summary>
    public static List<LinkSeed> Plan(IEnumerable<LinkSeed> seeds, IReadOnlyDictionary<string, CrawlInfo> state,
                                      ISet<string> disabled, DateTime nowUtc, CrawlLimits limits)
    {
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fresh = new List<LinkSeed>();
        var stale = new List<(LinkSeed seed, DateTime last)>();
        foreach (var s in seeds)
        {
            if (IsDisabled(s, disabled)) continue;
            if (!seenTargets.Add(s.TargetUri)) continue;
            if (!state.TryGetValue(s.Key, out var info)) { fresh.Add(s); continue; }
            var due = info.Unavailable ? limits.EffectiveUnavailableRetry : limits.EffectiveTtl;
            if (nowUtc - info.LastAttemptUtc >= due) stale.Add((s, info.LastAttemptUtc));
        }
        stale.Sort((a, b) => a.last.CompareTo(b.last));
        fresh.AddRange(stale.Select(x => x.seed));
        return fresh;
    }

    /// <summary>Keys whose crawled results should stay in the index: links that still exist and are not switched off.</summary>
    public static HashSet<string> KeepKeys(IEnumerable<LinkSeed> seeds, ISet<string> disabled)
        => new(seeds.Where(s => !IsDisabled(s, disabled)).Select(s => s.Key), StringComparer.OrdinalIgnoreCase);

    /// <summary>The seeds of your own links, derived from the index: your entries of category "link" that point somewhere.</summary>
    public static List<LinkSeed> SeedsFrom(IEnumerable<IndexEntry> personalEntries)
        => personalEntries
            .Where(e => e.Source == Source.Personal && e.Category == "link" && !string.IsNullOrEmpty(e.AssetUri))
            .Select(e => new LinkSeed(e.OwnerId + "/" + e.RecordId, Text.StripRichText(e.NameRaw), e.AssetUri!))
            .ToList();
}
