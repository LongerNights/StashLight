# StashLight

> **DISCLAIMER:** I’ve written applications and small mods for personal use before, but this mod was written almost entirely by Claude Code. It’s my first time using AI tools on a coding project. I wanted to see what they can really do, and I was surprised at every step. I know AI tooling is a polarizing topic, and I respect that people feel strongly about it either way. I’m reading through all the code myself, but there’s a lot of it. Since the mod is in early testing and release, please keep that in mind, and feel free to open an issue if you spot something off.

A Resonite mod that adds a **Search** tab to the dash for finding things in your inventory. It runs inside the game, searches an in-memory index, and needs no external server.

- Type to search; results update as you type, with thumbnails, type chips (Avatars, Worlds, Objects, …) and a details pane.
- **Scope:** Mine, All, or one group inventory (and Local Storage, if you use that mod).
- **Linked:** optionally include the public folders your inventory links point to (off by default).
- Double-click an item to spawn it; avatars can be equipped, worlds opened, facets spawned onto the dash, links copied.
- Search syntax: plain words, `"exact phrase"`, `-exclude`, `type:avatar`, `tag:name`, `in:folder`, `by:owner`, `is:mine`, `is:linked`, `is:group`. Typos fall back to fuzzy matching.

## How it works

**Indexing.** Your inventory is flattened into one in-memory list (name, path, type, tags, thumbnail, owner, date). It is built from independent parts, so each can be refreshed alone:

1. Records already in the engine's local database, read in bulk. This is quick and needs no network.
2. Your folder tree, listed read-only through the cloud records API in the background, with a few folders loading at once.
3. Group inventories you belong to, and Local Storage if installed.
4. *(Optional)* **Linked folders.** The crawler follows your link records into other owners' public folders slowly (about 5 requests a second, depth- and size-limited), one link at a time. Use the **Links…** panel to switch single links off or re-crawl them.

Changes you make in-game (saving, deleting) update the index live. A periodic background refresh catches the rest.

**Caching.** The index is saved to disk (Brotli-compressed JSON, in an `StashLight` folder inside Resonite's local database folder), so the next start restores it in about half a second and the cloud refresh runs later in the background.

**Searching.** A query scans the index in memory. Names are normalised, words match by prefix, and results are ranked (exact > prefix > substring, with a small boost for your own items). Copies of the same item in a folder collapse into one row (×N) using the newest copy. Large indexes are scored on several cores, and on 100k entries a query takes roughly 5–15 ms.

## Install

Requires Resonite with [MonkeyLoader / ResoniteModLoader](https://github.com/resonite-modding-group/ResoniteModLoader). Copy `StashLight.dll` into `<Resonite>\rml_mods` and restart the game.

## Build

```
cd src
dotnet build -c Release                      # builds
dotnet build -c Release -p:DeployToGame=true # also copies the DLL into rml_mods
```

The project references the game's own DLLs. If Resonite isn't at the default Steam path, pass `-p:GameDir="C:\path\to\Resonite\"` (trailing backslash). Tests (engine-free core only): `cd tests; dotnet test`. `tools\redeploy-resonite.ps1` closes the game, builds, deploys and relaunches in desktop mode.

## Settings

Under **Mods** in the dash (ResoniteModSettings), or `MonkeyLoader\Configs\StashLight.json`. They cover animations, refresh timing, the disk cache and the link-crawl limits.

## Docs

[SPEC.md](SPEC.md) has the architecture, verified engine facts, measurements and milestones. [CLAUDE.md](CLAUDE.md) holds development notes and pitfalls.

## License

[MIT](LICENSE)
