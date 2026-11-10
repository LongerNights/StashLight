namespace InventorySearch.Core;

/// <summary>
/// Maps a record's type + tags to a coarse category used by filter chips and type:.
/// Tag names mirror SkyFrost.Base.RecordTags / InventoryBrowser.ClassifyItem (verified in game build 2026.11.6).
/// </summary>
public static class Categorizer
{
    public static readonly string[] All = { "avatar", "world", "facet", "tool", "media", "object", "folder", "link", "other" };

    public static string Categorize(string recordType, IReadOnlyCollection<string> tags)
    {
        if (recordType == "directory") return "folder";
        if (recordType == "link") return "link";
        bool Has(string t) => tags.Contains(t);
        if (Has("common_avatar")) return "avatar";
        if (Has("world_orb") || recordType == "world") return "world";
        if (Has("facet")) return "facet";
        if (Has("common_tool") || Has("common_tooltip") || Has("interactive_camera") || Has("color_dialog")) return "tool";
        switch (recordType)
        {
            case "texture": case "audio": case "video": return "media";
        }
        if (Has("camera_photo") || Has("vr_photo") || Has("360_photo") || Has("stereo_photo")
            || Has("audio_clip") || Has("video_clip") || Has("audio_player") || Has("video_player")) return "media";
        if (recordType == "object") return "object";
        return "other";
    }
}
