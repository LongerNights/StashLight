using System.IO.Compression;
using System.Text;
using StashLight.Core;
using Xunit;

namespace StashLight.Tests;

public class LinkCrawlTests
{
    private const string Me = "U-me";
    private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly CrawlLimits Limits = new();

    private static LinkSeed Seed(string id, string name, string target = null!) => new($"{Me}/{id}", name, target ?? $"resrec:///U-{name}/R-{id}");
    private static CrawlInfo Info(LinkSeed s, double hoursAgo, bool unavailable = false, int entries = 10)
        => new(s.Key, s.Name, Now.AddHours(-hoursAgo), 3, entries, false, unavailable);
    private static Dictionary<string, CrawlInfo> State(params CrawlInfo[] infos) => infos.ToDictionary(i => i.Key, i => i, StringComparer.OrdinalIgnoreCase);
    private static HashSet<string> None => new(StringComparer.OrdinalIgnoreCase);
    private static string[] Names(IEnumerable<LinkSeed> s) => s.Select(x => x.Name).ToArray();

    // ------------------------------------------------------------ planner

    [Fact]
    public void Plan_NeverCrawledFirst_ThenStaleOldestFirst_AndSkipsFresh()
    {
        var a = Seed("1", "a"); var b = Seed("2", "b"); var c = Seed("3", "c"); var d = Seed("4", "d");
        var state = State(Info(a, hoursAgo: 30), Info(b, hoursAgo: 2), Info(c, hoursAgo: 100));      // d never crawled
        var plan = CrawlPlanner.Plan(new[] { a, b, c, d }, state, None, Now, Limits);
        Assert.Equal(new[] { "d", "c", "a" }, Names(plan));            // d (new), then c (100 h), a (30 h); b is fresh (2 h < 24 h TTL)
    }

    [Fact]
    public void Plan_UnavailableLinks_AreRetriedOnlyAfterTheirOwnDelay()
    {
        var gone = Seed("1", "gone");
        Assert.Empty(CrawlPlanner.Plan(new[] { gone }, State(Info(gone, 5, unavailable: true)), None, Now, Limits));
        Assert.Single(CrawlPlanner.Plan(new[] { gone }, State(Info(gone, 25, unavailable: true)), None, Now, Limits));
        // custom delays are honoured
        var quick = new CrawlLimits(Ttl: TimeSpan.FromMinutes(10), UnavailableRetry: TimeSpan.FromMinutes(1));
        Assert.Single(CrawlPlanner.Plan(new[] { gone }, State(Info(gone, 0.05, unavailable: true)), None, Now, quick));
    }

    [Fact]
    public void Plan_SkipsLinksYouSwitchedOff_ByNameKeyOrRecordId()
    {
        var a = Seed("R-1", "Alpha"); var b = Seed("R-2", "Beta"); var c = Seed("R-3", "Gamma");
        var off = CrawlPlanner.ParseDisabled(" alpha ; U-me/R-2,\n R-3 ");
        Assert.Equal(3, off.Count);
        Assert.Empty(CrawlPlanner.Plan(new[] { a, b, c }, State(), off, Now, Limits));
        Assert.True(CrawlPlanner.IsDisabled(a, off));      // by name, case-insensitive
        Assert.True(CrawlPlanner.IsDisabled(b, off));      // by full key
        Assert.True(CrawlPlanner.IsDisabled(c, off));      // by link record id alone
    }

    [Fact]
    public void Plan_CrawlsTwoLinksToTheSameTargetOnce()
    {
        var a = Seed("1", "a", "resrec:///U-x/R-same"); var b = Seed("2", "b", "RESREC:///u-x/r-SAME"); var c = Seed("3", "c");
        Assert.Equal(new[] { "a", "c" }, Names(CrawlPlanner.Plan(new[] { a, b, c }, State(), None, Now, Limits)));
    }

    [Fact]
    public void KeepKeys_ExcludesDisabledAndDeletedLinks()
    {
        var a = Seed("1", "a"); var b = Seed("2", "b");
        var keep = CrawlPlanner.KeepKeys(new[] { a, b }, CrawlPlanner.ParseDisabled("b"));
        Assert.Equal(new[] { a.Key }, keep.ToArray());
    }

    [Fact]
    public void ParseDisabled_HandlesEmptyAndMessyInput()
    {
        Assert.Empty(CrawlPlanner.ParseDisabled(null));
        Assert.Empty(CrawlPlanner.ParseDisabled("  ,; ,\n "));
        Assert.Equal(new[] { "x", "y z" }, CrawlPlanner.ParseDisabled("x,, y z ;").OrderBy(s => s).ToArray());
    }

    [Fact]
    public void SeedsFrom_UsesOnlyYourOwnLinkEntriesThatPointSomewhere()
    {
        IndexEntry L(string id, string name, string? asset, Source src = Source.Personal, string cat = "link")
            => new(Me, id, name, "link", cat, "Inventory", Array.Empty<string>(), null, asset, Now, src, null, 0);
        var seeds = CrawlPlanner.SeedsFrom(new[]
        {
            L("R-1", "<color=red>Red</color> Folder", "resrec:///U-a/R-9"),
            L("R-2", "no target", null),                                  // a link with no target: nothing to crawl
            L("R-3", "someone else's", "resrec:///U-b/R-8", Source.Linked),
            L("R-4", "a folder not a link", "x", cat: "folder"),
        });
        var s = Assert.Single(seeds);
        Assert.Equal("U-me/R-1", s.Key);
        Assert.Equal("Red Folder", s.Name);                                // rich text stripped
        Assert.Equal("resrec:///U-a/R-9", s.TargetUri);
    }

    // ------------------------------------------------------------ links panel text and setting edits

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(59, "just now")]
    [InlineData(60, "1 min ago")]
    [InlineData(59 * 60, "59 min ago")]
    [InlineData(60 * 60, "1 h ago")]
    [InlineData(47 * 3600, "47 h ago")]
    [InlineData(48 * 3600, "2 days ago")]
    [InlineData(-5, "just now")]                    // a clock a little behind is not an error
    public void HumanAge_Boundaries(int seconds, string expected)
        => Assert.Equal(expected, LinkRows.HumanAge(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Describe_CoversEveryRowState()
    {
        var s = Seed("1", "a");
        Assert.Equal("switched off", LinkRows.Describe(Info(s, 1), disabled: true, Now));          // off wins over any crawl info
        Assert.Equal("not crawled yet", LinkRows.Describe(null, false, Now));
        Assert.Equal("1,234 items · 3 h ago", LinkRows.Describe(Info(s, 3, entries: 1234), false, Now));
        Assert.Equal("unavailable (tried 5 h ago)", LinkRows.Describe(Info(s, 5, unavailable: true), false, Now));
        var cut = Info(s, 0.5) with { Truncated = true, Entries = 900 };
        Assert.Equal("900 items · 30 min ago · cut at the limit", LinkRows.Describe(cut, false, Now));
    }

    [Fact]
    public void Summary_CountsCrawledItemsUnavailableAndOff_AndExplainsWhenLinkedIsOff()
    {
        var a = Seed("1", "a"); var b = Seed("2", "b"); var c = Seed("3", "c"); var d = Seed("4", "d");
        var state = State(Info(a, 1, entries: 100), Info(b, 1, entries: 50), Info(c, 1, unavailable: true));
        var off = CrawlPlanner.ParseDisabled("b");
        var seeds = new[] { a, b, c, d };
        Assert.Equal("4 links · 1 crawled (100 items) · 1 unavailable · 1 switched off · crawling now",
                     LinkRows.Summary(seeds, state, off, linkedOn: true, crawling: true));
        Assert.EndsWith("Linked is Off, so nothing is being crawled", LinkRows.Summary(seeds, state, off, linkedOn: false, crawling: true));
        Assert.Equal("0 links · 0 crawled (0 items)", LinkRows.Summary(Array.Empty<LinkSeed>(), State(), None, true, false));
    }

    [Fact]
    public void ToggleDisabled_AddsAndRemovesOnlyThatLink_KeepingOtherEntries()
    {
        var a = Seed("R-1", "Alpha"); var b = Seed("R-2", "Beta"); var all = new[] { a, b };
        string s1 = LinkRows.ToggleDisabled("My Own Entry; Gamma", a, all, disable: true);
        Assert.Equal("My Own Entry, Gamma, Alpha", s1);                                       // others kept, name added
        Assert.True(CrawlPlanner.IsDisabled(a, CrawlPlanner.ParseDisabled(s1)));
        Assert.False(CrawlPlanner.IsDisabled(b, CrawlPlanner.ParseDisabled(s1)));
        string s2 = LinkRows.ToggleDisabled(s1, a, all, disable: false);                      // switching back on removes it
        Assert.Equal("My Own Entry, Gamma", s2);
        // removes however the user spelled it: by name in other case, by key, or by record id
        Assert.Equal("", LinkRows.ToggleDisabled("alpha", a, all, false));
        Assert.Equal("", LinkRows.ToggleDisabled("U-me/R-1", a, all, false));
        Assert.Equal("Beta", LinkRows.ToggleDisabled("R-1, Beta", a, all, false));
        Assert.Equal("Beta, Alpha", LinkRows.ToggleDisabled("Beta, Alpha", a, all, disable: true));   // no duplicate entry
    }

    [Fact]
    public void ToggleDisabled_UsesTheRecordIdWhenTwoLinksShareAName()
    {
        var a = Seed("R-1", "Same"); var b = Seed("R-2", "Same");
        string s = LinkRows.ToggleDisabled("", a, new[] { a, b }, disable: true);
        Assert.Equal("R-1", s);                                                                // switching off one must not switch off the other
        var off = CrawlPlanner.ParseDisabled(s);
        Assert.True(CrawlPlanner.IsDisabled(a, off));
        Assert.False(CrawlPlanner.IsDisabled(b, off));
    }

    // ------------------------------------------------------------ keyboard navigation

    [Theory]
    // 12 items in a 5-column grid: rows are 0-4, 5-9, 10-11 (a short last row)
    [InlineData(0, NavKey.Right, 1)]
    [InlineData(4, NavKey.Right, 4)]            // right edge: stay (no wrap to the next row)
    [InlineData(5, NavKey.Left, 5)]             // left edge: stay
    [InlineData(7, NavKey.Left, 6)]
    [InlineData(7, NavKey.Up, 2)]
    [InlineData(2, NavKey.Up, 2)]               // top row: stay
    [InlineData(7, NavKey.Down, 11)]            // row below is short: land on its last item
    [InlineData(5, NavKey.Down, 10)]
    [InlineData(10, NavKey.Down, 10)]           // last row: stay
    [InlineData(11, NavKey.Right, 11)]          // no item after the last
    [InlineData(3, NavKey.Home, 0)]
    [InlineData(3, NavKey.End, 11)]
    [InlineData(9, NavKey.PageUp, 4)]           // 1 visible row: up one row
    [InlineData(1, NavKey.PageDown, 6)]
    [InlineData(8, NavKey.PageDown, 11)]        // clamps to the last item
    public void Grid_Move(int current, NavKey key, int expected)
        => Assert.Equal(expected, GridNavigation.Move(current, 12, 5, visibleRows: 1, key));

    [Fact]
    public void List_OneColumn_UpDownStepOne_LeftRightDoNothing()
    {
        Assert.Equal(4, GridNavigation.Move(3, 10, 1, 8, NavKey.Down));
        Assert.Equal(2, GridNavigation.Move(3, 10, 1, 8, NavKey.Up));
        Assert.Equal(3, GridNavigation.Move(3, 10, 1, 8, NavKey.Left));
        Assert.Equal(3, GridNavigation.Move(3, 10, 1, 8, NavKey.Right));
        Assert.Equal(9, GridNavigation.Move(3, 10, 1, 8, NavKey.PageDown));          // 3 + 8 clamps to the last
        Assert.Equal(0, GridNavigation.Move(5, 10, 1, 8, NavKey.PageUp));
    }

    [Fact]
    public void Nothing_Selected_AnyKeySelectsTheFirst_EndTheLast_AndEmptyListYieldsNone()
    {
        Assert.Equal(0, GridNavigation.Move(-1, 10, 5, 3, NavKey.Down));
        Assert.Equal(0, GridNavigation.Move(-1, 10, 5, 3, NavKey.Left));
        Assert.Equal(9, GridNavigation.Move(-1, 10, 5, 3, NavKey.End));
        Assert.Equal(0, GridNavigation.Move(99, 10, 5, 3, NavKey.Down));               // a stale index counts as "nothing selected"
        Assert.Equal(-1, GridNavigation.Move(0, 0, 5, 3, NavKey.Down));
        Assert.Equal(-1, GridNavigation.Move(-1, 0, 5, 3, NavKey.Home));
    }

    [Fact]
    public void KeyRepeater_OneStepOnPress_ThenRepeatsAfterTheDelay_AndResetsOnRelease()
    {
        var k = new KeyRepeater(delayMs: 350, intervalMs: 60);
        Assert.True(k.Poll(true, 0));                     // the press
        Assert.False(k.Poll(true, 16));                   // held, still inside the delay
        Assert.False(k.Poll(true, 340));
        Assert.True(k.Poll(true, 350));                   // delay over: first repeat
        Assert.False(k.Poll(true, 380));
        Assert.True(k.Poll(true, 410));                   // then every 60 ms
        Assert.False(k.Poll(false, 420));                 // released
        Assert.True(k.Poll(true, 430));                   // a new press steps immediately again
        Assert.False(k.Poll(true, 440));
    }

    [Fact]
    public void KeyRepeater_AfterAStall_DoesNotBurstManyStepsAtOnce()
    {
        var k = new KeyRepeater(350, 60);
        k.Poll(true, 0);
        Assert.True(k.Poll(true, 5000));                  // the game hitched for 5 s while the key was held
        Assert.False(k.Poll(true, 5001));                 // one step for the hitch, not a flood of catch-up steps
        Assert.False(k.Poll(true, 5030));
    }

    // ------------------------------------------------------------ rate gate

    [Fact]
    public void Gate_FiveRequestsPerSecond_SpacesSlotsBy200Ms_AcrossCallers()
    {
        double clock = 1000;
        var gate = new MinIntervalGate(5, () => clock);
        Assert.Equal(0, gate.Reserve(), 3);       // first caller goes immediately
        Assert.Equal(200, gate.Reserve(), 3);     // two more callers arriving at the same instant queue behind it
        Assert.Equal(400, gate.Reserve(), 3);
        clock += 1000;                            // a second later all reserved slots have passed
        Assert.Equal(0, gate.Reserve(), 3);
        clock += 50;                              // arriving mid-interval waits only for the remainder
        Assert.Equal(150, gate.Reserve(), 3);
    }

    [Fact]
    public void Gate_ZeroOrNegativeRate_MeansNoLimit()
    {
        var gate = new MinIntervalGate(0, () => 5);
        for (int i = 0; i < 10; i++) Assert.Equal(0, gate.Reserve(), 3);
    }

    [Fact]
    public async Task Gate_RealClock_ActuallyThrottles()
    {
        var gate = new MinIntervalGate(50);      // 20 ms apart
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 6; i++) await gate.WaitAsync(CancellationToken.None);
        Assert.True(sw.ElapsedMilliseconds >= 90, $"6 requests at 50/s took only {sw.ElapsedMilliseconds} ms");   // 5 gaps of 20 ms, minus slack
    }

    // ------------------------------------------------------------ parts: crawled linked content

    private static IndexEntry Linked(string name, string id, string owner = "U-other", string path = "Inventory\\Public")
        => new(owner, id, name, "object", "object", path, Array.Empty<string>(), "resdb:///t", "resdb:///a", Now, Source.Linked, "My link", 1);

    [Fact]
    public void LinkedParts_AreInTheIndex_ReplacedPerLink_AndCarryTheirCrawlInfo()
    {
        var p = new IndexParts();
        var s = Seed("1", "a");
        p.SetLinked(s.Key, new List<IndexEntry> { Linked("one", "R-1"), Linked("two", "R-2") }, Info(s, 0, entries: 2));
        Assert.Equal(2, p.Build().Count);
        Assert.Equal(2, p.LinkedEntryCount);

        p.SetLinked(s.Key, new List<IndexEntry> { Linked("one", "R-1") }, Info(s, 0, entries: 1));      // a re-crawl replaces, not appends
        Assert.Equal(1, p.Build().Count);
        Assert.Equal(1, p.CrawlState()[s.Key].Entries);
    }

    [Fact]
    public void ForgetCrawl_MakesLinksDueAgain_WithoutLosingTheirItemsUntilReplaced()
    {
        var p = new IndexParts();
        var a = Seed("1", "a"); var b = Seed("2", "b"); var gone = Seed("3", "gone");
        p.SetLinked(a.Key, new List<IndexEntry> { Linked("a1", "R-1") }, Info(a, hoursAgo: 1));
        p.SetLinked(b.Key, new List<IndexEntry> { Linked("b1", "R-2") }, Info(b, hoursAgo: 1));
        p.SetCrawlInfo(Info(gone, hoursAgo: 1, unavailable: true));
        var seeds = new[] { a, b, gone };
        Assert.Empty(CrawlPlanner.Plan(seeds, p.CrawlState(), None, Now, Limits));          // all fresh: nothing to do

        Assert.Equal(1, p.ForgetCrawl(b.Key));                                               // force one link
        Assert.Equal(new[] { "b" }, Names(CrawlPlanner.Plan(seeds, p.CrawlState(), None, Now, Limits)));
        Assert.Equal(2, p.Build().Count);                                                    // its items are still searchable meanwhile

        Assert.Equal(2, p.ForgetCrawl());                                                    // force everything (incl. unavailable ones)
        Assert.Equal(3, CrawlPlanner.Plan(seeds, p.CrawlState(), None, Now, Limits).Count);
        Assert.Equal(0, p.ForgetCrawl("nope"));
        Assert.Equal(2, p.Build().Count);
    }

    [Fact]
    public void RetainLinked_DropsResultsOfDeletedOrDisabledLinks()
    {
        var p = new IndexParts();
        var a = Seed("1", "a"); var b = Seed("2", "b");
        p.SetLinked(a.Key, new List<IndexEntry> { Linked("a1", "R-1") }, Info(a, 0));
        p.SetLinked(b.Key, new List<IndexEntry> { Linked("b1", "R-2"), Linked("b2", "R-3") }, Info(b, 0));
        Assert.Equal(2, p.RetainLinked(new[] { a.Key }));
        Assert.Equal(new[] { "R-1" }, p.Build().Entries.Select(e => e.RecordId).ToArray());
        Assert.False(p.CrawlState().ContainsKey(b.Key));
    }

    [Fact]
    public void DeletingYourLinkRecord_AlsoRemovesWhatWasCrawledThroughIt()
    {
        var p = new IndexParts();
        var s = Seed("R-9", "a");
        p.SetLinked(s.Key, new List<IndexEntry> { Linked("x", "R-1") }, Info(s, 0));
        Assert.Equal(1, p.RemoveRecord(Me, "R-9"));                    // you deleted the link record itself
        Assert.Equal(0, p.Build().Count);
        Assert.Empty(p.CrawlState());
    }

    [Fact]
    public void Snapshot_DoesNotStoreDbCopiesOfCrawledItems_AndRestoresEverything()
    {
        var p = new IndexParts();
        var s = Seed("1", "a");
        p.SetLinked(s.Key, new List<IndexEntry> { Linked("x", "R-1") }, Info(s, 0));
        p.SetDb(new List<IndexEntry> { Linked("x", "R-1"), Linked("y", "R-2") });       // R-1 duplicates the crawl
        var snap = p.Snapshot();
        Assert.Single(snap.Db);
        var q = new IndexParts(); q.Restore(snap);
        Assert.Equal(p.Build().Entries.Select(e => e.RecordId).OrderBy(x => x), q.Build().Entries.Select(e => e.RecordId).OrderBy(x => x));
        Assert.Single(q.CrawlState());
    }

    [Fact]
    public void ExcludeLinked_HidesCrawledContent_ButGroupsAndYoursStay()
    {
        var p = new IndexParts();
        p.SetTree(new List<IndexEntry> { new(Me, "R-m", "lamp mine", "object", "object", "Inventory", Array.Empty<string>(), null, null, Now, Source.Personal, null, 0) });
        var s = Seed("1", "a");
        p.SetLinked(s.Key, new List<IndexEntry> { Linked("lamp theirs", "R-1") }, Info(s, 0));
        var idx = p.Build();
        Assert.Equal(2, QueryEngine.Search(idx, Query.Parse("lamp"), SortMode.Name, 10).Total);
        var q = Query.Parse("lamp"); q.ExcludeLinked = true;
        Assert.Equal(1, QueryEngine.Search(idx, q, SortMode.Name, 10).Total);
    }

    // ------------------------------------------------------------ cache file

    private static IndexCacheFile WithLinked()
    {
        var p = new IndexParts();
        var s = Seed("1", "My link");
        p.SetLinked(s.Key, new List<IndexEntry> { Linked("one", "R-1") }, new CrawlInfo(s.Key, s.Name, Now, 7, 1, true, false));
        return IndexCacheFile.From(Me, Array.Empty<SharedTarget>(), p.Snapshot());
    }

    private static byte[] Bytes(IndexCacheFile f) { using var ms = new MemoryStream(); f.Write(ms); return ms.ToArray(); }

    [Fact]
    public void CacheFile_RoundTripsLinkedContentAndCrawlInfo()
    {
        var read = IndexCacheFile.TryRead(new MemoryStream(Bytes(WithLinked())), Me)!;
        Assert.Single(read.Linked);
        var info = Assert.Single(read.Crawl).Value;
        Assert.True(info.Truncated); Assert.Equal(7, info.Folders); Assert.Equal(Now, info.LastAttemptUtc);
        var parts = new IndexParts(); parts.Restore(read.ToParts());
        Assert.Equal(1, parts.LinkedEntryCount);
    }

    [Fact]
    public void CacheFile_WrittenBeforeM4_StillLoads_WithNoLinkedContent()
    {
        // exactly what a 0.4.x file looked like: no Linked / Crawl properties at all
        const string old = """{"Version":1,"UserId":"U-me","SavedUtc":"2026-11-10T01:00:00Z","Targets":[],"Tree":[],"Db":[],"Groups":{}}""";
        using var ms = new MemoryStream();
        using (var br = new BrotliStream(ms, CompressionLevel.Fastest, leaveOpen: true)) br.Write(Encoding.UTF8.GetBytes(old));
        var read = IndexCacheFile.TryRead(new MemoryStream(ms.ToArray()), Me);
        Assert.NotNull(read);
        Assert.Empty(read!.Linked); Assert.Empty(read.Crawl);
        var parts = new IndexParts(); parts.Restore(read.ToParts());           // restoring must not choke on the missing parts
        Assert.Equal(0, parts.Build().Count);
    }

    [Fact]
    public void Distinct_KeepsTheFirstLinkPerTargetFolder()
    {
        var seeds = new List<LinkSeed>
        {
            new("U-me/R-1", "badges", "resrec:///U-x/R-a"),
            new("U-me/R-2", "badges", "resrec:///u-x/r-a"),     // same folder, different link record (case differs)
            new("U-me/R-3", "bluey", "resrec:///U-y/R-b"),
        };
        Assert.Equal(new[] { "U-me/R-1", "U-me/R-3" }, CrawlPlanner.Distinct(seeds).Select(x => x.Key).ToArray());
    }
}
