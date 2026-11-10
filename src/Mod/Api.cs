using System.Text;
using StashLight.Core;

namespace StashLight;

/// <summary>
/// Static, string-in/string-out surface for diagnostics (callable from McpLink's call_method via
/// typeName "StashLight.Api"). M1 has no UI; this is how results are inspected.
/// </summary>
public static class Api
{
    public static string Status()
    {
        var idx = IndexService.Index;
        return $"phase={IndexService.Phase} user={IndexService.UserId} entries={idx.Count} personal={idx.PersonalCount} shared={idx.SharedCount} linked={idx.LinkedCount}\n"
             + $"linked content: {IndexService.Parts.LinkedEntryCount} crawled items from {IndexService.Parts.CrawlState().Count} links, crawling now: {LinkCrawler.Active} {LinkCrawler.Progress}\n"
             + IndexService.Timeline();
    }

    public static string Search(string query, int limit)
    {
        var res = QueryEngine.Search(IndexService.Index, Query.Parse(query), SortMode.Relevance, Math.Max(1, Math.Min(limit, 100)));
        var sb = new StringBuilder();
        sb.Append($"query '{query}': {res.Total} rows ({res.TotalEntries} records) in {res.ElapsedMs:F2} ms{(res.Fuzzy ? " (fuzzy)" : "")}; categories: ");
        sb.AppendLine(string.Join(", ", res.CategoryCounts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}")));
        foreach (var h in res.Hits)
        {
            var e = h.Entry;
            sb.AppendLine($"  [{h.Score,3}] {(e.Source == Source.Personal ? "mine  " : e.Source == Source.Group ? "group " : "linked")} {e.Category,-7} {Text.StripRichText(e.NameRaw)}{(h.Copies > 1 ? $" ×{h.Copies}" : "")}  — {e.Path}  ({e.OwnerId})");
        }
        return sb.ToString();
    }

    /// <summary>The same rows the Links panel shows: every link of yours with its crawl state and whether it is switched off.</summary>
    public static string Links()
    {
        var seeds = CrawlPlanner.Distinct(CrawlPlanner.SeedsFrom(IndexService.Index.Entries)).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var state = IndexService.Parts.CrawlState();
        var off = CrawlPlanner.ParseDisabled(StashLightMod.Get(StashLightMod.DisabledLinksKey, ""));
        var sb = new StringBuilder();
        sb.AppendLine(LinkRows.Summary(seeds, state, off, StashLightMod.IncludeLinked, LinkCrawler.Active));
        foreach (var s in seeds)
        {
            state.TryGetValue(s.Key, out var info);
            sb.AppendLine($"  {s.Key}  {s.Name}  —  {LinkRows.Describe(info, CrawlPlanner.IsDisabled(s, off), DateTime.UtcNow)}");
        }
        return sb.ToString();
    }

    /// <summary>Opens the Links panel (as the Links… button does) so its rows can be inspected.</summary>
    public static string OpenLinksPanel() => Ui.SearchScreen.DiagOpenLinks();
    public static string CloseLinksPanel() => Ui.SearchScreen.DiagCloseLinks();

    /// <summary>Selects result number <paramref name="index"/> and scrolls it into view (what an arrow key does).</summary>
    public static string SelectIndex(int index) => Ui.SearchScreen.DiagSelect(index);

    /// <summary>Forces a re-crawl of one link (its key, e.g. "U-me/R-…") or, with an empty key, of all links.</summary>
    public static string Recrawl(string key)
        => $"reset {LinkCrawler.Recrawl(string.IsNullOrWhiteSpace(key) ? null : key)} link(s); crawl active now: {LinkCrawler.Active}";

    /// <summary>Time N repeated searches (ms each) to measure steady-state query cost.</summary>
    public static string Bench(string query, int runs)
    {
        var idx = IndexService.Index; var q = Query.Parse(query);
        var times = new List<double>();
        for (int i = 0; i < Math.Max(1, runs); i++) times.Add(QueryEngine.Search(idx, q, SortMode.Relevance, 200).ElapsedMs);
        times.Sort();
        return $"query '{query}' x{times.Count} on {idx.Count} entries: min {times[0]:F2} median {times[times.Count / 2]:F2} max {times[^1]:F2} ms";
    }
}
