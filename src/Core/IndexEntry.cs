namespace InventorySearch.Core;

/// <summary>Where an entry came from. Order matters: it is the ranking tie-break (lower is better).</summary>
public enum Source
{
    Personal = 0,   // the user's own inventory
    Group = 1,      // a group inventory the user belongs to, or the Local Storage mod's inventory
    Linked = 2,     // other people's/groups' folders reachable through links, or cached from browsing
}

/// <summary>One searchable record. Engine-free so it can be unit tested and cached to disk.</summary>
public sealed record IndexEntry(
    string OwnerId,
    string RecordId,
    string NameRaw,          // as stored (may contain rich-text markup)
    string RecordType,       // object, world, texture, audio, ...
    string Category,         // avatar, world, facet, tool, media, object, other (see Categorizer)
    string Path,             // record path, e.g. "Inventory\Avatars"
    string[] Tags,
    string? ThumbnailUri,
    string? AssetUri,
    DateTime LastModified,
    Source Source,
    string? ViaLinkName,
    int LinkDepth)
{
    /// <summary>resrec URI for this record (computed, so not written to the cache).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Uri => $"resrec:///{OwnerId}/{RecordId}";
}
