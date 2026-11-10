using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace StashLight.Core;

public enum SortMode { Relevance, Name, DateModified, Type }

public sealed record SearchHit(IndexEntry Entry, int Score, int Copies = 1);

public sealed record SearchResult(
    IReadOnlyList<SearchHit> Hits,      // top `limit` after sorting
    int Total,                          // total result rows (groups when grouping is on)
    IReadOnlyDictionary<string, int> CategoryCounts,   // per category, in rows
    bool Fuzzy,
    double ElapsedMs,
    int TotalEntries = 0);              // raw matching records (>= Total)

public static class QueryEngine
{
    // match tiers (higher = better); per-term score, summed over terms
    private const int Exact = 100, Prefix = 80, WordPrefix = 60, Substring = 40, TagMatch = 30, PathMatch = 20;
    private const double FuzzyThreshold = 0.6;
    private const int FuzzyBelow = 8;   // run the fuzzy pass only when there are fewer exact-ish hits than this

    /// <summary>
    /// Indexes at least this big are scored on several cores (scoring each entry is independent). Crawled linked content can
    /// take the index past 100k entries, where one core takes ~100 ms per keystroke. Tests set it to compare both paths.
    /// </summary>
    public static int ParallelThreshold { get; set; } = 20_000;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static SearchResult Search(SearchIndex index, Query q, SortMode sort = SortMode.Relevance, int limit = 200,
                                      CancellationToken ct = default, bool group = true, string? onlyCategory = null)
    {
        var sw = Stopwatch.StartNew();
        int n = index.Count;
        var scores = new int[n];
        var matched = new List<int>();
        var counts = new Dictionary<string, int>();

        if (n >= ParallelThreshold && Environment.ProcessorCount > 1)
        {
            // each chunk scores its own range into `raw` (-1 = no match); then one cheap linear pass collects the matches in
            // index order, so the result is identical to the sequential path. Leaves cores free for the game itself.
            var raw = new int[n];
            const int chunk = 4096;
            int chunks = (n + chunk - 1) / chunk;
            var options = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(2, Math.Min(4, Environment.ProcessorCount / 2)) };
            Parallel.For(0, chunks, options, c =>
            {
                int end = Math.Min(n, (c + 1) * chunk);
                for (int i = c * chunk; i < end; i++) raw[i] = ScoreEntry(index, i, q);
            });
            for (int i = 0; i < n; i++)
                if (raw[i] >= 0) { scores[i] = raw[i]; matched.Add(i); }
        }
        else
        {
            for (int i = 0; i < n; i++)
            {
                if ((i & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
                int s = ScoreEntry(index, i, q);
                if (s < 0) continue;
                scores[i] = s;
                matched.Add(i);
            }
        }

        bool fuzzy = false;
        if (q.Terms.Count > 0 && matched.Count < FuzzyBelow)
        {
            var qg = Text.Trigrams(q.Text);
            if (qg.Length > 0)
            {
                var have = new HashSet<int>(matched);
                for (int i = 0; i < index.Count; i++)
                {
                    if ((i & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
                    if (have.Contains(i) || !PassesFilters(index, i, q) || IsExcluded(index, i, q)) continue;
                    double sim = Text.Containment(qg, index.Grams[i]);
                    if (sim < FuzzyThreshold) continue;
                    scores[i] = (int)(sim * 20);   // always below any real tier
                    matched.Add(i); fuzzy = true;
                }
            }
        }

        Comparison<int> cmp = sort switch
        {
            SortMode.Name => (a, b) => string.CompareOrdinal(index.Name[a], index.Name[b]),
            SortMode.DateModified => (a, b) => index.Entries[b].LastModified.CompareTo(index.Entries[a].LastModified),
            SortMode.Type => (a, b) => { int c = string.CompareOrdinal(index.Entries[a].Category, index.Entries[b].Category); return c != 0 ? c : string.CompareOrdinal(index.Name[a], index.Name[b]); },
            _ => (a, b) => RelevanceCompare(index, scores, a, b),
        };

        int totalEntries = matched.Count;
        int[]? copies = null;
        if (group && matched.Count > 0 && index.DupGroupCount < index.Count)   // nothing to collapse otherwise
        {
            // collapse repeated saves of the same item. The representative is always the NEWEST copy
            // (this is what gets spawned), regardless of sort mode; ties fall back to the sort order.
            var best = new int[index.DupGroupCount]; Array.Fill(best, -1);
            copies = new int[index.DupGroupCount];
            foreach (int i in matched)
            {
                int g = index.GroupId[i];
                copies[g]++;
                if (best[g] < 0) { best[g] = i; continue; }
                int dt = index.Entries[i].LastModified.CompareTo(index.Entries[best[g]].LastModified);
                if (dt > 0 || (dt == 0 && cmp(i, best[g]) < 0)) best[g] = i;
            }
            var reps = new List<int>();
            foreach (int b in best) if (b >= 0) reps.Add(b);
            matched = reps;
        }

        foreach (int i in matched)
        {
            var c = index.Entries[i].Category;
            counts[c] = counts.GetValueOrDefault(c) + 1;
        }

        // category chips: counts above cover every category; the filter only narrows the visible rows
        if (onlyCategory != null)
            matched = matched.Where(i => index.Entries[i].Category == onlyCategory).ToList();

        var top = TopK(matched, Math.Min(limit, matched.Count), cmp);
        var hits = top.Select(i => new SearchHit(index.Entries[i], scores[i], copies == null ? 1 : copies[index.GroupId[i]])).ToList();
        return new SearchResult(hits, matched.Count, counts, fuzzy, sw.Elapsed.TotalMilliseconds, totalEntries);
    }

    private static int RelevanceCompare(SearchIndex idx, int[] scores, int a, int b)
    {
        int c = scores[b].CompareTo(scores[a]);                         // higher score first
        if (c != 0) return c;
        var ea = idx.Entries[a]; var eb = idx.Entries[b];
        c = ea.Source.CompareTo(eb.Source);                             // personal first
        if (c != 0) return c;
        c = ea.LinkDepth.CompareTo(eb.LinkDepth);                       // shallower first
        if (c != 0) return c;
        c = eb.LastModified.CompareTo(ea.LastModified);                 // newer first
        if (c != 0) return c;
        return string.CompareOrdinal(idx.Name[a], idx.Name[b]);
    }

    /// <summary>Selects the k smallest by <paramref name="cmp"/> without sorting everything.</summary>
    private static List<int> TopK(List<int> items, int k, Comparison<int> cmp)
    {
        if (k <= 0) return new List<int>();
        if (items.Count <= k) { var all = new List<int>(items); all.Sort(cmp); return all; }
        // manual max-heap of size k (largest at root = worst kept)
        var heap = new int[k]; int n = 0;
        void Up(int i) { while (i > 0) { int p = (i - 1) / 2; if (cmp(heap[i], heap[p]) <= 0) break; (heap[i], heap[p]) = (heap[p], heap[i]); i = p; } }
        void Down(int i) { while (true) { int l = 2 * i + 1, r = l + 1, m = i; if (l < n && cmp(heap[l], heap[m]) > 0) m = l; if (r < n && cmp(heap[r], heap[m]) > 0) m = r; if (m == i) break; (heap[i], heap[m]) = (heap[m], heap[i]); i = m; } }
        foreach (int it in items)
        {
            if (n < k) { heap[n] = it; Up(n); n++; }
            else if (cmp(it, heap[0]) < 0) { heap[0] = it; Down(0); }
        }
        var res = heap.Take(n).ToList(); res.Sort(cmp); return res;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool AnyTagContains(string[] tags, string t)
    {
        foreach (var x in tags) if (x.Contains(t, StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool AnyTagEquals(string[] tags, string t)
    {
        foreach (var x in tags) if (x == t) return true;
        return false;
    }

    private static bool PassesFilters(SearchIndex idx, int i, Query q)
    {
        var e = idx.Entries[i];
        if (q.OnlyMine is bool mine && (e.Source == Source.Personal) != mine) return false;
        if (q.OnlySource is Source only && e.Source != only) return false;
        if (q.ExcludeLinked && e.Source == Source.Linked) return false;
        if (q.ExcludeGroups && e.Source == Source.Group) return false;
        if (q.ScopeOwner != null && !string.Equals(idx.OwnerN[i], q.ScopeOwner, StringComparison.OrdinalIgnoreCase)) return false;
        if (q.Types.Count > 0)
        {
            bool ok = false;
            foreach (var t in q.Types)
                if (e.Category == t || e.RecordType == t
                    || (t.Length == e.Category.Length + 1 && t[^1] == 's' && t.StartsWith(e.Category, StringComparison.Ordinal))) { ok = true; break; }
            if (!ok) return false;
        }
        foreach (var t in q.Tags) if (!AnyTagContains(idx.TagsN[i], t)) return false;
        foreach (var p in q.InPaths) if (!idx.PathN[i].Contains(p, StringComparison.Ordinal)) return false;
        if (q.Owners.Count > 0 && !q.Owners.Any(o => idx.OwnerN[i].Contains(o, StringComparison.Ordinal)
                                                     || (e.ViaLinkName != null && Text.Normalize(e.ViaLinkName).Contains(o, StringComparison.Ordinal)))) return false;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool IsExcluded(SearchIndex idx, int i, Query q)
    {
        foreach (var x in q.Excluded)
            if (idx.Name[i].Contains(x, StringComparison.Ordinal) || idx.PathN[i].Contains(x, StringComparison.Ordinal)
                || AnyTagEquals(idx.TagsN[i], x)) return true;
        return false;
    }

    /// <summary>-1 = no match. Otherwise summed per-term tier score plus small source bonus.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int ScoreEntry(SearchIndex idx, int i, Query q)
    {
        if (!PassesFilters(idx, i, q)) return -1;
        string name = idx.Name[i];
        if (IsExcluded(idx, i, q)) return -1;

        int score = 0;
        foreach (var term in q.Terms)
        {
            int t = TermTier(idx, i, name, term);
            if (t == 0) return -1;
            score += t;
        }
        if (idx.Entries[i].Source == Source.Personal) score += 5;
        return score;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int TermTier(SearchIndex idx, int i, string name, string term)
    {
        if (name == term) return Exact;
        if (name.StartsWith(term, StringComparison.Ordinal)) return Prefix;
        if (Text.HasWordPrefix(name, term)) return WordPrefix;
        if (name.Contains(term, StringComparison.Ordinal)) return Substring;
        foreach (var tag in idx.TagsN[i]) if (tag == term || tag.StartsWith(term, StringComparison.Ordinal)) return TagMatch;
        if (idx.PathN[i].Contains(term, StringComparison.Ordinal)) return PathMatch;
        return 0;
    }
}
