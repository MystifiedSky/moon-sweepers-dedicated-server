# Install the Moon Dedicated client

## Requirements

- An owned Steam copy of **Moon Sweepers 1.0.5** on **Windows x64**.
- **Steam running and online** when joining a server.
- **BepInEx 6, Unity IL2CPP, Windows x64**. Use the build specified by the [Moon Sweepers BepInEx Patcher](https://www.nexusmods.com/moonsweepers/mods/9); Mono and CoreCLR loader packages are not interchangeable with the IL2CPP package.
- The [Moon Sweepers BepInEx Patcher](https://www.nexusmods.com/moonsweepers/mods/9), installed separately. Keep it installed alongside the compatibility patcher included here.
- Both included DLLs: `MoonDedicated.dll` and `MoonLoaderCompatibility.dll`.
- A compatible dedicated server, its address/query port, and its password if required. Use matching client/server releases and install any additional mods required by the host.
- **For Vortex installation only:** [Vortex](https://www.nexusmods.com/about/vortex/) and the [Moon Sweepers Vortex Extension](https://www.nexusmods.com/site/mods/2517). Vortex is optional for manual installation.

The game, BepInEx, the required Nexus patcher and the Vortex extension are separate requirements; they are not bundled in the client ZIP.

## Installation instructions

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

## Connect

Add a label and the host's address/query port, for example `play.example.com:27026`. Save, select the entry, refresh its details and choose **Join server**. Enter the password if required.

The browser stays inside the game. No external browser executable is required. Saved servers can be edited, deleted or marked as favourites. Passwords are not saved in that list; the transient join request is deleted when the mod reads it. Mouse navigation and keyboard text entry are supported; controller navigation is not implemented.

For a server on the same PC, use `127.0.0.1:27026` and wait for its host console to report READY. Other players need the host's reachable address, not `127.0.0.1`. The original **JOIN GAME** entry continues to use Steam lobbies.

Use matching client/server releases and install any required mods before joining. If the menu entry is missing, inspect `BepInEx/LogOutput.log` for plugin/patcher errors and verify the supported game version. Connection results and logs are under `BepInEx/moon-ds/`.

This ZIP contains the client plugin, loader compatibility DLL and installation instructions. It does not include the game, BepInEx or the Nexus patcher. The compatibility DLL repairs malformed generated Unity module metadata while preserving backups; it leaves warning/error logging enabled. Licence and third-party acknowledgments are included separately.
