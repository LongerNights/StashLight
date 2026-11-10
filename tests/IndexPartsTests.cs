using System.Diagnostics;
using System.IO.Compression;
using StashLight.Core;
using Xunit;

namespace StashLight.Tests;

public class IndexPartsTests
{
    private const string Me = "U-me";
    private static readonly HashSet<string> Shared = new(StringComparer.OrdinalIgnoreCase) { "G-Elysium", "L-LocalStorage" };
    private static bool IsShared(string owner) => Shared.Contains(owner);

    private static IndexEntry E(string name, string id, string owner = Me, string path = "Inventory", Source src = Source.Personal,
                                string cat = "object", string[]? tags = null, int ageDays = 0)
        => new(owner, id, name, "object", cat, path, tags ?? Array.Empty<string>(), "resdb:///thumb", "resdb:///asset",
               new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc).AddDays(-ageDays), src, null, 0);

    private static string[] Ids(IndexParts p) => p.Build().Entries.Select(e => e.RecordId).OrderBy(x => x).ToArray();

    // ------------------------------------------------------------ routing and changes

    [Fact]
    public void Add_RoutesPersonalAndSharedOwners_AndRejectsStrangers()
    {
        var p = new IndexParts();
        Assert.True(p.Add(E("mine", "R-1"), Me, IsShared));
        Assert.True(p.Add(E("group", "R-2", "G-Elysium", src: Source.Group), Me, IsShared));
        Assert.True(p.Add(E("local", "R-3", "L-LocalStorage", src: Source.Group), Me, IsShared));
        Assert.False(p.Add(E("stranger", "R-4", "U-someoneelse", src: Source.Linked), Me, IsShared));
        Assert.Equal(new[] { "R-1", "R-2", "R-3" }, Ids(p));
        Assert.True(p.HasGroup("G-Elysium"));
        Assert.True(p.HasGroup("g-elysium"));      // owner ids compare case-insensitively
    }

    [Fact]
    public void Add_SameRecordTwice_ReplacesInsteadOfDuplicating_AndDropsStaleCachedCopy()
    {
        var p = new IndexParts();
        p.SetDb(new List<IndexEntry> { E("old name", "R-1", src: Source.Personal) });         // stale copy from the record DB
        p.SetTree(new List<IndexEntry> { E("old name", "R-1") });
        Assert.True(p.Add(E("new name", "R-1"), Me, IsShared));
        var entries = p.Build().Entries;
        Assert.Single(entries);
        Assert.Equal("new name", entries[0].NameRaw);
    }

    [Fact]
    public void RemoveRecord_RemovesFromEveryPart()
    {
        var p = new IndexParts();
        p.SetTree(new List<IndexEntry> { E("a", "R-1"), E("b", "R-2") });
        p.SetDb(new List<IndexEntry> { E("a", "R-1") });
        p.SetGroup("G-Elysium", new List<IndexEntry> { E("a", "R-1", "G-Elysium", src: Source.Group) });   // same id, different owner
        Assert.Equal(2, p.RemoveRecord(Me, "R-1"));                // tree + db; the group's R-1 belongs to another owner
        Assert.Equal(new[] { "R-1", "R-2" }, Ids(p));              // group R-1 and personal R-2 remain
        Assert.Equal(1, p.RemoveRecord("G-Elysium", "R-1"));
        Assert.Equal(new[] { "R-2" }, Ids(p));
    }

    [Fact]
    public void RemoveFolder_RemovesTheFolderAndEverythingBelow_ButNotSimilarlyNamedSiblings()
    {
        var p = new IndexParts();
        p.SetTree(new List<IndexEntry>
        {
            E("in foo", "R-1", path: "Inventory\\Foo"),
            E("deep", "R-2", path: "Inventory\\Foo\\Bar\\Baz"),
            E("sibling", "R-3", path: "Inventory\\FooBar"),           // must survive: not inside Foo
            E("root item", "R-4", path: "Inventory"),
            E("other owner", "R-5", "G-Elysium", "Inventory\\Foo", Source.Group),
        });
        Assert.Equal(2, p.RemoveFolder(Me, "Inventory\\Foo"));
        Assert.Equal(new[] { "R-3", "R-4", "R-5" }, Ids(p));
        Assert.Equal(0, p.RemoveFolder(Me, "inventory\\foo"));        // already gone (and the match is case-insensitive)
    }

    [Fact]
    public void RetainGroups_DropsGroupsYouLeftOrLocalStorageYouUninstalled()
    {
        var p = new IndexParts();
        p.SetGroup("G-Elysium", new List<IndexEntry> { E("e", "R-1", "G-Elysium", src: Source.Group) });
        p.SetGroup("L-LocalStorage", new List<IndexEntry> { E("l", "R-2", "L-LocalStorage", src: Source.Group) });
        Assert.Equal(1, p.RetainGroups(new[] { "G-Elysium" }));        // Local Storage mod gone
        Assert.Equal(new[] { "R-1" }, Ids(p));
        Assert.False(p.HasGroup("L-LocalStorage"));
    }

    [Fact]
    public void Build_PersonalBeatsGroupBeatsCachedCopy_ForTheSameRecord()
    {
        var p = new IndexParts();
        p.SetDb(new List<IndexEntry> { E("x", "R-1", "G-Elysium", src: Source.Linked) });
        p.SetGroup("G-Elysium", new List<IndexEntry> { E("x", "R-1", "G-Elysium", src: Source.Group) });
        var e = p.Build().Entries.Single();
        Assert.Equal(Source.Group, e.Source);
    }

    [Fact]
    public void SnapshotRestore_RoundTripsParts_AndIsACopy()
    {
        var p = new IndexParts();
        p.SetTree(new List<IndexEntry> { E("a", "R-1") });
        p.SetGroup("G-Elysium", new List<IndexEntry> { E("g", "R-2", "G-Elysium", src: Source.Group) });
        var snap = p.Snapshot();
        p.RemoveRecord(Me, "R-1");                                    // changing the live parts must not touch the snapshot
        Assert.Single(snap.Tree);

        var q = new IndexParts();
        q.Restore(snap);
        Assert.Equal(new[] { "R-1", "R-2" }, Ids(q));
    }

    [Fact]
    public void Snapshot_LeavesOutDbEntriesTheTreeOrGroupsAlreadyHold_ButTheIndexIsUnchanged()
    {
        var p = new IndexParts();
        p.SetTree(new List<IndexEntry> { E("mine", "R-1") });
        p.SetGroup("G-Elysium", new List<IndexEntry> { E("group", "R-2", "G-Elysium", src: Source.Group) });
        p.SetDb(new List<IndexEntry>
        {
            E("mine", "R-1"),                                              // duplicate of the tree
            E("group", "R-2", "G-ELYSIUM", src: Source.Group),             // duplicate of the group (owner case differs)
            E("linked only", "R-3", "U-other", src: Source.Linked),        // genuinely extra: must be kept
        });
        var snap = p.Snapshot();
        Assert.Single(snap.Db);
        Assert.Equal("R-3", snap.Db[0].RecordId);
        var restored = new IndexParts(); restored.Restore(snap);
        Assert.Equal(Ids(p), Ids(restored));                                // searching a restored cache finds the same things
    }

    [Fact]
    public void CacheFile_DoesNotStoreTheComputedUri_YetStillExposesIt()
    {
        var f = Sample();
        var read = IndexCacheFile.TryRead(new MemoryStream(Bytes(f)), Me)!;
        Assert.Equal("resrec:///U-me/R-1", read.Tree[0].Uri);
        using var br = new BrotliStream(new MemoryStream(Bytes(f)), CompressionMode.Decompress);
        using var sr = new StreamReader(br);
        Assert.DoesNotContain("resrec:///", sr.ReadToEnd());
    }

    [Fact]
    public void CacheFile_CompressesRealisticEntriesWell()
    {
        // realistic: GUID-ish ids and hash-like asset/thumbnail URIs dominate; this guards the single-write Brotli path
        var rng = new Random(11);
        string Hash() => string.Concat(Enumerable.Range(0, 64).Select(_ => "0123456789abcdef"[rng.Next(16)]));
        var list = Enumerable.Range(0, 10_000).Select(i =>
            new IndexEntry(Me, "R-" + Guid.NewGuid(), "Item " + rng.Next(5000), "object", "object", "Inventory\\Folder" + rng.Next(100),
                           new[] { "tag" + rng.Next(40) }, "resdb:///" + Hash() + ".webp", "resdb:///" + Hash() + ".brson",
                           new DateTime(2026, 1, 1).AddMinutes(rng.Next(500000)), Source.Personal, null, 0)).ToList();
        var p = new IndexParts(); p.SetTree(list);
        var bytes = Bytes(IndexCacheFile.From(Me, Array.Empty<SharedTarget>(), p.Snapshot()));
        // each entry carries ~130 incompressible hash chars + a GUID; ~1.2 MB is the floor, the old write path made this ~2x worse
        Assert.True(bytes.Length < 2_200_000, $"cache for 10k realistic entries is {bytes.Length / 1024} KB");
    }

    [Fact]
    public void ConcurrentAddsAndBuilds_DoNotThrow()
    {
        var p = new IndexParts();
        var tasks = Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < 300; i++)
            {
                p.Add(E("n" + i, $"R-{t}-{i}"), Me, IsShared);
                if (i % 10 == 0) p.Build();
                if (i % 25 == 0) p.RemoveRecord(Me, $"R-{t}-{i - 1}");
            }
        })).ToArray();
        Task.WaitAll(tasks);
        Assert.True(p.TotalEntries > 0);
    }

    // ------------------------------------------------------------ cache file

    private static IndexCacheFile Sample()
    {
        var p = new IndexParts();
        p.SetTree(new List<IndexEntry> { E("Kitavali <color=red>x</color>", "R-1", tags: new[] { "common_avatar", "winter" }, cat: "avatar", ageDays: 3) });
        p.SetDb(new List<IndexEntry> { E("cached", "R-2", "U-other", "Inventory\\Public", Source.Linked) });
        p.SetGroup("G-Elysium", new List<IndexEntry> { E("group thing", "R-3", "G-Elysium", "Inventory\\Tools", Source.Group) });
        return IndexCacheFile.From(Me, new[] { new SharedTarget("G-Elysium", "Elysium Enterprises", false), new SharedTarget("L-LocalStorage", "Local storage", true) }, p.Snapshot());
    }

    private static byte[] Bytes(IndexCacheFile f) { using var ms = new MemoryStream(); f.Write(ms); return ms.ToArray(); }

    [Fact]
    public void CacheFile_RoundTrip_PreservesEveryField()
    {
        var original = Sample();
        var read = IndexCacheFile.TryRead(new MemoryStream(Bytes(original)), Me);
        Assert.NotNull(read);
        Assert.Equal(Me, read!.UserId);
        Assert.Equal(2, read.Targets.Count);
        Assert.True(read.Targets[1].IsLocal);

        var a = original.Tree[0]; var b = read.Tree[0];
        Assert.Equal(a.NameRaw, b.NameRaw); Assert.Equal(a.Category, b.Category); Assert.Equal(a.Path, b.Path);
        Assert.Equal(a.Tags, b.Tags); Assert.Equal(a.LastModified, b.LastModified); Assert.Equal(a.Source, b.Source);
        Assert.Equal(a.ThumbnailUri, b.ThumbnailUri); Assert.Equal(a.AssetUri, b.AssetUri); Assert.Equal(a.RecordId, b.RecordId);
        Assert.Equal(Source.Linked, read.Db[0].Source);
        Assert.Equal(Source.Group, read.Groups["G-Elysium"][0].Source);

        // and a restored index searches the same as the original
        var parts = new IndexParts(); parts.Restore(read.ToParts());
        Assert.Equal(3, parts.Build().Count);
    }

    [Fact]
    public void CacheFile_ForAnotherUser_IsIgnored()
        => Assert.Null(IndexCacheFile.TryRead(new MemoryStream(Bytes(Sample())), "U-someoneelse"));

    [Fact]
    public void CacheFile_FromANewerOrOlderFormat_IsIgnored()
    {
        var f = Sample(); f.Version = IndexCacheFile.CurrentVersion + 1;
        Assert.Null(IndexCacheFile.TryRead(new MemoryStream(Bytes(f)), Me));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(50)]
    public void CacheFile_GarbageOrTruncated_YieldsNullNotAnException(int keepBytes)
    {
        var good = Bytes(Sample());
        var bad = keepBytes == 0 ? Array.Empty<byte>() : keepBytes == 1 ? new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 } : good.Take(Math.Min(keepBytes, good.Length / 2)).ToArray();
        Assert.Null(IndexCacheFile.TryRead(new MemoryStream(bad), Me));
    }

    [Fact]
    public void CacheFile_ValidBrotliButNotOurJson_YieldsNull()
    {
        using var ms = new MemoryStream();
        using (var br = new BrotliStream(ms, CompressionLevel.Fastest, leaveOpen: true)) br.Write("{\"hello\": [1,2,3]}"u8);
        Assert.Null(IndexCacheFile.TryRead(new MemoryStream(ms.ToArray()), Me));       // no UserId/Version -> rejected
        using var ms2 = new MemoryStream();
        using (var br = new BrotliStream(ms2, CompressionLevel.Fastest, leaveOpen: true)) br.Write("not json at all"u8);
        Assert.Null(IndexCacheFile.TryRead(new MemoryStream(ms2.ToArray()), Me));
    }

    [Fact]
    public void CacheFile_TenThousandEntries_IsSmallAndFast()
    {
        var rng = new Random(3);
        var list = Enumerable.Range(0, 10_000).Select(i =>
            E("Item " + rng.Next(100000) + " fox", "R-" + i, path: "Inventory\\Folder" + rng.Next(200), tags: new[] { "tag" + rng.Next(50), "common_avatar" })).ToList();
        var p = new IndexParts(); p.SetTree(list);
        var file = IndexCacheFile.From(Me, Array.Empty<SharedTarget>(), p.Snapshot());

        var sw = Stopwatch.StartNew();
        var bytes = Bytes(file);
        long writeMs = sw.ElapsedMilliseconds; sw.Restart();
        var read = IndexCacheFile.TryRead(new MemoryStream(bytes), Me);
        long readMs = sw.ElapsedMilliseconds;

        Assert.NotNull(read);
        Assert.Equal(10_000, read!.Tree.Count);
        Assert.True(bytes.Length < 2_000_000, $"cache is {bytes.Length / 1024} KB");
        Assert.True(writeMs < 3000 && readMs < 3000, $"write {writeMs} ms, read {readMs} ms, {bytes.Length / 1024} KB");
    }
}
