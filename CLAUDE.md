# InventorySearch

## Goal

Build a **Resonite mod** for searching the user's inventory — a faster, better-designed replacement for **RedX**, an existing in-game facet (on the dash) that does inventory search. RedX has always been slow and recently broke.

Priorities:
1. **Speed** — fast search over the inventory (index/cache records rather than re-walking the cloud inventory on every query).
2. **UI** — a cleaner, more usable interface than RedX.
3. **Works again** — RedX is currently broken; the mod must not depend on its internals.

**The mod spec is in [SPEC.md](SPEC.md)** — architecture, UI, settings, milestones, and open questions. Keep it updated as decisions are made.

## Tooling: McpLink

Resonite is controlled live through McpLink (`mcp__mcplink__*` tools). Read [CLAUDE-MCPLINK.md](CLAUDE-MCPLINK.md) before using it — it covers addressing, safety rules (read-only on `userspace`, confirm before mutating the user's creations), and how to inspect worlds, ProtoFlux, and UI.

## What RedX is (inspected via MCP, userspace, read-only)

- Lives at `Root/Dash/.../Render/ScreensRoot/Screens/CustomScreen [RedX]` (userspace slot `ID27EA900`; RefIDs change per session). Also a home-screen facet `RedX v2.3`.
- It is a pure ProtoFlux/UIX "custom screen" with facets: Main (`RedXFacet v2`), Filter, Search, Paging, Active (selected-item info). Logic slots are under `RedXFacet v2/LgX`: `UrlBuilder`, `DoRequest`, `TotalItemsHandler`, `IsSearchingHandler`, `ActiveHandler`, `SpawnRecord`.
- **It does not search the local/cloud inventory directly.** It queries an external third-party server: `https://redx.cloudns.org/search.animx?q=<term>&from=0&size=128&type=...` (types: directory, common_avatar, model, facet, texture, ...). The response is an `.animx` (animation asset) used as a data transport, which `DoRequest` loads and samples with ProtoFlux, 128 results per page.
- Search term comes from dynvar `RedX.SearchTerm`; each keystroke change re-triggers `UrlBuilder` (debounced) → `DoRequest`.
- Status check (2026-11-09): host resolves but `search.animx` returns HTTP 404 — the backend is likely dead/moved, which explains the breakage. Slowness is inherent: network round-trip + ProtoFlux parsing of 128 results per page.

## Implications for the mod

- Don't depend on `redx.cloudns.org`. Search the user's own inventory in-process (index records locally, query the index in memory) — that fixes both the breakage and the speed.
- Replace the ProtoFlux data path with C# (mod) and build a cleaner UIX panel.

## Inventory API findings (explored via McpLink, 2026-11-09)

- The engine already holds the inventory as a tree of **`FrooxEngine.RecordDirectory`** objects (the in-game `InventoryBrowser` uses them; its `CurrentDirectory` member exposes one).
  - Members: `Path`, `Name`, `OwnerId`, `Records` (list of `SkyFrost.Base.Record`), `Subdirectories`, `ParentDirectory`, `CurrentLoadState` (e.g. `FullyLoaded`), `IsLink`/`LinkRecord`, `CanWrite`.
  - Methods: `EnsureFullyLoaded()`, `TryLocalCacheLoad()` (loads from the local cache — fast, no network), `GetSubdirectoryAtPath(path)`, `TryGetSubdirectoryAtPath(path, createIfNotLoaded)`. ⚠ `UpdateRecursively` looks like a walker but skips non-writable (linked) folders and forces cloud loads — don't use it. Links appear as `IsLink` *subdirectories*, not records. Verified details: [SPEC.md §3](SPEC.md#3-verified-engine-facts).
  - Engine source can be regenerated for verification: `dotnet tool install ilspycmd --tool-path <dir>`, then `ilspycmd -p -o <out> "<Resonite>\FrooxEngine.dll"` (also FrooxEngine.Store, SkyFrost.Base, SkyFrost.Base.Models). Run from PowerShell in the foreground (the tool is a `.cmd` shim; background PowerShell output did not persist). Installed mods in `rml_mods/` can be decompiled the same way for prior art.
  - A loaded folder is fully in memory: e.g. `Inventory\Random Shit` = 249 records + 4 subdirectories, instantly readable.
- `Record` fields seen in engine logs: Name, Type (object/world/texture/...), Path, Tags, AssetURI, ThumbnailURI, OwnerId/RecordId, Version, Deleted. `RecordManager` only has Fetch/Save/Modify/Delete — no search/enumeration.
- Top level of the inventory also contains **link** records (e.g. "Resonite Essentials" → `G-Resonite/Inventory/...`, "Patreon Swag"); decide whether to follow links when indexing.
- McpLink's `inventory` tool lists one folder at a time (`owner`, `path`); `eval` is NOT installed (needs `McpLink_libs`/`McpLinkEval.dll`), so use `reflect_get` (`target`+`path`), `call_method`, `describe_type` (`includeMethods:true`) instead. Argument names differ from the guide's examples — an unknown-arg error lists the accepted ones.

## Proposed design

1. On startup (and on inventory change), read the engine's local record DB in bulk, then walk the tree from the root `RecordDirectory` with `EnsureFullyLoaded` (our own walker — not `UpdateRecursively`), flattening records into an in-memory index (name, path, type, tags, thumbnail, URIs, normalized search text). Persisting the index to disk is M3.
2. Search = in-memory filter (tokenized/prefix/fuzzy) over the index, off the update thread; paginate/virtualize results in the UI.
3. UI: new UIX panel/dash screen (type filters, folder breadcrumb, thumbnail grid, spawn on click) driven from C#.

## Measured full-tree walk (2026-11-09, user's own inventory, links not followed)

- 136 directories, 1,889 non-directory records (1,738 objects, 151 links). Small — an in-memory index of this is trivial.
- Walking every folder via McpLink's `inventory` tool took ~20 s total (~0.15 s/folder), with no folder over 1 s and zero errors — i.e. all folders were already loaded locally. That time is dominated by per-call HTTP/tool overhead from outside the game; an in-process C# walk of loaded `RecordDirectory` objects should take milliseconds.
- Not yet measured: following the 151 links (they point into other owners' inventories and may need network fetches), and a cold start where folders aren't cached yet.

## Measured: following links into other owners' inventories (2026-11-09, partial)

- Method: Resonite's public cloud API, unauthenticated, read-only (`GET https://api.resonite.com/users/{owner}/records/{recordId}` to resolve a link, then `GET .../users/{owner}/records?path=<dir path>` to list a folder — direct children only, returns the whole folder in one response; `offset`/`count` are ignored, so don't write paging loops). Only public records are visible this way. Links resolve via `record.path + "\\" + record.name`.
- 151 own links → mostly record-ID links to ~110 different owners/groups' public folders. Walking them (with nested links followed, 8 threads) hit my 1,500-folder cap after 163 s: **1,505 folders, 15,734 object records (~10/folder), and not finished** — 2,717 more link targets still queued, and nested links keep multiplying (1,584 nested links seen). 141 link targets were unresolved (deleted/private).
- Conclusion: the linked universe is **well over 10x larger than the personal inventory** (likely tens of thousands of records and thousands of folders), and a full crawl takes minutes even at ~9 folders/s. Do NOT crawl it on the update thread or at every startup.
- Design implications: index personal inventory eagerly (tiny, local); treat linked folders as an optional, lazily built, disk-persisted background index (incremental, rate-limited, depth-limited, with a per-link toggle and cycle/visited tracking); search personal results first.

## Code layout & build (M1)

- `src/Core/` — engine-free: `IndexEntry`, `SearchIndex` (immutable snapshot), `Query` parser, `QueryEngine` (ranking, filters, fuzzy, top-K), `Categorizer`, `Text`. Compiled into both the mod and the tests.
- `src/Mod/` — engine side: `InventorySearchMod` (RML mod entry + config), `IndexService` (waits for login, builds index in phases), `LocalDbBootstrap` (reads `Engine.LocalDB`), `PersonalIndexer` (RecordDirectory walk), `RecordMapper`, `IndexCache` (disk cache I/O), `ChangeHooks` (live add/delete updates), `GroupInventories` (groups + optional Local Storage), `CloudWalker` (read-only direct listings), `LinkCrawler` (M4: opt-in crawl of your links' folders), `LocalDbProbe` (logs LocalDB queue waits), `RecordRegistry`, `Api` (diagnostic static methods). `src/Core/` also has `IndexParts` (index assembled from parts incl. per-link crawled content, change routing), `IndexCacheFile` (cache format), `LinkCrawlPlan` (`CrawlPlanner`, what to crawl), `MinIntervalGate` (rate limit).
- `tests/` — xunit tests for Core (`cd tests; dotnet test`). The mod builds against the game's own `Libraries\ResoniteModLoader.dll` (via `GameDir`); `lib/` is git-ignored scratch.
- Build: `cd src; dotnet build -c Release` (add `-p:DeployToGame=true` to copy into `rml_mods`; `-p:GameDir=...` if Resonite moved). Restart Resonite to load a new DLL (no hot reload for `rml_mods`).
- `src/Mod/Ui/` (M2) — `DashPatches` (Harmony hooks on `UserspaceScreensManager`), `SearchScreen` (layout, state, debounced queries, details pane), `ResultGrid` (virtualized grid: small recycled cell pool driven by `ScrollRect.NormalizedPosition`; y=0 is top), `ItemActions` (spawn/equip/open world/open folder/copy link, mirroring `InventoryBrowser`), `UiKit` (helpers).
- Diagnostics without UI: inspect via McpLink `call_method` with `typeName: "InventorySearch.Api"`, methods `Status()`, `Search(query, limit)`, `Bench(query, runs)` (all static), and via the `logs` tool (`[InventorySearch]` lines).

## Running the game (permissions and procedure)

- The user has **explicitly allowed** closing, deploying and launching Resonite as needed for testing. Never kill it: close gracefully (`Renderite.Renderer` owns the window; `CloseMainWindow()`), wait for all of `Resonite`, `Renderite.Host`, `Renderite.Renderer` to exit.
- **Launch in desktop mode: `Start-Process "<Resonite dir>\Resonite.exe" -ArgumentList '-Screen'`** (works while Steam is running). Do NOT use `steam://rungameid/...` or `steam://run/...`: Resonite has several launch options, so Steam waits on a chooser dialog and never starts the game. `-Screen` = `HeadOutputDevice.Screen` in `LaunchOptions.cs`.
- One command does it all: `tools\redeploy-resonite.ps1` (adapted from the Worlds tab project): close, build with `DeployToGame`, relaunch with `-Screen`.
- Settings live in `MonkeyLoader\Configs\InventorySearch.json`; the game rewrites it at shutdown, so edit it only while the game is closed, and restore it after any experiment.

## UIX gotchas (learned the hard way)

- **Never pass a delegate to `UIBuilder.Button(text, action)`** (or any API that assigns a `SyncDelegate`): it throws `Assigning method declared on non-data model type…` for plain C# classes/lambdas. Create the button without an action and use `button.LocalPressed += handler` (local-only event) — see `UiKit.TextButton`.
- Mod UI code runs inside engine callbacks; wrap each UI section in try/catch (`SearchScreen.Guard`) so a bug is logged by name instead of aborting everything. Errors show in `MonkeyLoader/Logs/MonkeyLog_*.log` (grep `[InventorySearch]`).
- **Never use `Elements.Core.MathX.Clamp` for ints**: `MathX.Clamp(5, 0, 0)` returns **-1** (verified by probe). It caused the "empty tab doesn't change the view" bug (negative cell-pool index → `ArgumentOutOfRangeException` inside `Refresh`, after the status text had already updated). Use `Math.Clamp`/`GridMath` (Core, unit-tested with a fuzz test).
- **`UIBuilder.Panel(...)` / `Image(...)` create a CHILD slot** under the rect you give them. If code later destroys "all children" of that rect (e.g. rebuilding a menu's rows), it deletes the background too. Put backgrounds in their own `Bg` child and skip it when clearing (`SearchScreen.RebuildScopeMenu`).
- **Text field placeholder:** `Text.NullContent` is drawn only while `Content` is **null**; `TextField("")` starts as `""`, so set `tf.TargetString = null` and `Editor.FinishHandling = NullOnWhitespace` so an emptied field returns to null. Enums for text alignment are in `Elements.Assets` (`using Elements.Assets;`).
- **`RecordDirectory.EnsureFullyLoaded()` is not read-only**: it makes the engine write each loaded record into LocalDB (lookup + update per record through LiteDB's single queue, shared with asset lookups). Don't use it to bulk-walk inventories; use `CloudWalker` (direct `Cloud.Records.GetRecords`). Only Local Storage (a patched disk read) still goes through `RecordDirectory`. `LocalDbProbe` logs LocalDB queue waits for the first 4 minutes.
- Exceptions inside `world.RunSynchronously` actions don't show in the MonkeyLoader log — read the engine log via McpLink `logs` (`level: error`) when something silently fails.
- The running game locks `rml_mods/InventorySearch.dll`: every code change needs close → deploy → relaunch.

## Status & next steps

- M1 (index + query engine) done and measured in-game. M2 (dash screen) is built and unit-tested at the Core level, but **the UI has not yet been run in-game** — first launch with 0.2.0 is its first test (see SPEC.md §9 for the checklist).
- Deploying: the running game locks `rml_mods/InventorySearch.dll`; close Resonite, then `cd src; dotnet build -c Release -p:DeployToGame=true`.
- M1–M4 are implemented and verified in game: index/query, dash screen with groups + Local Storage, disk cache + live updates + background refresh, and the opt-in link crawler (SPEC §9d, §9g). M5 (0.6.x: list view, keyboard navigation, Links panel with per-link switches and forced re-crawl, parallel scoring) is done; see SPEC §9h. `src/Mod/Ui/LinksPanel.cs` is the Links panel; `Api` has `Links/Recrawl/OpenLinksPanel/SelectIndex` diagnostics.
