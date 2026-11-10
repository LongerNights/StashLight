namespace StashLight.Core;

/// <summary>Text and setting edits behind the Links panel. Pure, so it is unit tested.</summary>
public static class LinkRows
{
    public static string HumanAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age.TotalMinutes < 1) return "just now";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes} min ago";
        if (age.TotalHours < 48) return $"{(int)age.TotalHours} h ago";
        return $"{(int)age.TotalDays} days ago";
    }

    /// <summary>The info shown on one link's row.</summary>
    public static string Describe(CrawlInfo? info, bool disabled, DateTime nowUtc)
    {
        if (disabled) return "switched off";
        if (info == null) return "not crawled yet";
        string age = HumanAge(nowUtc - info.LastAttemptUtc);
        if (info.Unavailable) return $"unavailable (tried {age})";
        return $"{info.Entries:N0} items · {age}" + (info.Truncated ? " · cut at the limit" : "");
    }

    /// <summary>The one-line summary at the top of the panel.</summary>
    public static string Summary(IReadOnlyCollection<LinkSeed> seeds, IReadOnlyDictionary<string, CrawlInfo> state,
                                 ISet<string> disabled, bool linkedOn, bool crawling)
    {
        int off = 0, crawled = 0, unavailable = 0, items = 0;
        foreach (var s in seeds)
        {
            if (CrawlPlanner.IsDisabled(s, disabled)) { off++; continue; }
            if (!state.TryGetValue(s.Key, out var info)) continue;
            if (info.Unavailable) { unavailable++; continue; }
            crawled++; items += info.Entries;
        }
        string text = $"{seeds.Count} links · {crawled} crawled ({items:N0} items)";
        if (unavailable > 0) text += $" · {unavailable} unavailable";
        if (off > 0) text += $" · {off} switched off";
        if (!linkedOn) text += " · Linked is Off, so nothing is being crawled";
        else if (crawling) text += " · crawling now";
        return text;
    }

    /// <summary>
    /// Returns the new DisabledLinks setting after switching <paramref name="seed"/> off or on. Other entries (and their
    /// spelling) are kept; entries that name this link (by name, key or record id) are removed; switching off adds its name,
    /// or its record id when another link shares the name.
    /// </summary>
    public static string ToggleDisabled(string? setting, LinkSeed seed, IReadOnlyCollection<LinkSeed> all, bool disable)
    {
        string id = seed.Key.Contains('/') ? seed.Key[(seed.Key.IndexOf('/') + 1)..] : seed.Key;
        var tokens = (setting ?? "")
            .Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !t.Equals(seed.Name, StringComparison.OrdinalIgnoreCase)
                        && !t.Equals(seed.Key, StringComparison.OrdinalIgnoreCase)
                        && !t.Equals(id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (disable)
        {
            bool nameIsShared = all.Count(o => !ReferenceEquals(o, seed) && o.Key != seed.Key
                                               && o.Name.Equals(seed.Name, StringComparison.OrdinalIgnoreCase)) > 0;
            tokens.Add(nameIsShared ? id : seed.Name);
        }
        return string.Join(", ", tokens);
    }
}
