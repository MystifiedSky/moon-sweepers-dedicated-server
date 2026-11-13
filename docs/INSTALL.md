# Install

You need a copy of Moon Sweepers 1.0.5 that you own. The server runs **a copy of your own game folder** (the game has no separate server download). Do not share or publish that copy.

## 0. Prepare a game folder with the mod (once)

1. Make a copy of your `Moon Sweepers` folder (or use the Steam one on the host).
2. Install **BepInEx 6 (IL2CPP, Windows x64)** and the **MoonSweepers BepInEx Patcher** from Nexus (https://www.nexusmods.com/moonsweepers/mods/9) into that folder, following the patcher's page. Start the game once so BepInEx creates its `interop` folder, then close it.
3. Put `MoonDedicated.dll` in `BepInEx/plugins/MoonDedicated/` and `MoonLoaderCompatibility.dll` in `BepInEx/patchers/MoonDedicated/`. Keep the Nexus patcher too. The game's **JOIN SERVER** button opens the browser inside the mod; no companion executable is required for players. (`moon-ds serve` installs both DLLs for hosts.)

`moon-ds check` tells you what is missing.

## Host on Windows

For a simple local test, extract the kit into a writable folder and run **moon-server.exe**. Keep its `mod/` folder beside it. On first run it detects your installed game (or asks for its folder) and copies it into `server/game`. That source game must already have BepInEx and the Moon Sweepers patcher installed. The server gets `server/server.properties`, `server/data/world` and `server/data/backups`. Later runs reuse those files. Wait for `[moon-ds] READY`, then join **127.0.0.1:27026** through the game's JOIN SERVER menu. Type `stop` in the host console to save, back up and exit. Closing the console forcibly is not a graceful stop.

Your client and server can run together on the same Windows account. **Each server uses `<MOON_DATA_DIR>/world/MoonSessions/slot-N`**, and never loads your personal Unity slot automatically. Different data directories give different worlds. A shared lock prevents concurrent access to the same server storage, including restore and offline backup. To migrate an older world, stop its old host, make a backup, then restore that backup with this server's configuration; old client/default Unity saves are not moved or overwritten automatically.

For manual setup or AMP-like testing:

```powershell
moon-ds init
# Set MOON_GAME_DIR to a separate prepared game copy; set MOON_DATA_DIR for this instance.
moon-ds serve --config server.properties
```

* Steam must be running on a local testing PC. A standalone host without Steam needs Valve's Steamworks redistributable: download app **1007** with SteamCMD (`+login anonymous +app_update 1007`), then set `--steam-redist-dir <that folder>` once.
* A localhost test needs no router forwarding. Other machines use your host's address and configured query port; allow the host application through the appropriate Windows firewall profile. Gameplay uses Steam networking; query UDP 27026 must reach the manager.
* `moon-server.exe --config path/to/server.properties` starts a configured instance directly. The advanced `moon-ds serve` entry remains supported for AMP and automation.

## Host on Linux

Install `wine` (64-bit) and `xvfb`. Then:

```
./moon-ds init
nano server.properties
./moon-ds serve --game-dir /srv/moon/game --steam-redist-dir /srv/moon/redist
```

The game runs as `wine MoonSweepers.exe -batchmode -nographics`, with a virtual display (Xvfb) started automatically if `DISPLAY` is not set. The Wine prefix defaults to `data/wine`; AMP uses `moon-data/wine`. No GPU, no Steam client.

Console commands (type them, or use `moon-ds send <cmd>` from another shell): `help`, `status`, `players`, `save`, `backup`, `say <text>`, `kick <steamid>`, `stop`. `Ctrl+C`/`SIGTERM` = `stop`: the world is saved first, then a backup is made.

## Host with AMP

1. Put the five files from `amp/` in AMP's configuration repository folder (or import them as a Generic-module template), and create an instance from **Moon Sweepers (Dedicated)**.
2. Settings â†’ "Installation": pick *Copy from a folder* and give the prepared game folder from step 0 (recommended), or *SteamCMD* with your own Steam login set in AMP.
3. **Update** the instance, then **Start**. When the console shows `[moon-ds] READY` the server is up.
4. All options appear in AMP's settings (name, password, allowed Steam IDs, ports, required mods, backups ...). AMP writes them to `server.properties`; you can also edit that file by hand. The template is **not required** â€” `moon-ds serve` runs the same way outside AMP.
5. Scheduled tasks: add a task that sends the console command `backup` (saves, then makes a zip in `moon-data/backups`) or `save`. Use AMP's own backup system as well if you like; run `save` just before it.

`amp/moonsweepersupdates.json` downloads `moon-ds` from the GitHub release of the repository named in the "Release Repository" setting. Set it to wherever you publish the release zips. `scripts/package.ps1` builds the Windows archive; the release workflow builds Windows and Linux archives.

## Player

1. Install BepInEx + patcher + `MoonDedicated.dll` in your own game folder (step 0).
2. Start Moon Sweepers and choose the new **JOIN SERVER** menu row (below Quit Game). It opens the dedicated server browser **inside the game**. Choose **Add Server**, give it a label, and enter an address like `play.example.com:27026`. Save the entry, wait for its details, then choose **Join Server**. Enter the password if requested; leave the game at its main menu while it connects. The address comes from the server's host, and `27026` is the default **query** port. The existing **JOIN GAME** entry continues to use the game's original Steam lobby flow.
3. You can optionally run `moon-ds` directly as a separate browser. Favourite (`*`) and rename servers as you like; the list is saved in `%APPDATA%\MoonDS\servers.json` (`~/.config/moon-ds/` on Linux). The in-game browser reads the same saved-list file. The standalone browser remains optional. Linux/Wine client UI acceptance is not yet tested.
4. If the server needs mods you do not have, you get a notice listing them and cannot join until you install them. Start the game once with the mod so the browser knows what you have installed.
5. Password-protected servers ask for the password in the browser. It is not stored in the saved server list; it is handed to the mod in a temporary `connect.json` request, which the mod deletes when it reads it.

You can also drop a `connect.json` yourself; see `docs/HOW-IT-WORKS.md`.
