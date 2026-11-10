using FrooxEngine.Store;
using InventorySearch.Core;

namespace InventorySearch;

internal static class RecordMapper
{
    private static readonly string[] NoTags = Array.Empty<string>();

    public static IndexEntry? ToEntry(Record r, Source source, string? viaLink, int depth)
    {
        if (r == null || r.IsDeleted || string.IsNullOrEmpty(r.RecordId) || string.IsNullOrEmpty(r.OwnerId)) return null;
        string type = r.RecordType ?? "";
        var tags = r.Tags != null && r.Tags.Count > 0 ? r.Tags.ToArray() : NoTags;
        return new IndexEntry(
            r.OwnerId, r.RecordId, r.Name ?? "", type,
            Categorizer.Categorize(type, tags),
            r.Path ?? "", tags, r.ThumbnailURI, r.AssetURI,
            r.LastModificationTime, source, viaLink, depth);
    }
}
