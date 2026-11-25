using System.Reflection;
using FrooxEngine;
using HarmonyLib;
using Record = FrooxEngine.Store.Record;

namespace StashLight;

/// <summary>
/// Keeps the index current while the game runs: postfixes on the RecordDirectory mutators the engine (and mods such as
/// Local Storage) call when you save, create, link or delete things. Postfixes only read the result and hand it to
/// IndexService (which never throws); they never change the engine's behavior.
///
/// Each hook is patched on its own (<see cref="Apply"/>), so an engine change that breaks one leaves the rest working.
/// Only synchronous methods are hooked: on .NET 11 the engine's async methods are compiled as runtime-async, and Harmony
/// refuses to patch those ("Runtime-async methods are not supported").
/// </summary>
internal static class ChangeHooks
{
    /// <summary>Patches every hook it can. Returns the names of the ones that failed (each is logged).</summary>
    public static List<string> Apply(Harmony harmony)
    {
        var failed = new List<string>();
        var t = typeof(RecordDirectory);
        Hook(harmony, failed, "AddItem", AccessTools.Method(t, nameof(RecordDirectory.AddItem)), postfix: nameof(AddItem));
        Hook(harmony, failed, "TryAddRecord", AccessTools.Method(t, nameof(RecordDirectory.TryAddRecord)), postfix: nameof(TryAddRecord));
        Hook(harmony, failed, "AddSubdirectory", AccessTools.Method(t, nameof(RecordDirectory.AddSubdirectory)), postfix: nameof(AddSubdirectory));
        Hook(harmony, failed, "DeleteItem", AccessTools.Method(t, nameof(RecordDirectory.DeleteItem)), postfix: nameof(DeleteItem));
        Hook(harmony, failed, "DeleteSubdirectory", AccessTools.Method(t, nameof(RecordDirectory.DeleteSubdirectory)), postfix: nameof(DeleteSubdirectory));
        // New links: AddLinkAsync is runtime-async, so catch the RecordDirectory it constructs for the new link instead.
        // That constructor also runs for every subfolder/link while a folder loads (ProcessContents), which isn't new.
        Hook(harmony, failed, "ProcessContents", AccessTools.Method(t, "ProcessContents"), prefix: nameof(LoadingPrefix), finalizer: nameof(LoadingFinalizer));
        Hook(harmony, failed, "new link", AccessTools.Constructor(t, new[] { typeof(Record), typeof(RecordDirectory), typeof(Engine) }), postfix: nameof(DirectoryCreated));
        return failed;
    }

    private static void Hook(Harmony harmony, List<string> failed, string what, MethodBase? original,
        string? prefix = null, string? postfix = null, string? finalizer = null)
    {
        try
        {
            if (original == null) throw new MissingMethodException(nameof(RecordDirectory), what);
            harmony.Patch(original, prefix: Hm(prefix), postfix: Hm(postfix), finalizer: Hm(finalizer));
        }
        catch (Exception ex)
        {
            failed.Add(what);
            ResoniteModLoader.ResoniteMod.Error($"[StashLight] could not hook {what}; index updates for it are off: {ex.Message}");
        }
    }

    private static HarmonyMethod? Hm(string? name) => name == null ? null : new HarmonyMethod(AccessTools.Method(typeof(ChangeHooks), name));

    public static void AddItem(Record __result) => IndexService.RecordAdded(__result);

    public static void TryAddRecord(Record record, bool __result) { if (__result) IndexService.RecordAdded(record); }

    public static void AddSubdirectory(RecordDirectory __result) => IndexService.RecordAdded(__result?.EntryRecord);

    public static void DeleteItem(Record record, bool __result) { if (__result) IndexService.RecordRemoved(record); }

    public static void DeleteSubdirectory(RecordDirectory directory) => IndexService.FolderRemoved(directory);

    // > 0 while this thread is inside RecordDirectory.ProcessContents (loading existing contents, not creating any)
    [ThreadStatic] private static int _loading;

    public static void LoadingPrefix() => _loading++;

    public static Exception? LoadingFinalizer(Exception? __exception) { _loading--; return __exception; }

    public static void DirectoryCreated(Record record)
    {
        if (_loading > 0 || record == null || !IsLink(record)) return;
        IndexService.RecordAdded(record);
    }

    private static bool IsLink(Record record) => string.Equals(record.RecordType, "link", StringComparison.OrdinalIgnoreCase);
}
