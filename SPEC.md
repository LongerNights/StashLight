# StashLight — Mod Spec

Status: draft v0.2 (2026-11-09). Engine APIs were verified against decompiled game build `2026.11.6.1136`, using FrooxEngine, FrooxEngine.Store and SkyFrost.Base, and against installed mods that use the same APIs. Background and measurements: [CLAUDE.md](CLAUDE.md).

## 1. Summary

A Resonite mod that adds a fast, local inventory search to the dash. StashLight indexes the user's inventory **inside the game process**, searches it in memory, and shows results in a purpose-built dash screen.

## 2. Goals / non-goals

**Goals**
- G1. Search the user's own inventory instantly. No external server.
- G2. Optionally search content reachable through **links**, meaning other users' and groups' folders linked from the inventory, without hurting game performance.
- G3. A clear, usable UI: type-as-you-search, thumbnails, filters, a details pane, one-click spawn.
- G4. Never hitch or crash the game. Userspace crashes kill Resonite.

**Non-goals (v1)**
- Searching all public Resonite content. The cloud's `records/pagedSearch` only filters by tag, owner, type and date, with no free-text name search.
- Editing, moving or deleting records from the search UI. "Open folder" covers that.
- Searching inside objects, such as slot names in saved items.

## 3. Verified engine facts

All of these are confirmed in decompiled source unless marked *measured*.

### 3.1 Inventory model — `FrooxEngine.RecordDirectory`
- **Root:** `Engine.Current.Cloud.InventoryRootDirectory` (`OwnerId` = the user, `Name` = "Inventory"). BetterInventoryBrowser uses this. If it is null, fall back to `new RecordDirectory(ownerId, "Inventory", engine)`.
- Members: `OwnerId`, `Path`, `Name`, `ParentDirectory`, `CurrentLoadState` (`NotLoaded` / `LocalCache` / `FullyLoaded`), `IsLink`, `LinkRecord`, `DirectoryRecord`, `EntryRecord`, `CanWrite`, and the read-only lists `Subdirectories` and `Records`.
- **Links are subdirectories, not records.** A `"link"` record becomes a `RecordDirectory` with `IsLink == true` in `Subdirectories`. `OwnerId` is the *target* owner, taken from `LinkRecord.AssetURI`, and `Path` is the parent path plus the link name.
- **Loading:**
  - `TryLocalCacheLoad()`: loads from LocalDB on the thread pool. It never touches the network and returns false if nothing was cached.
  - `EnsureFullyLoaded()`: makes one cloud call, `GetRecords(owner, null, path)`, for a normal folder. A link takes two: `GetRecord(linkUri)` and then `GetRecords`. Both run on the thread pool and cache the results into LocalDB about 5 s later.
  - `GetSubdirectoryAtPath(path)`: loads every folder along the path.
- **Thread safety:** loading replaces the `subdirectories` and `records` lists wholesale (`ProcessContents`), never editing them in place. So a background thread can safely snapshot `Subdirectories` and `Records` and iterate the copy.
- **`UpdateRecursively` is NOT usable.** It returns early for any folder where `!CanWrite`, so it never walks other owners' linked content, and it always forces a cloud load. We write our own walker.
- Mutation methods (for change hooks): `AddItem(name, objectData, thumbnail, tags)`, `TryAddRecord(record)`, `AddLinkAsync(name, target)`, `AddSubdirectory(name, dummyOnly)`, `DeleteItem(record)`, `DeleteSubdirectory(dir)`.

### 3.2 Record model — `FrooxEngine.Store.Record` (implements `SkyFrost.Base.IRecord`)
- Fields: `OwnerId`, `RecordId`, `Name`, `Description`, `RecordType`, `OwnerName`, `Tags` (`HashSet<string>`), `Path`, `AssetURI`, `ThumbnailURI`, `IsPublic`, `IsForPatrons`, `IsReadOnly`, `IsDeleted`, `LastModificationTime`, `CreationTime?`, `FirstPublishTime?`, `Visits`, `Rating`, `Version`, `FetchedOn`.
- URL: `record.GetUrl(cloud.Platform)` gives `resrec:///owner/R-id`.

### 3.3 Local record database — `Engine.LocalDB` (LiteDB)
- `FetchRecordsAsync(Expression<Func<Record,bool>>)` queries **every record the client has ever cached**. That includes other owners' records from linked folders the user has browsed. `GetRecordCount()` returns the total.
- This means a cold start can build a near-complete index **with zero network calls**.
- **Measured in-game (M1, right after launch):** 5,419 records total. `GetRecordCount` 1.3 s (cold), own records (1,888 under `Inventory`) fetched in 0.9 s, other owners' records (3,323) in 1.2 s. First usable index published ~3.5 s after login. The cache holds only what the client has browsed, so the LocalDB alone covered **~93%** of the personal inventory (1,888 of 2,030) and a small slice of linked content. A cloud refresh is still needed for completeness.
- **Caveat:** about a third of "other owners" rows in LocalDB have an empty `Path` (world/session records, owned by groups or machine IDs, not inventory items). Filter on `Path` starting with `Inventory`.
- Per-folder `TryLocalCacheLoad` is slow (≈85 ms each, 8.5 s for 99 folders) versus the one bulk query. Prefer the bulk query for the cache pass.

### 3.4 Cloud records API — `Engine.Cloud.Records` (`SkyFrost.Base.RecordsManager`, authenticated)
- `GetRecords<Record>(ownerId, tag: null, path)` lists a folder's direct children, all in one response with no paging. This is the same call the engine uses.
- `GetRecords<Record>(List<RecordId>)` (POST `records/list`) **resolves many link targets in one request**.
- `GetRecord<Record>(Uri)` / `GetRecord<Record>(ownerId, recordId)` fetch a single record.
- `ExtractRecordID(uri, out owner, out id)` parses a link URI.
- `GetRecordsInHierarchy<Record>(owner, path)` is a recursive `IAsyncEnumerable`. It recurses one folder at a time, sequentially, and does not follow links.
- `FindRecords(SearchParameters)` filters by tags, owner, type and date only. Not used.
- Because the client is authenticated, private folders the user is allowed to see resolve too. The anonymous public API used during measurement could not see them.

### 3.5 Threading primitives
- `await default(ToBackground)` / `await default(ToWorld)` / `await default(NextUpdate)` switch between the thread pool and the world thread (structs in FrooxEngine). This is the same pattern QuickFolderOpener uses.
- `world.RunSynchronously(action)`, `component.RunSynchronously(action)`, `slot.StartTask(Func<Task>)` and `component.StartTask(...)` are also available.

### 3.6 Dash integration (pattern from ResoniteModSettings, which adds a "Mods" screen)
- Harmony postfixes on `UserspaceScreensManager.SetupDefaults` **and** `UserspaceScreensManager.OnLoading` call our builder. Skip if `World != Userspace.UserspaceWorld` or if our screen already exists.
- `RadiantDash dash = manager.Slot.GetComponentInParents<RadiantDash>()`, then `dash.AttachScreen(name, colorX, iconUri)` returns a `RadiantDashScreen`.
- **`screen.Slot.PersistentSelf = false` is mandatory.** The Screens container is a `Workspace` that autosaves to the cloud (`Private/RadiantDash/Screens`). A persistent screen would be saved there without our logic and would linger after the mod is removed. The screen is rebuilt at every launch.
- `screen.Slot.OrderOffset` sets the position among dash buttons.
- `new UIBuilder(screen.ScreenCanvas)` + `RadiantUI_Constants.SetupDefaultStyle(ui)`. The background uses `RadiantDashScreen.BuildBackground(UIBuilder, bool)`, which is protected, so call it via a Harmony reverse patch, as ResoniteModSettings does.

### 3.7 Item actions (from `InventoryBrowser`)
- **Spawn:** `InventoryBrowser.Spawn` is internal, so we reproduce its `SpawnItem` logic with public APIs:
  1. `world = Engine.WorldManager.FocusedWorld`
  2. `world.RunSynchronously`
  3. check `world.CanSpawnObjects()`, else show `NotificationMessage.SpawnTextMessage(...)`
  4. `s = world.RootSlot.LocalUserSpace.AddSlot("InventorySpawn")`
  5. `s.StartTask(async () => { await s.LoadObjectAsync(record); s.PositionInFrontOfUser(null, float3.Down*0.2f, 0.5f); s = s.GetComponent<InventoryItem>()?.Unpack(false, list) ?? s; ...undo batch via CreateSpawnUndoPoint })`
- **Equip avatar:** `Engine.WorldManager.FocusedWorld?.TryEquipAvatar(record)`.
- **Open world:** `RecordTags.GetCorrespondingWorldUrl(record.Tags)`, then `Userspace.OpenWorld(new WorldStartSettings(uri){ GetExisting = true, FetchedWorldName = record.Name })`.
- **Item classification** (avatar, world orb, facet, …): `InventoryBrowser.ClassifyItem` / `RecordTags`. Mirror its tag checks.
- **Open folder:**
  - Personal items: `UserspaceRadiantDash.OpenInventoryAtPath(path)`, where `path` is relative to the root.
  - Linked items: `await dash.OpenInventoryBrowser()` and then `browser.Open(new RecordDirectory(owner, path, Engine.Current, name), SlideSwapRegion.Slide.None)`, the same approach as QuickFolderOpener.
  - Get the dash via `Userspace.UserspaceWorld.GetGloballyRegisteredComponent<UserspaceRadiantDash>()`.
- `InventoryBrowser.CurrentUserspaceInventory` is a static that gives the user's dash browser.

### 3.8 Mod platform
- Game runtime: **.NET 10** (`net10.0`). MonkeyLoader is installed with the **ResoniteModLoader compat game pack v5.0.1.2**. Every installed mod, McpLink included, is an RML mod in `rml_mods/`. The `MonkeyLoader/Mods` folder is empty.
- Build: `net10.0` class library. References:
  - `ResoniteModLoader.dll`, from the game pack nupkg at `lib/net10.0/`
  - `0Harmony.dll` (`rml_libs/`)
  - `FrooxEngine.dll`, `FrooxEngine.Store.dll`, `SkyFrost.Base.dll`, `SkyFrost.Base.Models.dll`, `Elements.Core.dll` and `Elements.Assets.dll` from the game folder, with `Private=false`

  Output `StashLight.dll` goes to `rml_mods/`.
- API: `class StashLight : ResoniteMod` with `Name`/`Author`/`Version`/`Link` and `OnEngineInit()` (`new Harmony(id).PatchAll()`). Config uses `ModConfigurationKey<T>` (the same pattern as BetterInventoryBrowser). ResoniteModSettings already provides a settings UI for RML config keys.

### 3.9 Measurements
- Personal inventory: 136 folders, 1,889 records (1,738 objects, 151 links). All folders were already locally cached.
- Linked content: crawl capped at 1,500 folders, 15,734 records in 163 s, and not finished (2,717 targets still queued). Its size is unknown but is at least 10x the personal inventory. 141 targets were unresolvable anonymously; some may resolve with auth.

### 3.9b M1 in-game measurements (build 0.1.0, 2026-11-09)
| Step | Result |
|---|---|
| LocalDB bulk read (count + own + others) | ~3.3 s cold; publishes first index |
| Tree walk from cache only | 133 folders, 8.5 s (redundant; now off by default) |
| Tree walk with cloud refresh (4 concurrent) | 136 folders, 2,019 entries, 142 link seeds, 5.6 s, 0 failures |
| Final | 2,030 personal entries (incl. 136 folders and 142 links); ready in 17.9 s with the redundant pass, expected ≈9 s without it |
| Index build | 57–115 ms for ~5k entries |
| Query (5,353 entries, in-game, build with default JIT) | median 0.7 ms (empty), 1.1 ms (`type:avatar`), 7.5 ms (`a`), 13.6 ms (`fox`, triggers the fuzzy pass); max 32 ms |
| Query (100k synthetic entries, outside game) | 18–36 ms before tuning, 11–21 ms after `AggressiveOptimization` + allocation fixes |

Findings:
- **Real data has many exact duplicates:** `Inventory\Avatars\Kitavali` holds 240 distinct records all named "Kitavali" (repeated saves). Search results must **group duplicates** (same owner + path + name) into one row with a ×N badge and category counts should count groups. Added to M2.
- In-game queries ran ~10x slower per entry than the same code outside the game, probably tiered JIT never promoting hot loops in a busy process, hence `AggressiveOptimization` on hot paths. Re-measure after the next deploy.
- The 10 ms target at 100k entries is **not yet met** (11–21 ms) but real indexes are 5–20k entries. Next lever if needed: parallel scoring across cores.

### 3.10 Prior art (installed)
- **SimpleStashLight** (art0007i): filters only the *currently open* folder's UI slots by name. Not a real search.
- **BetterInventoryBrowser**: pinned and recent folders, sorting, and name filtering of the current folder.
- Neither one searches across folders, so there is no overlap. Our "Open folder" action should coexist with both: don't patch `BrowserDialog`/`InventoryBrowser` UI building.

## 4. Architecture

```
┌──────────────── StashLight mod (C#, Harmony) ────────────────┐
│                                                                   │
│  LocalDbBootstrap ─┐                                              │
│  PersonalIndexer ──┼──► SearchIndex ◄──── QueryEngine ◄──── UI    │
│  LinkCrawler ──────┘    (immutable         (thread pool)   (dash  │
│        │                 snapshots)                        screen)│
│        └──► IndexCache (disk) ◄┘                                  │
│  ChangeHooks (Harmony postfixes on RecordDirectory mutators)      │
└───────────────────────────────────────────────────────────────────┘
```

### 4.1 Threading rules
- Indexing, crawling, querying and disk I/O run on the **thread pool**. RecordDirectory loading already runs there, and list snapshots are safe (3.1).
- **Only UIX/slot work** happens on the userspace world thread, through `await default(ToWorld)` or `RunSynchronously`. Spawning happens on the focused world's thread.
- The index is an immutable snapshot, swapped atomically. A query always sees one consistent snapshot. No locks on the hot path.

### 4.2 Data model
```csharp
sealed record IndexEntry(
    string OwnerId, string RecordId,     // spawn URI: resrec:///{OwnerId}/{RecordId}
    string Name, string NameRaw,         // NameRaw keeps rich text for display
    string RecordType, string Path, string[] Tags,
    string? ThumbnailUri, string? AssetUri,
    DateTime LastModified,
    Source Source,                       // Personal | Linked
    string? ViaLinkName, int LinkDepth);
```
Derived at index time: normalized lowercase tokens for name, path and tags, plus trigrams for fuzzy matching. The source `Record` object is kept only while needed for an action. Actions refetch by ID via `LocalDB.TryFetchRecordAsync` or `Cloud.Records.GetRecord`.

### 4.3 Startup sequence
1. **IndexCache load** (disk) makes search available within the first second.
2. **LocalDbBootstrap:** query LocalDB for the user's records and for records owned by known link-target owners. Merge them into the index with zero network calls.
3. **PersonalIndexer:** walk from `Cloud.InventoryRootDirectory`. For each folder, call `TryLocalCacheLoad()`. If the folder is `NotLoaded`, or older than its TTL, call `EnsureFullyLoaded()` with ≤ 4 concurrent loads. Snapshot the lists and add the records. Each `IsLink` subdirectory becomes a `LinkSeed` and is not followed here.
4. **LinkCrawler** (if enabled) processes seeds in the background.

Personal index target: **< 2 s** warm, with no frame-time impact since it runs off-thread.

### 4.4 LinkCrawler
- Resolve seeds in batches with `Cloud.Records.GetRecords(List<RecordId>)`, up to N per request. Then list each folder with `GetRecords(owner, null, path)`.
- The crawler uses the cloud API directly, not `RecordDirectory`, so it doesn't build up the engine's in-memory tree. Results are optionally written back via `LocalDB.StoreRecordAsync` so the engine's browser benefits too. *Decide in M4; default off, to avoid bloating the user's LocalDB.*
- Limits (configurable):
  - max link depth 3
  - max folders per seed 500
  - global max folders 5,000
  - concurrency 4
  - ≤ 5 requests/s
- Use a visited set keyed by `(owner, path.ToLowerInvariant())`.
- Incremental and resumable. Folders are re-listed after their TTL (24 h) or when the parent listing shows a newer `LastModificationTime`.
- Results are merged into the index in batches, at most once every 2 s. Unresolvable targets are listed in settings and retried at most once a day.

### 4.5 IndexCache
- Location: `Path.Combine(Engine.Current.DataPath, "StashLight", $"index-{userId}.bin")`.
- Format: a versioned, compressed format (System.Text.Json + Brotli is fine for about 100k entries). It holds entries, crawl state and per-folder timestamps.
- If the file is corrupt or from an old version, delete it and rebuild.

### 4.6 ChangeHooks
- Harmony postfixes on the `RecordDirectory` mutators listed in 3.1 apply incremental index updates.
- Also re-walk the personal inventory when the screen is opened, if the last walk is more than 10 minutes old. Cheap, since it's mostly local cache.

### 4.7 QueryEngine
- Terms are ANDed. Supports `"phrase"`, `-exclude`, `type:`, `tag:`, `in:`, `by:` and `is:mine` / `is:linked`.
- Rank tiers:
  1. exact name
  2. name prefix
  3. name word-prefix
  4. name substring
  5. tag
  6. path
  7. fuzzy (trigram ≥ 0.4, only when few results)
- Tie-breaks: personal, then shallower link depth, then most recently modified.
- Sorts: relevance, name, date, type.
- Target **< 10 ms** at 100k entries. Debounce 60 ms. Stale queries are cancelled.

## 5. UI

A dash screen named **"Search"**, added via the 3.6 pattern and built with UIX from C#.

### 5.1 Layout
```
┌───────────────────────────────────────────────────────────────────┐
│ 🔍 [ search…                                    ] [⟳] [⚙]         │
│ [All] [Avatars] [Objects] [Worlds] [Tools] [Media] … [Mine|+Links]│
├───────────────────────────────────────────┬───────────────────────┤
│ ┌────┐ ┌────┐ ┌────┐ ┌────┐ ┌────┐        │  [ big thumbnail ]    │
│ │thmb│ │thmb│ │thmb│ │thmb│ │thmb│        │  Name                 │
│ │name│ │name│ │name│ │name│ │name│        │  Inventory › Avatars  │
│ └────┘ └────┘ └────┘ └────┘ └────┘        │  type · owner · date  │
│   … virtualized grid (or list view) …     │  tags: …              │
│                                           │  [Spawn] [Equip]      │
│                                           │  [Open folder]        │
│                                           │  [Copy link]          │
├───────────────────────────────────────────┴───────────────────────┤
│ 1,889 mine · 15,734 linked · crawling 312/2,717 · query 3 ms      │
└───────────────────────────────────────────────────────────────────┘
```

### 5.2 Behaviour
- Results update live as you type, with no paging.
- The grid is **virtualized**: a recycled pool of about 2–3 screens of cells. Thumbnails load lazily for visible cells only.
- Grid and list views. Each result shows its folder breadcrumb. Linked results get a "via <link>" badge.
- Click selects an item and shows the details pane. Double-click, or **Spawn**, spawns it.
- **Equip** appears for avatars and **Open world** for world orbs.
- Keyboard: typing focuses search. Arrow keys move the selection, Enter spawns, Esc clears.
- Empty states: indexing progress, and "no results" suggestions.
- Styling: `RadiantUI_Constants`, for a consistent dash look.

### 5.3 Actions
All actions use the verified engine paths in 3.7. **Copy link** copies `resrec:///owner/R-id` to the clipboard via the engine's clipboard API. *Look up the exact call during M2. GetItemLink/CopyHyperlink mods are installed and show prior art.*

## 6. Settings (`ModConfigurationKey`, editable through ResoniteModSettings)

| Key | Default | Notes |
|---|---|---|
| `EnableLinkCrawl` | true | Master switch for G2 |
| `LinkMaxDepth` | 3 | |
| `LinkMaxFoldersPerSeed` / `LinkMaxFoldersTotal` | 500 / 5,000 | |
| `LinkRequestsPerSecond` / `LinkConcurrency` | 5 / 4 | |
| `LinkRefreshHours` | 24 | |
| `DisabledLinks` | [] | Record IDs of personal links to skip |
| `CacheCrawlInLocalDB` | false | Write crawled records into the engine LocalDB |
| `DefaultView` | Grid | Grid / List |
| `ScreenOrderOffset` | 200 | Dash button position |

## 7. Robustness
- Every patch and callback is wrapped in try/catch and logs with an `[StashLight]` prefix. A failure disables that feature, never the game.
- No synchronous network or disk I/O on any world thread.
- Offline: search the cache and show an "offline — cached results" note.
- Use one cache file per user ID. Rebuild when the logged-in user changes.

## 8. Verification plan
- **Unit tests** (no engine): query parsing, ranking and fuzzy thresholds, plus a 100k-entry benchmark for the < 10 ms target.
- **In-game, via McpLink:**
  - The personal index count matches the walk (1,889).
  - `perf` shows no frame spikes during indexing and crawling.
  - 20 known items each rank first for their names.
  - Spawn, equip, open-world and open-folder behave the same as in the stock inventory browser.
  - The screen slot is non-persistent: after a restart, exactly one Search screen exists and the Screens workspace holds no saved copy.
- **Crawl:** request rate stays within config, and it resumes after a mid-crawl restart.

## 9. Milestones
1. **M1 – Core:** skeleton loads under MonkeyLoader+RML. LocalDbBootstrap, PersonalIndexer, SearchIndex and QueryEngine. Measure LocalDB size and query time. Results are logged. No UI.
   *Status: done and verified in-game (build 0.1.0); tuned build 0.1.1 built, awaiting deploy. Remaining M1 item: persist nothing yet (cache is M3).*
2. **M2 – UI:** Search dash screen, search box, virtualized grid, details pane and actions. **Group exact duplicates** (owner + path + name) into one result with a ×N badge.
   *Status: 0.2.1 running in-game. Verified via McpLink (read-only) after launch: one Search screen, `persistent=false`, all sections built, no errors; first query ran (status "3,918 results (5,312 items) · 9.9 ms"), chips show live counts, 30-cell pool fully bound with names/subtitles/badges, 21/21 cells with a thumbnail URL had the asset loaded. User screenshots then confirmed: scrolling/recycling, thumbnails, ×N badges, chips, selection + details pane. Visual issues found and fixed in 0.2.2: name/path text overlapping (now shrink-to-fit + tail-ellipsis paths), missing search hint (prompt must be `Text.NullContent`, not a locale key), Search tab reused the Inventory icon (now the search icon), opaque selection wash (now subtle tint + accent bar), tags clipped in details. 0.2.2 verified via McpLink after launch: no errors, one non-persistent screen, search hint set, typing works (field 'kita' → "32 results (336 items) · 6.2 ms"), idle frame time 5–9 ms (a transient 22 ms spike right after a query; FPS during scrolling not yet measured). Still unverified: spawn/equip/open-folder/copy-link results, scope/sort buttons, how the 0.2.2 visual fixes look. (0.2.0 failed at first button: synced-delegate gotcha, see CLAUDE.md.)* Implemented: `Search` dash screen (non-persistent, hooks as 3.6); debounced type-as-you-search (60 ms, off-thread, stale results dropped); category chips with live per-category counts; scope (Everything/Mine/Linked) and sort (Relevance/Name/Newest) toggles; virtualized 5-column thumbnail grid (cell pool rebinds on scroll, lazy thumbnails); duplicate grouping with ×N badge; details pane; actions Spawn, Equip avatar, Open world, Open folder, Copy link; click a selected item again to spawn it (or open it, for folders). Not implemented: list view and keyboard navigation (M5).
   **First in-game checklist:** (a) a "Search" button appears on the dash and the screen opens; (b) typing filters live and the status bar shows timings; (c) scrolling a large result set (e.g. empty query) is smooth and rows never go blank or overlap; (d) thumbnails load and recycle correctly; (e) chips and sort/scope toggles work; (f) Spawn / Equip / Open world / Open folder / Copy link behave like the stock inventory; (g) `rml_mods` log shows no `[StashLight]` errors; (h) after a restart exactly one Search screen exists and the cloud `Private/RadiantDash/Screens` workspace has not saved a copy.
3. **M3 – Cache and change hooks.** *Status: implemented in 0.4.0 (see 9d); Core unit-tested, in-game verification pending.*
4. **M4 – LinkCrawler:** *Status: implemented in 0.5.0/0.5.1 and verified in game (see 9g).* Opt-in via the Linked toggle; direct listings only; rate-limited; incremental and resumable; per-link switch-off via the `DisabledLinks` setting.
5. **M5 – Polish:** list view, keyboard navigation, filter chips with counts, settings.

## 9b. Group inventories, Local Storage and the scope dropdown (0.3.0)

- **Sources:** `Source.Personal` (your inventory) < `Source.Group` (groups you belong to + Local Storage) < `Source.Linked` (other people's linked/cached folders). Ranking tie-break follows this order.
- **Groups:** after your own inventory, each group in `Engine.Cloud.Groups.CurrentUserMemberships` is walked from `new RecordDirectory(groupId, "Inventory", engine)` (exactly how the engine's `InventoryBrowser` builds group roots) with the authenticated cloud client, one group at a time, republishing the index after each. Cached group records in LocalDB are classified as Group too.
- **Local Storage (optional):** the LocalStorage mod (art0007i) gives its records owner id `L-LocalStorage` and patches `RecordDirectory` (`LoadFrom`, `TryLocalCacheLoad`, …) to read disk. We detect it with `ModLoader.Mods()` (by name) and walk `new RecordDirectory("L-LocalStorage", "Inventory", engine)` like a group. **No assembly reference and no behavior change when it isn't installed.** Its records aren't in LocalDB/cloud, so their `Record` objects are kept in `RecordRegistry` for spawning.
- **Scope dropdown:** button opens a menu built each time it opens: `All`, `Mine`, each group (name + entry count), `Local storage` (if installed), `Linked (others)`. Query filters: `OnlySource` / `ScopeOwner` (Core, unit-tested).
- **Spawning:** single click selects; **double click** (same item within 0.5 s) or the Spawn button spawns. For grouped duplicates the **newest copy** (greatest `LastModified`) is always the representative, whatever the sort mode (unit-tested for all 4 sorts).
- **Kind icons:** folders get an orange folder icon (`OfficialAssets.Graphics.Icons.Dash.Folder`) and links a cyan chain (`Graphics.Icons.General.Chainlink`). Large and centered when there is no thumbnail, a small corner badge when there is; also shown in the details pane. (`UiKit.AddKindIcons` / `ShowKindIcon`.)
- **Copy link** copies `resrec:///owner/R-id`. Pasting a link into a world spawns the item wherever the engine's paste handler puts it (observed: world origin); that is engine behavior, not controlled by the mod. Use Spawn/double-click to place items in front of you.
- **Bug fixed:** empty result sets crashed `ResultGrid.Refresh` (see CLAUDE.md "UIX gotchas": `MathX.Clamp(5,0,0) == -1`), which froze the grid on the previous results while the status line changed.

**0.3.0 verified via McpLink after launch (read-only):** no mod errors in the engine or loader logs; one non-persistent Search screen; tiled background and scope scrim present; scope targets found: "The Gaff" (7 folders, 148 entries, 0.5 s), Elysium Enterprises (378 folders, 1,856 entries, 12.8 s), Local storage (detected; 0 entries because its Records\Inventory folder is empty, which is correct); index 6,360 entries = 2,030 mine + 2,005 groups + 2,325 linked, ready in 21.5 s (usable index from ~3 s, groups republish as each finishes); status line counts groups; query 7.1 ms. Not yet verified by hand: empty-tab fix, double-click, scope menu open/close/select, spawn of the newest copy, icons and style.

## 9d. M3: disk cache, live updates, background refresh (0.4.0)

- **Parts:** the index is assembled from independent parts (`Core/IndexParts.cs`, thread-safe, unit-tested): your inventory tree, the engine's cached-records DB, and one part per group / Local Storage owner. A refresh replaces only its own part.
- **Disk cache** (`Core/IndexCacheFile.cs`, `Mod/IndexCache.cs`): `<LocalDB.PermanentPath>\StashLight\<userId>.index`, versioned JSON + Brotli, written atomically (temp file then replace). Saved after each full build, after background refreshes, and ~20 s after the last live change. At login it is restored *first* (the scope menu's groups too), so everything including group content is searchable immediately; the live sources then refresh their parts. Anything unexpected (corrupt, other user, other format version) is deleted and rebuilt. If the Local Storage mod was uninstalled since the cache was written, its items are dropped; groups you left are dropped once memberships load. Setting: `UseDiskCache` (default on).
- **Measured (0.4.0 first launch):** nothing cached → built from scratch in 21.6 s → cache saved: 10,385 entries, 4.3 MB, 285 ms. Loading that real file takes 320–450 ms (+40–70 ms to build the index). The size was avoidable: serializing straight into the `BrotliStream` made the serializer flush it every buffer and wrecked the ratio (4.3 MB vs 1.6 MB for the same JSON at `Optimal`); the record-DB part also repeated ~4,000 items already stored under the tree/groups, and each entry stored its computed `Uri`. 0.4.1 serializes to bytes then compresses in one write at `Optimal`, drops duplicate DB entries on save, and no longer stores `Uri` (format version unchanged: old files still load). Also found: `SearchIndex` de-duplicated owner ids case-sensitively while everything else compared them case-insensitively; now consistent.
- **Measured (0.4.1 second launch — the restore test):** the old 4.3 MB cache restored 6,360 entries (2,005 of them group/local) **about 1 s after the build started** (982 ms including first-use JIT) versus ~21 s before for the group content to appear; live refreshes then ran as normal (ready in 21.6 s) with no errors and rewrote the cache at **1,064 KB in 178 ms** (was 4,335 KB). Reading the new file: 190–250 ms (was 320–450 ms); the DB part now holds only the 2,335 extra items instead of 6,360. Still to verify by hand: live add/delete updates (save and delete a harmless item), and a background refresh after 20 minutes.
- **Never lose good data to a bad refresh:** a refresh with errors that returns less than half of what is cached is rejected and the cached part is kept (`IndexParts.LooksLikeAFailedRefresh`). If memberships come back empty but groups were cached, the cached groups are kept.
- **Live updates** (`Mod/ChangeHooks.cs`): Harmony postfixes on `RecordDirectory.AddItem / TryAddRecord / AddSubdirectory / AddLinkAsync / DeleteItem / DeleteSubdirectory` add or remove the entry (and a deleted folder's contents) immediately; the index republishes after a 300 ms debounce. They only read results; they never alter engine behavior. Patch classes are applied individually, so a failure in one cannot disable the dash screen.
- **Background refresh:** your inventory every `RefreshMinutes` (default 20; 0 = off), groups every 3x that (at least an hour). Local Storage is indexed early at login (it is just disk), since spawning needs its Record objects.
- **Tests (Core):** routing of changes to the right part, replace-not-duplicate, removal across parts, folder removal incl. the `Foo` vs `FooBar` sibling case, retained groups, snapshot/restore, a concurrency test, and cache round trip / wrong user / wrong version / garbage / truncated / valid-Brotli-wrong-JSON / 10k-entry size and speed.

## 9g. M4: the link crawler (0.5.x)

**What it does.** Indexes the content of the folders your links point to (other people's / groups' folders) so it can be searched. It runs **only while the Linked toggle is on**, as a background pass that starts when the index is "ready" (so after the 60 s startup hold), and stops immediately when the toggle is switched off or you log out.

**Design (all from what the slowness investigation measured):**
- **Reads only, through direct cloud listings** (`Cloud.Records.GetRecords` / `GetRecord(Uri)`), never `RecordDirectory`, so nothing is written to the engine's database. `GetRecord(Uri)` understands both link forms (150 of the user's 151 links are `resrec:///owner/R-id`, one is a path link).
- **Polite:** one request at a time through `MinIntervalGate` (default 5/s, `LinkRequestsPerSecond`); a timeout/5xx is retried within the pass and never recorded as "unavailable"; four transient failures in a row pause the pass for 10 minutes; 404/403/401/410 mean the link is really gone.
- **Bounded:** link-hop depth 3 (`LinkMaxDepth`), 500 folders per link (`LinkMaxFoldersPerLink`), 5,000 folders per pass (`LinkMaxFoldersPerPass`; the rest continues on the next pass 2 minutes later). Folders reached by two links are read once per pass. Two links to the same target are crawled once.
- **Incremental, resumable, persisted:** each link's result is stored as its own part (`IndexParts.SetLinked`), published as soon as the link finishes, and saved to the disk cache at least every 60 s together with a `CrawlInfo` per link (when, folders, items, truncated, unavailable). A link is crawled again only after `LinkRefreshHours` (24); unavailable links are retried after 24 h. Later launches therefore normally do no crawl network work at all. Cache files written before M4 still load (no `Linked`/`Crawl` = empty).
- **Pruning:** results of links you delete or list in `DisabledLinks` (names, link record ids, or `owner/id`, comma-separated) are dropped. Pruning is skipped when no links are visible, so an empty index can never wipe the crawled content. Deleting a link record also drops what was crawled through it.
- **UI:** the status line shows "crawling links 14/140: <link name>" while a pass runs; results appear link by link. With the toggle off the crawled items stay in the cache (hidden), so turning it on later is instant.
- **Core (unit-tested, 61 tests in all):** `CrawlPlanner` (never-crawled first, then stalest; TTL; unavailable retry; disabled by name/key/id; duplicate targets; `SeedsFrom` the index's own link entries), `MinIntervalGate` (fake-clock spacing test plus a real-clock throttle test), linked parts (replace per link, retain, delete-link cascade, snapshot dedupe, cache round trip, pre-M4 file).
- `is:linked` now means linked content only and `is:group` means group/Local Storage items (it used to mean "not mine").

**Measured in game (0.5.0, Linked on in the user's settings, 140 links to crawl):**
- The pass started 2 s after "ready". In the first ~4 minutes it crawled 10 links / 9,166 items (the first link, Resonite Essentials, hit the 500-folder cap in ~100 s); by ~7 minutes the index held 10,896 linked items (14,931 total) and queries still took ~8 ms.
- **Database probe: no slow samples during the crawl** (the probe window overlapped 2.7 minutes of it; the only slow sample, 887 ms, was at engine start-up before the crawl existed). Compare 13.6 s waits with the old RecordDirectory-based walk.
- Search finds deep crawled items (e.g. "Flux-SDK UI 0.23.0" three folders down in another user's ProtoGraph archive). No errors from the mod in either log.

## 9f. The Linked toggle (0.4.3) and the M4 policy

*Decision (user, 2026-11-10): linked content is controlled by its own toggle, and "Linked (others)" is not an inventory, so it does not appear in the Scope menu.*

- **Scope menu** lists real inventories only: All, Mine, each group, Local storage.
- **`Linked: Off/On` button** in the header (between Scope and Sort). Off (default) hides every `Source.Linked` entry from results, chip counts, the "All" count and the status line (which says "N linked (hidden)"); On includes them. The state is the mod setting `IncludeLinked` (default off), saved when toggled, so it survives restarts. Core: `Query.ExcludeLinked` (unit-tested with scope and text filters).
- **M4 plan this implies:** the link crawler (other people's folders reached through your links; the earlier trial saw 15,000+ items in 1,500+ folders and was not finished) should only run **while Linked is On**, so the network work is opt-in too. It must use `CloudWalker` only (direct listings, nothing written to the engine's database), run after the startup hold, rate-limited, depth-limited, resumable, saved in the disk cache, with a per-link on/off list; and `LocalDbProbe` should be used to check it never clogs the database.

## 9e. Start-up slowness report (0.4.2)

*Report: assets take a long time to load for a few minutes after launch, while the frame rate is fine. Was it the indexing or the Search tab's thumbnails?*

**What the logs show** (engine logs in `Resonite\Logs`, compared across launches before and after the mod was first installed on 2026-11-09 21:28 local):
- `GET api.resonite.com/sessions?includeEmptyHeadless=true` times out repeatedly (30 s each, retried for minutes) and some `resdb:///` assets fail to gather (each tried 3 times). **Both also happen in pre-mod launches** (e.g. Nov 8: 6 timeouts and 7,108 failed gathers; Nov 9 21:00: 3 timeouts), at rates similar to launches with the mod (failed asset gathers 33–134 per launch either way; 3 session timeouts pre-mod vs 0–7 with it). In the investigated launch the first sessions request had already been waiting 18 s before the mod sent its first request, and the mod's own folder listings succeeded in the same window, so that endpoint was slow on its own.
- Only 1 of 18 failed assets in the latest launch matches a thumbnail in the index; 17 match nothing the mod knows. So the Search tab's thumbnails are not the main source.
- **Conclusion: the slowness is probably not caused by the mod, and is at least partly server/network side. Not proven either way.**

**A real inefficiency found and removed anyway.** `RecordDirectory.EnsureFullyLoaded()` makes the engine write every record it loads into its local LiteDB (a lookup by `OwnerId`/`RecordId` — the two separate indexes mean a scan of that owner's records — plus an update). The old walker did that for ~520 folders (~4,500 records) at every start, all through LiteDB's single worker queue, which the engine's own asset lookups also use. Hypothesis: that can stall asset lookups while FPS stays fine. 0.4.2 therefore: (1) lists folders with `CloudWalker` (direct `Cloud.Records.GetRecords`, read-only, nothing written to the engine's database; `UseEngineWalker` setting restores the old behavior for comparison); (2) when the index was restored from the disk cache, **waits 60 s** (`StartupRefreshDelaySeconds`) before touching the network/database, since search already works from the cache; (3) lowers default concurrency 4 → 3; (4) adds **`LocalDbProbe`**: times a trivial LocalDB query every 2 s for the first 4 minutes and logs any wait ≥ 300 ms plus a summary, so a clogged queue shows up in the log with the mod's phases beside it.

**Measured result (A/B, 2026-11-10, same cache, same machine; LocalDbProbe = a trivial LocalDB query every 2 s):**

| | Old behavior (`UseEngineWalker` on, no startup hold) | 0.4.2 default (direct listing, 60 s hold) |
|---|---|---|
| Worst wait of the probe query | **13,568 ms** (at +41 s) | 580 ms (at +1 s, engine start-up, before the mod did anything) |
| Slow samples (>= 300 ms) in 4 min | **18 of 91**, 56.6 s waited in total | 1 of 120 |
| Waits above 1 s | from +9 s to +85 s, i.e. **about 80 s after the indexer had already finished** (the engine's queued record writes draining) | none |
| Folder requests / failures | 521 / 0 | 521 / 0 |

So the old walk really did clog the engine's database for ~100 s after login, and assets wait in that queue: this matches the reported symptom ("assets slow for a few minutes, FPS fine"). The new default removes it. Separately, session-list timeouts still occur in 0.4.2 launches **while the mod is idle** (first timeout at 01:34:51, the mod made no network request until 01:35:44), and failed asset gathers were the lowest yet (33), so those remaining timeouts are server/network side.

**User confirmation (2026-11-10): load-in is "much better" with 0.4.2.** Still unverified: the 20-minute background refresh (never observed to fire yet), and the remaining session-list timeouts (server/network side).

**Decisive experiment if it still feels slow:** set the mod's `Enabled` setting to false (ResoniteModSettings, then restart) for one launch. If assets are just as slow, it is not the mod.

## 9h. M5: polish (0.6.x)

- **Links panel** (`Links…` header button, `src/Mod/Ui/LinksPanel.cs`): every link with its crawl result, a per-link On/Off switch (writes `DisabledLinks`), per-link **Re-crawl** and **Re-crawl all** (forced re-crawl; needs Linked on). Row texts come from Core `LinkRows` (unit tested). Rows are built 12 per frame. Verified in game: 151 rows, summary "151 links · 72 crawled (38,046 items) · 12 unavailable · crawling now".
- **Forced re-crawl**: `LinkCrawler.Recrawl(key?)` resets that link (or all), cancels the running pass and restarts it; log line `link crawl: forced a re-crawl of …`. Diagnostic: `Api.Recrawl(key)`, `Api.Links()`.
- **List view** (`ListView` setting / View button): one row per result, with category, owner, date, copies.
- **Keyboard navigation** (`KeyboardNav.cs`, unit tested; not hand-tested in game): arrows, Page Up/Down, Home/End move the selection, Enter = primary action, Esc clears search then selection. Active only while the dash shows Search and no text field is being edited.
- **Parallel scoring**: indexes of 20k+ entries are scored on up to 4 cores; results are identical to the sequential path (tested). 100k entries: 4.6–15 ms per query.
- Mod author shown as **Winter**.
- Notes: cache is ~1 MB per 6k entries (40k entries restore in 2.3–2.8 s); the user's settings have `LinkMaxDepth=10`.
- Diagnostics: `Api.OpenLinksPanel/CloseLinksPanel/SelectIndex`.

## 9c. Visual style (borrowed from the Worlds tab overhaul / Discover mod)

A light touch: layout and structure are unchanged. Tokens come from `D:\Claude Environment\WorldsTabUIOverhaul` (`Ui/Theme.cs`, `UiMotion.cs`, `docs/ui-redesign.md`) and live in `Ui/Theme.cs` here.

- **Background:** the stock Worlds tab's tiled texture under a Dark tint at 96.5 % (falls back to the tint if the texture can't load).
- **Shapes:** panels, thumbnail wells, the selection overlay and badge pills use the dash's rounded button sprite (nine-sliced); the source/copies labels are translucent-black pills like the Worlds cards.
- **Type scale (fixed):** Caption 14, Body 18, Label 22, Title 28, Hero 36. Long names/paths still shrink to fit within a cell (min sizes kept).
- **Buttons:** stock Mid grey by default; the dash cyan only for the selected chip, the current scope option and the one primary action (Spawn).
- **Animations** (all tweens on a few fields, guarded; `Animations` and `AnimationSpeed` mod settings, same idea as Discover's "Animations"/"Animation speed"): the scope menu drops in 30 px and fades (0.30 s QuinticOut / 0.20 s SineOut); the details pane rises once on first selection and the thumbnail fades in on each selection; the selection highlight fades in (0.12 s). Nothing animates while scrolling.
- **Popover behaviour:** an invisible click-away scrim sits behind the scope menu, so clicking anywhere else closes it.
- **Not adopted (yet):** segmented controls, switch rows, icon-only buttons, the pager footer, hover outlines on cells — candidates if wanted.

## 10. Open questions
- ~~How big is LocalDB?~~ Answered in 3.3: 5.4k records, ~3 s cold read, covers ~93% of personal items. Cold-start search is available in ~3.5 s, and complete after the cloud refresh (~9 s).
- Should linked results be on by default, or opt-in behind the `+Links` toggle?
- Should crawled records be written into the engine's LocalDB, which speeds up the stock browser but grows its database?
- What batch size does `records/list` accept? Measure in M4.
