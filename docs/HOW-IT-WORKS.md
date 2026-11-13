# How it works

## Why the server is the game itself
Moon Sweepers has no server build. Its multiplayer is host-authoritative over Steam: one player (the host) owns the world, others download it and then talk to the host through Steam's networking-messages API. A dedicated server therefore has to *be* a host. This kit runs the real game with `-batchmode -nographics` and the `MoonDedicated` BepInEx plugin, which:

* logs in to Steam as an **anonymous game server** (`SteamGameServer_Init`, `LogOnAnonymous`) using the game ports you configure — no Steam account, no extra licence, and the GameServer ID is what players connect to;
* presents itself to the game as the lobby host (the game thinks a lobby exists with the server as host and the authenticated players as members);
* starts the world from the chosen save slot, with the few GPU-only calls turned into no-ops (progress `Texture2DArray`, main menu, intro) and a 30 fps cap;
* admits players on its own control channel (Steam messages channel 3): Steam auth ticket (validated by Steam, plus a licence check for app 5335510) → game version → capacity → password → **mod check** → roster. The game's own traffic uses channels 0, 1 and 2 (decompiled receive pump, `GameAssembly 0x1807aa690`) and is untouched; the game ignores messages from anyone who is not in its member list, so every admitted player is listed as a member.

## Joining by address
The default private server has a manager-owned A2S_INFO/A2S_RULES endpoint on its configured query port. It uses challenge cookies and bounded replies and reads the mod's fresh state for the Steam ID and player counts. This keeps address queries working without enabling Steam master-list advertising. When `MOON_STEAM_LISTING=true`, Steam owns the configured query port instead. Rules include `moon_ds`, `moon_ver`, `moon_policy`, `moon_mods`, `moon_motd` and, for manager queries, `moon_ready`.

The player's **JOIN SERVER** button opens a browser drawn inside Unity by the mod. Saved entries, address input, favourites, passwords, requirements and join controls run in the same game process. It queries on a bounded asynchronous worker and hands a transient request to the existing player transport. The optional standalone `moon-ds` browser uses the same bookmark format and request channel. No separate program is launched by the game menu.

`connect.json`:
```json
{ "address": "host:27026", "query_port": 27026, "steam_id": "9027…", "password": "", "name": "My server", "created_unix_ms": 0 }
```
The mod deletes the file as soon as it reads it. The result is written to `BepInEx/moon-ds/last-join.json`, which the browser shows.

## Required mods
`MOON_REQUIRED_MODS` lists BepInEx plugin GUIDs (optionally `@version`). The browser shows them before joining, compared with `BepInEx/moon-ds/installed-mods.json` (written by the mod at every game start). The server enforces it again: the client sends its plugin list in the join request and gets a refusal naming exactly what is missing / wrong version / not allowed (strict policy).

## Saving, vehicles and Joonas
* The server saves with the game's own `MoonWorld.SaveProgress(complete: true)` on a timer, on the `save`/`backup` commands, before stopping, **3 s and 25 s after any player leaves**, and `moon-ds` zips the save folder after a clean stop.
* **Why vehicles were lost (found by decompiling).** The game's own `MoonSteamLobby.RefreshMembers` (`0x18059e1a0`) rebuilds the member list from Steam and then raises a static event so the crew session notices who joined or left. A server has no Steam lobby, so the mod replaces that method. Replacements that only rewrite the list (as the other server project does) never raise the event, so the game is never told a player left and never runs its leave handling (the vehicle hand-back and the leave save). This kit raises the same event (`Game.RaiseLobbyChanged`) whenever the list changes, and also saves 3 s and 25 s after a leave as a safety net.
* This is the best-supported explanation, **but it is not yet confirmed in the running game**: the vehicle-save classes themselves could not be located in the stripped DLL (see `MECHANICS.md` section E).
* "Joonas" desync: the server runs a steady frame cap (`MOON_SERVER_FPS`, default 30) instead of an uncapped loop, re-applied every 5 s because the game's own frame-pacing code can reset it. If it is not enough, `BepInEx/moon-ds/class-dump.txt` (written on every server start) lists the members of the vehicle, Joonas, crew-session and save classes so the next fix can be written against facts.

## Files
Server worlds: `data/world/MoonSessions/slot-N` (the mod redirects the server process's persistent data path before world types load). Server: `data/runtime/state.json` (written by the mod every second), `data/runtime/commands/*.cmd` (written by `moon-ds`), `data/runtime/game.log`, `BepInEx/moon-ds/server.log`, `data/backups/*.zip`.
Player: `BepInEx/moon-ds/{installed-mods.json,connect.json,last-join.json,client.log}`, browser list in `%APPDATA%\MoonDS\servers.json`.

The manager resolves paths to absolute paths before spawning the game and holds an OS lock for the server lifetime. External `send` commands use a separate manager queue, so stop and backup follow the same sequence as console commands. While a server is running, `moon-ds backup` queues an asynchronous snapshot backup and returns once queued; wait for `BACKUP <file>` in the manager log to confirm completion.

Live backups zip an immutable snapshot that the plugin publishes after a successful save task. New archives include a file inventory and creation metadata. Retention applies per slot to recognized new-format backups; older archives and unrelated ZIPs are preserved. Restore validates and extracts to a staging directory before swapping the world, keeping a unique recovery directory. Earlier kit archives remain restorable when they have the expected archive name and a root `slot.json` or `session.moon` save marker.

The plugin validates the running game version before installing hooks. Hook installation uses BepInEx's IL2CPP native provider and rolls back if any required hook fails. A save timeout keeps the original writer task tracked until it completes; it does not start an overlapping save. Failed or forced shutdown exits the manager with a nonzero status.

## What is untested
Current validation and remaining acceptance checks are listed in `AUDIT-SECOND-FIXES-2026-11-14.md`. The following list records the initial build's limitations.
Everything that needs the live game and two Steam accounts could not be run while this was built:
0. (Checked by decompiling instead of running: every IL2CPP and Steamworks function the mod calls exists, and the argument shapes of the message and ticket stubs match. See `MECHANICS.md`.)
1. BepInEx IL2CPP loading on Unity 6000.6 (the Nexus patcher is expected to handle it).
2. All the game hooks (found by name, as listed in `Game.cs`; a renamed method disables the mod with a clear log line).
3. That Steam answers queries on the query port with the SteamID when the server is not advertised (`moon-ds query <ip>:27026` tells you; `MOON_STEAM_LISTING=true` is the fallback).
4. Wine + Xvfb headless start on Linux, and the AMP update stages.
5. The vehicle-persistence and Joonas fixes.
What *has* been run: the manager (config, start/stop/save/backup/join-reporting against a stand-in game), the query client/responder, the server browser, and the mod compiles.

## Limits
* Versions before 0.1.2 used the default Unity save root. Version 0.1.2 uses per-instance server world storage; migrate older worlds deliberately using backup/restore.
* `say` is logged but not shown in-game yet. The embedded browser currently supports mouse navigation and text entry; controller navigation is not implemented.
* Pinned to game build 1.0.5; after a game update the mod refuses to load rather than guess.
