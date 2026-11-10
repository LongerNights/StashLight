using FrooxEngine;
using ResoniteModLoader;

namespace StashLight;

/// <summary>
/// Which extra inventories can be searched besides your own: the groups you belong to, plus Local Storage when
/// that mod is installed. Nothing here references the Local Storage mod's assembly — it just gives its records the
/// owner id "L-LocalStorage" and patches the engine's RecordDirectory, so we read it like any other inventory.
/// Without that mod installed, behavior is unchanged.
/// </summary>
internal static class GroupInventories
{
    public const string LocalOwner = "L-LocalStorage";

    public sealed record Target(string OwnerId, string Name, bool IsLocal);

    public static bool LocalStorageInstalled()
    {
        try { return ModLoader.Mods().Any(m => string.Equals(m.Name, "LocalStorage", StringComparison.OrdinalIgnoreCase)); }
        catch { return false; }
    }

    public static List<Target> Discover()
    {
        var list = new List<Target>();
        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var groups = Engine.Current?.Cloud?.Groups?.CurrentUserMemberships;
            if (groups != null)
                foreach (var m in groups)
                    if (!string.IsNullOrEmpty(m.GroupId) && seen.Add(m.GroupId))
                        list.Add(new Target(m.GroupId, string.IsNullOrWhiteSpace(m.GroupName) ? m.GroupId : m.GroupName, false));
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        }
        catch (Exception ex) { ResoniteMod.Warn("[StashLight] could not read group memberships: " + ex.Message); }
        if (LocalStorageInstalled()) list.Add(new Target(LocalOwner, "Local storage", true));
        return list;
    }

    /// <summary>Friendly name for an owner id (group name, "Local storage"), or the id itself.</summary>
    public static string NameOf(string ownerId)
    {
        foreach (var t in IndexService.Targets)
            if (string.Equals(t.OwnerId, ownerId, StringComparison.OrdinalIgnoreCase)) return t.Name;
        return ownerId;
    }
}
