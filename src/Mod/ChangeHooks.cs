using FrooxEngine;
using HarmonyLib;
using Record = FrooxEngine.Store.Record;

namespace InventorySearch;

/// <summary>
/// Keeps the index current while the game runs: postfixes on the RecordDirectory mutators the engine (and mods such as
/// Local Storage) call when you save, create, link or delete things. Postfixes only read the result and hand it to
/// IndexService (which never throws); they never change the engine's behavior.
/// </summary>
[HarmonyPatch(typeof(RecordDirectory))]
internal static class ChangeHooks
{
    [HarmonyPostfix, HarmonyPatch(nameof(RecordDirectory.AddItem))]
    public static void AddItem(Record __result) => IndexService.RecordAdded(__result);

    [HarmonyPostfix, HarmonyPatch(nameof(RecordDirectory.TryAddRecord))]
    public static void TryAddRecord(Record record, bool __result) { if (__result) IndexService.RecordAdded(record); }

    [HarmonyPostfix, HarmonyPatch(nameof(RecordDirectory.AddSubdirectory))]
    public static void AddSubdirectory(RecordDirectory __result) => IndexService.RecordAdded(__result?.EntryRecord);

    [HarmonyPostfix, HarmonyPatch(nameof(RecordDirectory.AddLinkAsync))]
    public static void AddLinkAsync(Task<Record> __result)
    {
        // the link record exists once the task completes; observe it without changing the task the engine awaits
        __result?.ContinueWith(t => { if (t.IsCompletedSuccessfully) IndexService.RecordAdded(t.Result); }, TaskScheduler.Default);
    }

    [HarmonyPostfix, HarmonyPatch(nameof(RecordDirectory.DeleteItem))]
    public static void DeleteItem(Record record, bool __result) { if (__result) IndexService.RecordRemoved(record); }

    [HarmonyPostfix, HarmonyPatch(nameof(RecordDirectory.DeleteSubdirectory))]
    public static void DeleteSubdirectory(RecordDirectory directory) => IndexService.FolderRemoved(directory);
}
