using Elements.Core;
using FrooxEngine;
using FrooxEngine.Store;
using StashLight.Core;
using ResoniteModLoader;
using FrooxEngine.Undo;
using SkyFrost.Base;
using Record = FrooxEngine.Store.Record;

namespace StashLight.Ui;

/// <summary>
/// What the buttons do. Mirrors the engine's own InventoryBrowser behaviour (verified in decompiled source):
/// spawn = InventoryBrowser.SpawnItem, equip = World.TryEquipAvatar, world = Userspace.OpenWorld,
/// open folder = UserspaceRadiantDash.OpenInventoryAtPath / InventoryBrowser.Open.
/// </summary>
internal static class ItemActions
{
    internal static void Toast(string message, bool error = false)
    {
        try
        {
            Userspace.UserspaceWorld?.RunSynchronously(() =>
                NotificationMessage.SpawnTextMessage((LocaleString)message, error ? colorX.Red : colorX.White));
        }
        catch (Exception ex) { ResoniteMod.Warn("[StashLight] toast failed: " + ex.Message); }
    }

    public static async Task<Record?> FetchRecordAsync(IndexEntry e)
    {
        if (RecordRegistry.TryGet(e.OwnerId, e.RecordId, out var known) && known != null) return known;   // Local Storage etc.
        var engine = Engine.Current;
        var local = await engine.LocalDB.TryFetchRecordAsync(e.OwnerId, e.RecordId).ConfigureAwait(false);
        if (local != null) return local;
        var res = await engine.Cloud.Records.GetRecord<Record>(e.OwnerId, e.RecordId).ConfigureAwait(false);
        return res.IsOK ? res.Entity : null;
    }

    public static void Spawn(IndexEntry e)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var rec = await FetchRecordAsync(e).ConfigureAwait(false);
                if (rec == null || string.IsNullOrEmpty(rec.AssetURI)) { Toast("Could not load that item", true); return; }
                SpawnRecord(rec);
            }
            catch (Exception ex) { ResoniteMod.Error("[StashLight] spawn failed: " + ex); Toast("Spawn failed", true); }
        });
    }

    private static void SpawnRecord(Record record, World? target = null)
    {
        var world = target ?? Engine.Current.WorldManager.FocusedWorld;
        if (world == null) return;
        world.RunSynchronously(() =>
        {
            if (!world.CanSpawnObjects()) { Toast("You are not allowed to spawn items here", true); return; }
            Slot s = world.RootSlot.LocalUserSpace.AddSlot("InventorySpawn");
            s.StartTask(async () =>
            {
                await default(ToWorld);
                await s.LoadObjectAsync(record);
                var unpacked = new List<Slot>();
                s.PositionInFrontOfUser(null, float3.Down * 0.2f, 0.5f);
                s = s.GetComponent<InventoryItem>()?.Unpack(keepExistingPosition: false, unpacked) ?? s;
                if (unpacked.Count > 0) foreach (var u in unpacked) u.CreateSpawnUndoPoint();
                else s.CreateSpawnUndoPoint();
            });
        });
    }

    /// <summary>The inventory's "Spawn facet": the facet is spawned into the dash's own (userspace) world instead of the focused world.
    /// Like the stock button it is only offered for your own and your platform group's facets.</summary>
    public static bool CanSpawnFacet(IndexEntry e)
    {
        if (e.Category != "facet") return false;
        var cloud = Engine.Current.Cloud;
        return e.OwnerId == cloud.CurrentUserID || (cloud.Platform.GroupId != null && e.OwnerId == cloud.Platform.GroupId);
    }

    public static void SpawnFacet(IndexEntry e, World dashWorld)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var rec = await FetchRecordAsync(e).ConfigureAwait(false);
                if (rec == null || string.IsNullOrEmpty(rec.AssetURI)) { Toast("Could not load that facet", true); return; }
                SpawnRecord(rec, dashWorld);
            }
            catch (Exception ex) { ResoniteMod.Error("[StashLight] spawn facet failed: " + ex); Toast("Spawn facet failed", true); }
        });
    }

    public static void Equip(IndexEntry e)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var rec = await FetchRecordAsync(e).ConfigureAwait(false);
                if (rec == null) { Toast("Could not load that avatar", true); return; }
                Engine.Current.WorldManager.FocusedWorld?.TryEquipAvatar(rec);
            }
            catch (Exception ex) { ResoniteMod.Error("[StashLight] equip failed: " + ex); Toast("Equip failed", true); }
        });
    }

    public static bool CanOpenWorld(IndexEntry e) => TryWorldUrl(e, out _);

    private static bool TryWorldUrl(IndexEntry e, out Uri? url)
    {
        url = null;
        if (e.Category != "world") return false;
        string? s = RecordTags.GetCorrespondingWorldUrl(new HashSet<string>(e.Tags));
        return Uri.IsWellFormedUriString(s, UriKind.Absolute) && Uri.TryCreate(s, UriKind.Absolute, out url);
    }

    public static void OpenWorld(IndexEntry e)
    {
        if (!TryWorldUrl(e, out var url) || url == null) { Toast("This world has no session link", true); return; }
        Userspace.OpenWorld(new WorldStartSettings(url) { GetExisting = true, FetchedWorldName = Text.StripRichText(e.NameRaw) });
    }

    public static void CopyLink(IndexEntry e)
    {
        var clip = Engine.Current.InputInterface.Clipboard;
        if (clip == null) { Toast("Clipboard not available", true); return; }
        _ = clip.SetText(e.Uri);
        Toast("Link copied");
    }

    /// <summary>Folder that contains the entry (or the folder itself for folder entries).</summary>
    public static (string owner, string path) FolderOf(IndexEntry e)
        => e.Category == "folder" ? (e.OwnerId, e.Path + "\\" + Text.StripRichText(e.NameRaw)) : (e.OwnerId, e.Path);

    public static void OpenFolder(IndexEntry e)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var engine = Engine.Current;
                string owner, path, name;
                if (e.Category == "link" && Uri.TryCreate(e.AssetUri, UriKind.Absolute, out var target))
                {
                    var rec = (await engine.RecordManager.FetchRecord(target, null).ConfigureAwait(false))?.Entity;
                    if (rec == null) { Toast("Could not open that link", true); return; }
                    owner = rec.OwnerId; path = rec.Path + "\\" + rec.Name; name = rec.Name;
                }
                else
                {
                    (owner, path) = FolderOf(e);
                    name = path.Split('\\')[^1];
                }

                string? myId = engine.Cloud.Session.CurrentUserID;
                bool mine = owner == myId && (path == "Inventory" || path.StartsWith("Inventory\\", StringComparison.OrdinalIgnoreCase));
                string rel = path.Length <= "Inventory".Length ? "" : path.Substring("Inventory\\".Length);

                // everything that touches the dash runs on the userspace world thread
                var us = Userspace.UserspaceWorld;
                us.RunSynchronously(() =>
                {
                    var dash = us.GetGloballyRegisteredComponent<UserspaceRadiantDash>();
                    if (dash == null) return;
                    if (mine) { dash.OpenInventoryAtPath(rel); return; }
                    // someone else's folder: open the engine's inventory screen, then navigate (same as QuickFolderOpener)
                    dash.StartTask(async () =>
                    {
                        var browser = await dash.OpenInventoryBrowser();
                        browser?.Open(new RecordDirectory(owner, path, engine, name), FrooxEngine.UIX.SlideSwapRegion.Slide.None);
                    });
                });
            }
            catch (Exception ex) { ResoniteMod.Error("[StashLight] open folder failed: " + ex); Toast("Could not open folder", true); }
        });
    }
}
