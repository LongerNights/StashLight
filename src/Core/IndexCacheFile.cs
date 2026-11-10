using System.IO.Compression;
using System.Text.Json;

namespace InventorySearch.Core;

/// <summary>
/// The on-disk index cache: versioned JSON, Brotli-compressed. It is only a cache, so anything unexpected
/// (corrupt bytes, older format, another user's file) simply yields null and the index is rebuilt.
/// </summary>
public sealed class IndexCacheFile
{
    /// <summary>Bump when <see cref="IndexEntry"/> or this layout changes; older files are ignored.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string UserId { get; set; } = "";
    public DateTime SavedUtc { get; set; }
    public List<SharedTarget> Targets { get; set; } = new();
    public List<IndexEntry> Tree { get; set; } = new();
    public List<IndexEntry> Db { get; set; } = new();
    public Dictionary<string, List<IndexEntry>> Groups { get; set; } = new();
    /// <summary>Crawled linked content per link (absent in files written before M4: loads as empty, so the format version is unchanged).</summary>
    public Dictionary<string, List<IndexEntry>> Linked { get; set; } = new();
    public Dictionary<string, CrawlInfo> Crawl { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static IndexCacheFile From(string userId, IEnumerable<SharedTarget> targets, PartsSnapshot parts) => new()
    {
        UserId = userId, SavedUtc = DateTime.UtcNow, Targets = targets.ToList(),
        Tree = parts.Tree, Db = parts.Db, Groups = parts.Groups,
        Linked = parts.Linked ?? new(), Crawl = parts.Crawl ?? new(),
    };

    public PartsSnapshot ToParts() => new(Tree, Db, new Dictionary<string, List<IndexEntry>>(Groups, StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, List<IndexEntry>>(Linked ?? new(), StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, CrawlInfo>(Crawl ?? new(), StringComparer.OrdinalIgnoreCase));

    public void Write(Stream output)
    {
        // Serialize first, then compress in ONE write. Serializing straight into the BrotliStream makes the serializer
        // flush it every buffer, which cuts the compression blocks short (measured: 4.3 MB instead of 1.6 MB).
        var json = JsonSerializer.SerializeToUtf8Bytes(this, Json);
        using var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true);
        brotli.Write(json);
    }

    /// <summary>Reads a cache file; null if it is unreadable, from another format version, or for another user.</summary>
    public static IndexCacheFile? TryRead(Stream input, string expectedUserId)
    {
        try
        {
            using var brotli = new BrotliStream(input, CompressionMode.Decompress, leaveOpen: true);
            var file = JsonSerializer.Deserialize<IndexCacheFile>(brotli, Json);
            if (file == null || file.Version != CurrentVersion) return null;
            if (!string.Equals(file.UserId, expectedUserId, StringComparison.OrdinalIgnoreCase)) return null;
            if (file.Tree == null || file.Db == null || file.Groups == null || file.Targets == null) return null;
            return file;
        }
        catch (Exception) { return null; }
    }
}
