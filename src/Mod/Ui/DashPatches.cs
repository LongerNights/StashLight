using FrooxEngine;
using HarmonyLib;

namespace InventorySearch.Ui;

/// <summary>
/// Adds our screen when the userspace dash builds its screens. Same hook points ResoniteModSettings uses
/// for its "Mods" screen (verified in its decompiled source): SetupDefaults on first creation, OnLoading
/// when the workspace is loaded from the saved cloud copy.
/// </summary>
[HarmonyPatch(typeof(UserspaceScreensManager))]
internal static class DashPatches
{
    [HarmonyPostfix]
    [HarmonyPatch("SetupDefaults")]
    public static void SetupDefaults(UserspaceScreensManager __instance) => SearchScreen.TryBuild(__instance);

    [HarmonyPostfix]
    [HarmonyPatch("OnLoading")]
    public static void OnLoading(UserspaceScreensManager __instance) => SearchScreen.TryBuild(__instance);
}
