using System.Diagnostics;
using StashLight.Core;
using Xunit;

namespace StashLight.Tests;

public class QueryEngineTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    public QueryEngineTests(Xunit.Abstractions.ITestOutputHelper output) { _out = output; }

    private static IndexEntry E(string name, string cat = "object", string path = "Inventory", string[]? tags = null,
                                Source src = Source.Personal, int depth = 0, string owner = "U-me", string? id = null, int ageDays = 0)
        => new(owner, id ?? "R-" + Guid.NewGuid().ToString("N"), name, cat == "avatar" || cat == "world" ? "object" : cat, cat, path,
               tags ?? Array.Empty<string>(), null, null, new DateTime(2026, 1, 1).AddDays(-ageDays), src, src == Source.Linked ? "link" : null, depth);

    private static SearchResult S(SearchIndex idx, string q, SortMode m = SortMode.Relevance, int limit = 50)
        => QueryEngine.Search(idx, Query.Parse(q), m, limit);

    [Fact]
    public void Parse_Phrases_Excludes_Filters()
    {
        var q = Query.Parse("red \"fox tail\" -broken type:avatar tag:cute in:Pets by:Staccato is:linked");
        Assert.Equal(new[] { "red", "fox tail" }, q.Terms);
        Assert.Equal(new[] { "broken" }, q.Excluded);
        Assert.Equal(new[] { "avatar" }, q.Types);
        Assert.Equal(new[] { "cute" }, q.Tags);
        Assert.Equal(new[] { "pets" }, q.InPaths);
        Assert.Equal(new[] { "staccato" }, q.Owners);
        Assert.Equal(Source.Linked, q.OnlySource);
    }

    [Fact]
    public void Parse_UnknownKeyIsPlainTerm_AndEmptyIsEmpty()
    {
        Assert.Equal(new[] { "foo:bar" }, Query.Parse("foo:bar").Terms);
        Assert.True(Query.Parse("   ").IsEmpty);
    }

    [Fact]
    public void Ranking_Exact_Prefix_WordPrefix_Substring()
    {
        var idx = new SearchIndex(new[]
        {
            E("my cat toy"), E("catalog"), E("cat"), E("scatter"), E("big cat"),
        });
        var names = S(idx, "cat").Hits.Select(h => h.Entry.NameRaw).ToList();
        // exact, prefix, word-prefix (tie broken by name), substring
        Assert.Equal(new[] { "cat", "catalog", "big cat", "my cat toy", "scatter" }, names);
    }

    [Fact]
    public void RichText_IsStrippedForMatching()
    {
        var idx = new SearchIndex(new[] { E("<color=#ff0000>Red</color> Dragon") });
        Assert.Equal(1, S(idx, "red dragon").Total);
        Assert.Equal(1, S(idx, "dragon").Total);
        Assert.Equal(0, S(idx, "ff0000").Total);   // markup is not searchable
    }

    [Fact]
    public void AllTermsMustMatch_AndExcludes()
    {
        var idx = new SearchIndex(new[] { E("red fox"), E("red wolf"), E("blue fox") });
        Assert.Equal(1, S(idx, "red fox").Total);
        Assert.Equal(1, S(idx, "fox -red").Total);
        Assert.Equal("blue fox", S(idx, "fox -red").Hits[0].Entry.NameRaw);
    }

    [Fact]
    public void Filters_Type_Tag_Path_Owner_Source()
    {
        var idx = new SearchIndex(new[]
        {
            E("alpha", "avatar", "Inventory\\Avatars", new[] { "common_avatar", "cute" }),
            E("beta", "world", "Inventory\\Worlds"),
            E("gamma", "object", "Inventory\\Pets\\Dogs", src: Source.Linked, depth: 1, owner: "U-Staccato"),
        });
        Assert.Equal(1, S(idx, "type:avatar").Total);
        Assert.Equal(1, S(idx, "type:avatars").Total);
        Assert.Equal(1, S(idx, "tag:cute").Total);
        Assert.Equal(1, S(idx, "in:dogs").Total);
        Assert.Equal(1, S(idx, "by:staccato").Total);
        Assert.Equal(1, S(idx, "is:linked").Total);
        Assert.Equal(0, S(idx, "is:group").Total);        // no group entries in this fixture
        Assert.Equal(2, S(idx, "is:mine").Total);
    }

    [Fact]
    public void Personal_Outranks_Linked_OnTie()
    {
        var idx = new SearchIndex(new[]
        {
            E("lamp", src: Source.Linked, depth: 2, id: "R-1", owner: "U-a"),
            E("lamp", src: Source.Personal, id: "R-2"),
            E("lamp", src: Source.Linked, depth: 1, id: "R-3", owner: "U-b"),
        });
        var ids = S(idx, "lamp").Hits.Select(h => h.Entry.RecordId).ToList();
        Assert.Equal(new[] { "R-2", "R-3", "R-1" }, ids);
    }

    [Fact]
    public void Dedupe_PersonalBeatsLinked_SameRecord()
    {
        var idx = new SearchIndex(new[]
        {
            E("x", src: Source.Linked, depth: 1, id: "R-9", owner: "U-o"),
            E("x", src: Source.Personal, id: "R-9", owner: "U-o"),
        });
        Assert.Equal(1, idx.Count);
        Assert.Equal(Source.Personal, idx.Entries[0].Source);
    }

    [Fact]
    public void Fuzzy_FindsTypos_OnlyWhenFewResults()
    {
        var idx = new SearchIndex(new[] { E("dragon statue"), E("unrelated thing") });
        var r = S(idx, "dragn statue");
        Assert.True(r.Fuzzy);
        Assert.Equal("dragon statue", r.Hits[0].Entry.NameRaw);
    }

    [Fact]
    public void Sorting_Name_Date_AndLimit()
    {
        var idx = new SearchIndex(new[] { E("b", ageDays: 1), E("a", ageDays: 5), E("c", ageDays: 0) });
        Assert.Equal(new[] { "a", "b", "c" }, S(idx, "", SortMode.Name).Hits.Select(h => h.Entry.NameRaw));
        Assert.Equal(new[] { "c", "b", "a" }, S(idx, "", SortMode.DateModified).Hits.Select(h => h.Entry.NameRaw));
        var r = S(idx, "", SortMode.Name, limit: 2);
        Assert.Equal(3, r.Total); Assert.Equal(2, r.Hits.Count);
    }

    [Fact]
    public void CategoryCounts_AreReported()
    {
        var idx = new SearchIndex(new[] { E("a", "avatar"), E("b", "avatar"), E("c", "world") });
        var r = S(idx, "");
        Assert.Equal(2, r.CategoryCounts["avatar"]);
        Assert.Equal(1, r.CategoryCounts["world"]);
    }

    [Fact]
    public void Grouping_CollapsesRepeatedSaves_AndCountsCopies()
    {
        var list = new List<IndexEntry>();
        for (int i = 0; i < 240; i++) list.Add(E("Kitavali", "avatar", "Inventory\\Avatars\\Kitavali", id: "R-k" + i, ageDays: i));
        list.Add(E("Kitavali", "avatar", "Inventory\\Other", id: "R-other"));    // different folder -> separate row
        list.Add(E("Winter Bird", "avatar", "Inventory\\Avatars"));
        var idx = new SearchIndex(list);

        var r = S(idx, "kitavali");
        Assert.Equal(2, r.Total);
        Assert.Equal(241, r.TotalEntries);
        Assert.Equal(240, r.Hits.Max(h => h.Copies));
        Assert.Equal(2, S(idx, "type:avatar").CategoryCounts["avatar"] - 1);   // 3 rows: counts rows, not records
        // representative of the 240 copies is the newest (relevance ties break on date)
        var rep = r.Hits.First(h => h.Copies == 240).Entry;
        Assert.Equal("R-k0", rep.RecordId);
    }

    [Fact]
    public void OnlyCategory_FiltersRows_ButCountsStayFull()
    {
        var idx = new SearchIndex(new[] { E("fox a", "avatar"), E("fox b", "avatar"), E("fox c", "world"), E("fox d", "object") });
        var r = QueryEngine.Search(idx, Query.Parse("fox"), SortMode.Relevance, 50, default, true, "avatar");
        Assert.Equal(2, r.Total);
        Assert.All(r.Hits, h => Assert.Equal("avatar", h.Entry.Category));
        Assert.Equal(2, r.CategoryCounts["avatar"]);
        Assert.Equal(1, r.CategoryCounts["world"]);     // other chips keep their counts
        Assert.Equal(1, r.CategoryCounts["object"]);
    }

    [Theory]
    [InlineData(SortMode.Relevance)]
    [InlineData(SortMode.Name)]
    [InlineData(SortMode.Type)]
    [InlineData(SortMode.DateModified)]
    public void Grouping_RepresentativeIsAlwaysTheNewestCopy(SortMode sort)
    {
        var list = new List<IndexEntry>();
        // same item saved 5 times; ids deliberately not in date order
        int[] ages = { 30, 2, 90, 0, 14 };
        for (int i = 0; i < ages.Length; i++)
            list.Add(E("Widget", "object", "Inventory\\Tools", id: "R-age" + ages[i], ageDays: ages[i]));
        var r = QueryEngine.Search(new SearchIndex(list), Query.Parse("widget"), sort, 50);
        Assert.Single(r.Hits);
        Assert.Equal("R-age0", r.Hits[0].Entry.RecordId);     // the newest (age 0 days) is what spawn will use
        Assert.Equal(5, r.Hits[0].Copies);
    }

    [Fact]
    public void ScopeFilters_ExactSourceAndOwner()
    {
        var idx = new SearchIndex(new[]
        {
            E("mine item", src: Source.Personal),
            E("group item", src: Source.Group, owner: "G-Elysium", id: "R-g1"),
            E("other group item", src: Source.Group, owner: "G-GAF", id: "R-g2"),
            E("local item", src: Source.Group, owner: "L-LocalStorage", id: "R-l1"),
            E("linked item", src: Source.Linked, owner: "U-someone", id: "R-x1", depth: 1),
        });
        Query Q(Action<Query> f) { var q = Query.Parse(""); f(q); return q; }
        Assert.Equal(1, QueryEngine.Search(idx, Q(q => q.ScopeOwner = "G-Elysium"), SortMode.Name, 50).Total);
        Assert.Equal(1, QueryEngine.Search(idx, Q(q => q.ScopeOwner = "g-gaf"), SortMode.Name, 50).Total);         // case-insensitive
        Assert.Equal(1, QueryEngine.Search(idx, Q(q => q.ScopeOwner = "L-LocalStorage"), SortMode.Name, 50).Total);
        Assert.Equal(1, QueryEngine.Search(idx, Q(q => q.OnlySource = Source.Personal), SortMode.Name, 50).Total);
        Assert.Equal(1, QueryEngine.Search(idx, Q(q => q.OnlySource = Source.Linked), SortMode.Name, 50).Total);
        Assert.Equal(5, QueryEngine.Search(idx, Query.Parse(""), SortMode.Name, 50).Total);                        // "All"
    }

    [Fact]
    public void ExcludeLinked_HidesOnlyLinkedEntries_AndCombinesWithScopeAndText()
    {
        var idx = new SearchIndex(new[]
        {
            E("lamp mine", src: Source.Personal),
            E("lamp group", src: Source.Group, owner: "G-Elysium", id: "R-g1"),
            E("lamp linked", src: Source.Linked, owner: "U-someone", id: "R-x1", depth: 1),
        });
        Query Q(string text, Action<Query> f) { var q = Query.Parse(text); f(q); return q; }
        Assert.Equal(3, QueryEngine.Search(idx, Query.Parse("lamp"), SortMode.Name, 50).Total);               // linked included
        var hidden = QueryEngine.Search(idx, Q("lamp", q => q.ExcludeLinked = true), SortMode.Name, 50);
        Assert.Equal(2, hidden.Total);
        Assert.DoesNotContain(hidden.Hits, h => h.Entry.Source == Source.Linked);
        Assert.Equal(hidden.Total, hidden.CategoryCounts.Values.Sum());                                       // chip counts follow the same filter
        Assert.Equal(1, QueryEngine.Search(idx, Q("lamp", q => { q.ExcludeLinked = true; q.ScopeOwner = "G-Elysium"; }), SortMode.Name, 50).Total);
        Assert.Equal(0, QueryEngine.Search(idx, Q("lamp", q => { q.ExcludeLinked = true; q.OnlySource = Source.Linked; }), SortMode.Name, 50).Total);   // contradictory request: nothing
        Assert.False(Q("", q => q.ExcludeLinked = true).IsEmpty);
    }

    [Fact]
    public void Ranking_Personal_Then_Group_Then_Linked()
    {
        var idx = new SearchIndex(new[]
        {
            E("lamp", src: Source.Linked, owner: "U-a", id: "R-1", depth: 1),
            E("lamp", src: Source.Group, owner: "G-x", id: "R-2"),
            E("lamp", src: Source.Personal, id: "R-3"),
        });
        Assert.Equal(new[] { "R-3", "R-2", "R-1" }, S(idx, "lamp").Hits.Select(h => h.Entry.RecordId));
    }

    // ---- grid window math (regression: MathX.Clamp(5,0,0) == -1 made empty results index the pool with -1) ----

    [Fact]
    public void GridMath_EmptyResults_TopRowIsZero_NotNegative()
    {
        Assert.Equal(0, GridMath.TopRow(0, 250f, 830f, 0f));
        Assert.Equal(0, GridMath.TopRow(0, 250f, 830f, 1f));
        Assert.Equal(0, GridMath.TopRow(1, 250f, 830f, 0.7f));      // fewer rows than the viewport
    }

    [Fact]
    public void GridMath_NeverProducesAnOutOfRangeSlot_ForAnyInput()
    {
        var rng = new Random(7);
        float[] ys = { float.NaN, float.NegativeInfinity, -5f, 0f, 0.5f, 1f, 9f, float.PositiveInfinity };
        for (int iter = 0; iter < 20000; iter++)
        {
            int rows = rng.Next(0, 3000);
            float vp = rng.Next(0, 3000);
            float y = iter < ys.Length ? ys[iter] : (float)rng.NextDouble() * 1.2f - 0.1f;
            int top = GridMath.TopRow(rows, 250f, vp, y);
            Assert.InRange(top, 0, Math.Max(0, rows - 1));
            int poolRows = GridMath.PoolRows(vp, 900f, 250f);
            for (int r = top; r < top + poolRows; r++)
                for (int c = 0; c < 5; c++)
                    Assert.InRange(GridMath.PoolSlot(r, c, poolRows, 5), 0, poolRows * 5 - 1);
        }
        Assert.InRange(GridMath.PoolSlot(-3, 4, 6, 5), 0, 29);      // even a negative row can't escape the pool
    }

    [Fact]
    public void Grouping_CanBeDisabled()
    {
        var idx = new SearchIndex(Enumerable.Range(0, 5).Select(i => E("same", id: "R-" + i)));
        Assert.Equal(1, S(idx, "same").Total);
        var raw = QueryEngine.Search(idx, Query.Parse("same"), SortMode.Relevance, 50, default, group: false);
        Assert.Equal(5, raw.Total);
    }

    [Fact]
    public void Categorizer_UsesTags()
    {
        Assert.Equal("avatar", Categorizer.Categorize("object", new[] { "common_avatar" }));
        Assert.Equal("world", Categorizer.Categorize("object", new[] { "world_orb" }));
        Assert.Equal("media", Categorizer.Categorize("texture", Array.Empty<string>()));
        Assert.Equal("object", Categorizer.Categorize("object", Array.Empty<string>()));
        Assert.Equal("other", Categorizer.Categorize("weird", Array.Empty<string>()));
    }

    [Fact]
    public void ExcludeGroups_KeepsPersonalAndLinked_DropsGroups()
    {
        var idx = new SearchIndex(new[]
        {
            E("lamp mine", id: "R-1"),
            E("lamp via link", src: Source.Linked, owner: "U-other", id: "R-2", depth: 1),
            E("lamp in group", src: Source.Group, owner: "G-grp", id: "R-3"),
        });
        var q = Query.Parse("lamp"); q.ExcludeGroups = true;
        var names = QueryEngine.Search(idx, q, SortMode.Relevance, 10).Hits.Select(h => h.Entry.NameRaw).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "lamp mine", "lamp via link" }, names);
        q.ExcludeLinked = true;
        Assert.Single(QueryEngine.Search(idx, q, SortMode.Relevance, 10).Hits);
    }

    [Fact]
    public void ParallelScoring_GivesExactlyTheSameResultsAsSequential()
    {
        var rng = new Random(5);
        string[] words = { "red", "blue", "fox", "wolf", "avatar", "tool", "lamp", "chair", "dragon", "sword", "kitavali", "flux" };
        string[] cats = { "avatar", "world", "object", "tool", "folder", "media" };
        var list = new List<IndexEntry>();
        for (int i = 0; i < 60_000; i++)
        {
            var name = string.Join(' ', Enumerable.Range(0, 2 + rng.Next(3)).Select(_ => words[rng.Next(words.Length)])) + (i % 7 == 0 ? " " + i : "");
            list.Add(E(name, cats[rng.Next(cats.Length)], "Inventory\\F" + rng.Next(300), id: "R-" + i, src: (Source)(i % 3), depth: i % 3,
                       owner: "U-" + (i % 40), ageDays: rng.Next(2000), tags: i % 5 == 0 ? new[] { "common_avatar" } : null));
        }
        var idx = new SearchIndex(list);
        int saved = QueryEngine.ParallelThreshold;
        try
        {
            foreach (var query in new[] { "fox", "red fox", "dragon -sword", "type:avatar lamp", "by:u-3 chair", "in:f12 flux", "\"red fox\"", "xyzzy", "", "is:linked wolf" })
                foreach (var sort in new[] { SortMode.Relevance, SortMode.Name, SortMode.DateModified })
                {
                    QueryEngine.ParallelThreshold = int.MaxValue;                       // sequential baseline
                    var a = QueryEngine.Search(idx, Query.Parse(query), sort, 300);
                    QueryEngine.ParallelThreshold = 1;                                  // force the parallel path
                    var b = QueryEngine.Search(idx, Query.Parse(query), sort, 300);
                    Assert.Equal(a.Total, b.Total);
                    Assert.Equal(a.TotalEntries, b.TotalEntries);
                    Assert.Equal(a.Fuzzy, b.Fuzzy);
                    Assert.Equal(a.Hits.Select(h => (h.Entry.RecordId, h.Score, h.Copies)), b.Hits.Select(h => (h.Entry.RecordId, h.Score, h.Copies)));
                    Assert.Equal(a.CategoryCounts.OrderBy(k => k.Key), b.CategoryCounts.OrderBy(k => k.Key));
                }
        }
        finally { QueryEngine.ParallelThreshold = saved; }
    }

    [Fact]
    public void ParallelScoring_HonoursCancellation()
    {
        var idx = new SearchIndex(Enumerable.Range(0, 50_000).Select(i => E("item " + i, id: "R-" + i)));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        int saved = QueryEngine.ParallelThreshold;
        try
        {
            QueryEngine.ParallelThreshold = 1;
            Assert.ThrowsAny<OperationCanceledException>(() => QueryEngine.Search(idx, Query.Parse("item"), SortMode.Relevance, 10, cts.Token));
        }
        finally { QueryEngine.ParallelThreshold = saved; }
    }

    [Fact]
    public void Benchmark_100k_Entries_Under_Budget()
    {
        var rng = new Random(1);
        string[] words = { "red", "blue", "cat", "dog", "avatar", "tool", "lamp", "chair", "world", "fox", "wolf", "dragon", "gun", "sword" };
        var list = new List<IndexEntry>();
        for (int i = 0; i < 100_000; i++)
            list.Add(E(string.Join(' ', Enumerable.Range(0, 3).Select(_ => words[rng.Next(words.Length)])) + " " + i,
                       path: "Inventory\\F" + rng.Next(500), id: "R-" + i, src: i % 5 == 0 ? Source.Personal : Source.Linked, depth: i % 4));
        var sw = Stopwatch.StartNew();
        var idx = new SearchIndex(list);
        var build = sw.ElapsedMilliseconds;

        for (int w = 0; w < 5; w++) S(idx, "fox");  // warm up JIT
        var times = new List<double>();
        foreach (var q in new[] { "fox", "red fox", "dragon sword", "cat -dog", "type:object lamp", "xyzzy", "" })
        {
            double best = double.MaxValue;
            for (int rep = 0; rep < 5; rep++) best = Math.Min(best, S(idx, q, limit: 200).ElapsedMs);
            times.Add(best);
            _out.WriteLine($"100k entries, query '{q}': best of 5 = {best:F2} ms");
        }
        _out.WriteLine($"index build 100k: {build} ms");
        // generous CI ceiling; spec target is < 10 ms typical on a quiet machine
        Assert.True(times.Max() < 150, $"slowest query {times.Max():F1} ms; build {build} ms; all: {string.Join(", ", times.Select(t => t.ToString("F1")))}");
    }
}
