# Moon Sweepers Dedicated Server

An unofficial dedicated server for **Moon Sweepers 1.0.5**, with a server browser **inside the game**. Host on your own PC or manage a server through the **AMP control panel**. Server packages are available for Windows and Linux. The host runs the real game headlessly and uses an anonymous Steam game-server identity.

You must own Moon Sweepers. Game files, BepInEx and the game patcher are **not bundled**. This project is not affiliated with the game's developers or Valve.

## Choose a download

Open this repository's [**Releases** page](https://github.com/MystifiedSky/moon-sweepers-dedicated-server/releases) and download the latest matching package. Use client and server files from the same release.

| Purpose | Download |
|---|---|
| Join a server | **moon-client.zip** |
| Host on Windows, including a local test | **moon-ds-windows-x86_64.zip** |
| Host on Linux | **moon-ds-linux-x86_64.zip** |
| Host through the AMP control panel | Use the template in `amp/`; AMP downloads the package for its host operating system |
| Manually update an already prepared client | **MoonDedicated.dll** and **MoonLoaderCompatibility.dll** |

For Vortex, import the client ZIP directly. For manual installation or hosting, extract the appropriate ZIP first. Each download has a matching `.sha256` checksum. Download release assets rather than GitHub's automatically generated **Source code** ZIPs. The main-branch release workflow builds the client and server packages together.

## Client requirements

- An owned Steam copy of **Moon Sweepers 1.0.5** on **Windows x64**.
- **Steam running and online** when joining a server.
- **BepInEx 6, Unity IL2CPP, Windows x64**. Use the build specified by the [Moon Sweepers BepInEx Patcher](https://www.nexusmods.com/moonsweepers/mods/9); Mono and CoreCLR loader packages are not interchangeable with the IL2CPP package.
- The [Moon Sweepers BepInEx Patcher](https://www.nexusmods.com/moonsweepers/mods/9), installed separately. Keep it installed alongside the compatibility patcher included here.
- Both included DLLs: `MoonDedicated.dll` and `MoonLoaderCompatibility.dll`.
- A compatible dedicated server, its address/query port, and its password if required. Use matching client/server releases and install any additional mods required by the host.
- **For Vortex installation only:** [Vortex](https://www.nexusmods.com/about/vortex/) and the [Moon Sweepers Vortex Extension](https://www.nexusmods.com/site/mods/2517). Vortex is optional for manual installation.

The game, BepInEx, the required Nexus patcher and the Vortex extension are separate requirements; they are not bundled in the client ZIP.

## Join a server

### With Vortex

1. Install Vortex and the [Moon Sweepers Vortex Extension](https://www.nexusmods.com/site/mods/2517), then select Moon Sweepers as a managed game. Restart Vortex if the extension requests it.
2. Install **BepInEx 6 Unity IL2CPP Windows x64** and the [Moon Sweepers BepInEx Patcher](https://www.nexusmods.com/moonsweepers/mods/9), following the patcher's instructions. Start the game once to finish loader setup, then close it. Follow any additional restart instructions on the patcher's page.
3. Download this mod using **Mod Manager Download**, or use Vortex's **Install From File** to select `moon-client.zip`. Keep the ZIP intact when importing it into Vortex.
4. Enable the mod and **Deploy Mods**. The ZIP installs the client plugin and compatibility patcher into their respective BepInEx folders; no PowerShell script is needed.
5. Start Steam and Moon Sweepers. Select **JOIN SERVER**, below **QUIT GAME**.

### Manually

1. Install the same BepInEx and Moon Sweepers patcher prerequisites, start the game once, then close it.
2. Extract `moon-client.zip` and copy its `BepInEx` folder into your own Moon Sweepers game folder, merging folders when prompted.
3. Confirm that the DLLs are in these locations relative to your game folder:

   ```text
   BepInEx/plugins/MoonDedicated/MoonDedicated.dll
   BepInEx/patchers/MoonDedicated/MoonLoaderCompatibility.dll
   ```

4. Start Steam and the game, then select **JOIN SERVER**.

When upgrading, close the game first and keep only one active copy of each included DLL. Remove older manual copies of these two DLLs from other locations before switching to Vortex. Keep the separate Moon Sweepers BepInEx Patcher installed.

### Connect

Choose **Add server**, enter a label and the host's address, such as `play.example.com:27026`, then save. Select the entry, refresh its details and choose **Join server**. Enter a password if the server requires one.

The browser, saved list and connection controls run inside the mod. No separate program needs to be open. The host supplies the address; there is no automatic public directory. **JOIN GAME** continues to use the original Steam lobby system. Required mods and versions are checked again by the server.

For the client ZIP's instructions, see [the client guide](docs/CLIENT.md). The [Nexus listing text](docs/NEXUS-DESCRIPTION.md) includes installation steps, requirements and credits.

## Host on your Windows PC

1. Prepare your installed game with BepInEx and the patcher as above. Keep Steam running for local testing.
2. Extract the Windows server package into a writable folder of your choice. Keep the executable, `mod` and other extracted folders together.
3. Run **moon-server.exe**. On first run it finds your installed game, or asks for its folder, then creates a separate server copy. Later runs reuse the same world and settings.
4. Wait for **[moon-ds] READY** in the console. In your game, join **127.0.0.1:27026**.
5. To shut down, type **stop** in the server console and wait for **STOPPED**. This saves first and makes a stop-backup. Avoid closing the console forcibly.

The first run creates:

| Path beside moon-server.exe | Purpose |
|---|---|
| `server/server.properties` | Name, ports, password, mods and other settings |
| `server/game/` | Separate game installation used by the server |
| `server/data/world/MoonSessions/slot-N/` | Server-owned world save |
| `server/data/backups/` | Backup ZIPs |
| `server/data/runtime/` | Status, command queue and logs |

Your client and server can run on the same Windows account. Server worlds are isolated from personal client saves. For multiple instances, configure separate game and data folders and different ports. Password and Steam-ID allow-list protection are **disabled by default** when their settings are blank.

A localhost test needs no router forwarding. Other players use your host's reachable address and query port. Allow the host through the appropriate firewall profile and forward the configured query UDP port when hosting behind a router. Gameplay uses Steam networking. A Windows host without a running Steam client needs Valve's Steamworks redistributable (SteamCMD app 1007); see [installation](docs/INSTALL.md).

## Host through the AMP control panel

The `amp/` folder contains an **AMP Generic-module template** for hosting and managing the dedicated server inside AMP. The template declares Windows and Linux host support; choose the host operating system that your AMP installation uses.

1. Import the template files from `amp/` into AMP and create a **Moon Sweepers (Dedicated)** instance.
2. Set **Release Repository** to this project's GitHub `owner/repository`, where the release packages are published.
3. In the instance's installation settings, select a prepared game folder containing BepInEx and the required patcher.
4. **Update** the instance, then **Start** it. Wait for **[moon-ds] READY** in the AMP console.
5. Use AMP to manage server settings, start/stop, console commands and scheduled saves/backups.

The template downloads the server package for the AMP host's operating system. On Linux, the Windows game runs through Wine and Xvfb. See [AMP setup details](docs/INSTALL.md#host-with-amp) and [all settings](docs/CONFIG.md). AMP deployment still needs runtime acceptance testing.

## Host on Linux without AMP

Linux runs the Windows game through **64-bit Wine and Xvfb**. Prepare an owned game copy with BepInEx and the patcher, then configure and start the manager:

```sh
./moon-ds init
# Edit server.properties: MOON_GAME_DIR, MOON_DATA_DIR and optional settings.
./moon-ds serve --config server.properties
```

See [Linux installation details](docs/INSTALL.md#host-on-linux) for prerequisites and [all settings](docs/CONFIG.md) for configuration options.

## Saves, backups and upgrades

* Worlds live under **`<MOON_DATA_DIR>/world/MoonSessions/slot-N`**. Each instance owns its data folder. Never run two writers against the same world.
* Type `save` for a world save, `backup` for a save followed by a snapshot backup, or `stop` for a graceful shutdown. Wait for `SAVED`, `BACKUP` or `STOPPED` as appropriate. A queued command alone does not confirm success.
* Before upgrading, stop the server cleanly and retain an off-machine backup. Replace release binaries and the DLL while the processes are closed. Keep `server/` and your configuration intact.
* Restore only while stopped: `moon-ds restore "backup.zip" --config "server/server.properties"`. Restore preserves the previous world in a uniquely named recovery folder.
* Releases before 0.1.2 used the normal Unity save root. New versions do not automatically copy or overwrite that old world. Back it up and restore it deliberately into the new server's configuration.

## Troubleshooting and validation

For the cleaning/saving investigation and the exact local tests performed for 0.1.6, see [the cleaning and persistence audit](docs/AUDIT-CLEANING-2026-11-14.md). Use matching updated client/server files. Old test marks that were already changed by a previous build remain changed in that save; evaluate new strokes on untouched ground.

* **No JOIN SERVER:** check `BepInEx/LogOutput.log`, verify game version 1.0.5 and ensure one `MoonDedicated.dll` is installed under `plugins`. The client needs the patcher as well as BepInEx.
* **Server unreachable:** wait for READY, refresh the entry, and check the **query** port, host address and firewall/NAT settings. Use `127.0.0.1` only on the host PC.
* **Join rejected:** match game/client/server versions and required mods; verify the password and allow-list. Keep Steam online. See the server console and `BepInEx/moon-ds/server.log` for admission details.
* **Save/backup failure:** retain logs and existing backups. The manager reports failures and exits nonzero after an unconfirmed final save; do not treat that shutdown as a successful backup.
* **Loader warnings about Class::Init or Unity module preloading:** install both DLLs from the complete release and restart. The compatibility patcher uses Unity's exported metadata API and repairs malformed generated nested-type metadata before loading. Originals remain beside the affected interop DLLs as `.before-moon-repair-<hash>`. Warning/error logging stays enabled. See [loader compatibility details](docs/LOADER-COMPATIBILITY.md).

Windows local hosting, Steam authentication, world download and isolated storage have been exercised. Linux compilation and automated manager/protocol regressions are checked. Linux/Wine runtime, AMP deployment, multiple simultaneous players and long-running vehicle/desync behavior still need acceptance testing. The embedded browser supports mouse and keyboard text entry; controller navigation is not implemented.

## Development and automatic releases

Build with a current Rust toolchain and the .NET SDK. Packaging also needs Python 3.11 or newer:

```powershell
dotnet restore mod/MoonDedicated --locked-mode
dotnet build -c Release mod/MoonDedicated --no-restore
cargo build --release --locked
.\scripts\package.ps1
```

Every push to **main** runs the release workflow. It stamps one unique numeric version into the client and both server builds, runs checks, then publishes all three packages and checksums together as a GitHub Release. Failed builds publish no release. No manual tag or personal access token is needed for the normal workflow. See [release setup, numbering and first-push instructions](docs/RELEASES.md).

Licence: [GPL-3.0](LICENSE.md). Acknowledgments: [CREDITS.md](CREDITS.md).
