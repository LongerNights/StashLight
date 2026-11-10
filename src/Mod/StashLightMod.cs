using Elements.Core;
using HarmonyLib;
using ResoniteModLoader;

namespace StashLight;

/// <summary>Mod entry point (ResoniteModLoader mod running under MonkeyLoader's RML compat pack).</summary>
public sealed class StashLightMod : ResoniteMod
{
    public const string VERSION = "0.6.3";

    public override string Name => "StashLight";
    public override string Author => "Winter";
    public override string Version => VERSION;
    public override string Link => "";

    // ------------------------------------------------------------------------------------------------------------------
    // Settings page (ResoniteModSettings shows each key's Description as its label, in declaration order, and renders
    // `dummy` keys as plain text). So: short labels with the unit/default in them, grouped under coloured headers
    // (the same trick FasterBadges uses). Key *names* never change, so existing config files keep working.
    // ------------------------------------------------------------------------------------------------------------------

    private static ModConfigurationKey<dummy> Header(string name, string title) =>
        new(name, $"<align=center><color=#4fd9bd><b>[ {title} ]</b></color></align>");
    private static ModConfigurationKey<dummy> Note(string name, string text) =>
        new(name, $"<size=70%><color=#86888b>{text}</color></size>");

    [AutoRegisterConfigKey] public static readonly ModConfigurationKey<dummy> HeaderGeneral = Header("_general", "General");

    [AutoRegisterConfigKey]
    public static readonly ModConfigurationKey<bool> EnabledKey =
        new("Enabled", "Enable inventory search", () => true);

    [AutoRegisterConfigKey]
    public static readonly ModConfigurationKey<bool> ListViewKey =
        new("ListView", "Show results as a list instead of a thumbnail grid", () => false);

    [AutoRegisterConfigKey]
    public static readonly ModConfigurationKey<bool> IncludeLinkedKey =
        new("IncludeLinked", "Include items from linked folders (the Linked button)", () => false);

    [AutoRegisterConfigKey]
    public static readonly ModConfigurationKey<bool> LinkedOnlyKey =
        new("LinkedOnly", "Show only linked items (the third state of the Linked button)", () => false);

    [AutoRegisterConfigKey]
    public static readonly ModConfigurationKey<bool> AnimationsKey =
        new("Animations", "Animate menus and panels", () => true);

    [AutoRegisterConfigKey]
    public static readonly ModConfigurationKey<float> AnimationSpeedKey =
        new("AnimationSpeed", "Animation speed (1 = normal, 2 = twice as fast)", () => 1f, false, v => v > 0f);

    [AutoRegisterConfigKey] public static readonly ModConfigurationKey<dummy> HeaderIndex = Header("_index", "Index & refresh");

    [AutoRegisterConfigKey]
    public static readonly ModConfigurationKey<bool> UseDiskCacheKey =
        new("UseDiskCache", "Save the index to disk (search works instantly on the next start)", () => true);

    [AutoRegisterConfigKey]
    public static readonly ModConfigurationKey<int> RefreshMinutesKey =
        new("RefreshMinutes", "Refresh from the cloud every N minutes (0 = never)", () => 20, false, v => v >= 0);

    [AutoRegisterConfigKey]
    public static readonly ModConfigurationKey<bool> RefreshFromCloudKey =
        new("RefreshFromCloud", "Refresh every folder from the cloud after start-up", () => true);

    [AutoRegisterConfigKey]
    public static readonly ModConfigurationKey<int> StartupRefreshDelaySecondsKey =
        new("StartupRefreshDelaySeconds", "Wait N seconds after start before refreshing (0 = at once)", () => 60, false, v => v >= 0);

    [AutoRegisterConfigKey]
    public static readonly ModConfigurationKey<int> LoadConcurrencyKey =
        new("LoadConcurrency", "Folders loaded at the same time", () => 3, false, v => v >= 1 && v <= 16);

    [AutoRegisterConfigKey]
    public static readonly ModConfigurationKey<bool> IncludeCachedOtherOwnersKey =
        new("IncludeCachedOtherOwners", "Index other owners' items already in the local database", () => true);

    [AutoRegisterConfigKey] public static readonly ModConfigurationKey<dummy> HeaderLinks = Header("_links", "Linked folders");
    [AutoRegisterConfigKey] public static readonly ModConfigurationKey<dummy> NoteLinks =
        Note("_links_note", "Links to other people's folders are crawled slowly in the background. Per-link switches and Re-crawl are in the Links… panel of the Search screen.");

    [AutoRegisterConfigKey]
    public static readonly ModConfigurationKey<int> LinkMaxDepthKey =
        new("LinkMaxDepth", "Follow links this many levels deep (3 = links inside links inside links)", () => 3, false, v => v >= 1 && v <= 20);

    [AutoRegisterConfigKey]
    public static readonly ModConfigurationKey<int> LinkMaxFoldersPerSeedKey =
        new("LinkMaxFoldersPerLink", "Most folders read per link", () => 500, false, v => v >= 1);

    [AutoRegisterConfigKey]
    public static readonly ModConfigurationKey<int> LinkMaxFoldersTotalKey =
        new("LinkMaxFoldersPerPass", "Most folders read per background pass", () => 5000, false, v => v >= 1);

    [AutoRegisterConfigKey]
    public static readonly ModConfigurationKey<double> LinkRequestsPerSecondKey =
        new("LinkRequestsPerSecond", "Most cloud requests per second", () => 5.0, false, v => v > 0.0 && v <= 50.0);

    [AutoRegisterConfigKey]
    public static readonly ModConfigurationKey<int> LinkRefreshHoursKey =
        new("LinkRefreshHours", "Crawl each link again after N hours", () => 24, false, v => v >= 1);

    [AutoRegisterConfigKey]
    public static readonly ModConfigurationKey<string> DisabledLinksKey =
        new("DisabledLinks", "Skipped links (names or ids, comma separated)", () => "");

    /// <summary>The Linked toggle in the Search screen (also a setting): include other people's folders reached through your links.</summary>
    internal static bool IncludeLinked => Get(IncludeLinkedKey, false);
    /// <summary>Linked button, third state: results are limited to linked content (implies IncludeLinked).</summary>
    internal static bool LinkedOnly => IncludeLinked && Get(LinkedOnlyKey, false);

    [AutoRegisterConfigKey] public static readonly ModConfigurationKey<dummy> HeaderAdvanced = Header("_advanced", "Troubleshooting");

    [AutoRegisterConfigKey]
    public static readonly ModConfigurationKey<bool> WalkCacheFirstKey =
        new("WalkCacheFirst", "Also walk the folder tree from the local cache first (slower)", () => false);

    [AutoRegisterConfigKey]
    public static readonly ModConfigurationKey<bool> UseEngineWalkerKey =
        new("UseEngineWalker", "Load folders through the engine instead of read-only (slower start-up)", () => false);

    internal static ModConfiguration? Cfg;

    public override void OnEngineInit()
    {
        Cfg = GetConfiguration();
        Msg($"StashLight {VERSION} loaded");
        // Patch each class on its own: if one fails (say an engine method changed), the others still apply.
        var harmony = new Harmony("Winter.StashLight");
        foreach (var (type, what) in new[] { (typeof(Ui.DashPatches), "dash screen"), (typeof(ChangeHooks), "live index updates") })
        {
            try { harmony.CreateClassProcessor(type).Patch(); Msg($"patched: {what}"); }
            catch (Exception ex) { Error($"failed to patch ({what}); that feature is off: {ex}"); }
        }
        Msg($"settings read: IncludeLinked={IncludeLinked}, ListView={Get(ListViewKey, false)}, LinkMaxDepth={Get(LinkMaxDepthKey, 3)}, "
            + $"StartupRefreshDelaySeconds={Get(StartupRefreshDelaySecondsKey, 60)}, UseEngineWalker={Get(UseEngineWalkerKey, false)}, "
            + $"DisabledLinks='{Get(DisabledLinksKey, "")}'");
        IndexService.Start();
    }

    internal static T Get<T>(ModConfigurationKey<T> key, T fallback)
    {
        try { return Cfg != null && Cfg.TryGetValue(key, out T? v) && v != null ? v : fallback; }
        catch { return fallback; }
    }
}
