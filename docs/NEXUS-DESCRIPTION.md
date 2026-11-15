# Moon Sweepers Dedicated Server — Client Mod

**Short description:** Join dedicated Moon Sweepers servers directly from the game, with a JOIN SERVER menu button, an in-game browser and saved server entries.

## Description

Join dedicated Moon Sweepers servers without keeping a separate browser application open.

This client mod adds **JOIN SERVER** to the main menu and provides connection controls inside the game. Add an address supplied by your host, save it to your list and connect from the browser.

Dedicated servers maintain their own world saves and can stay online independently of any player's game.

This download contains the **client mod**. Server software, hosting instructions and source code are available on [GitHub](https://github.com/MystifiedSky/moon-sweepers-dedicated-server). The server kit also includes a template for hosting through the AMP control panel.

Designed for **Moon Sweepers 1.0.5 on Windows x64**. Use matching client and server releases.

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

## Joining a server

Choose **Add server**, enter a label and the host's address/query port, save the entry, refresh its details and select **Join server**. Enter the password if required. For a server on your own PC, use `127.0.0.1:27026` with the default ports; other players need the host's reachable address.

## Main features

- **JOIN SERVER** button on the main menu.
- Server browser and connection controls inside the game.
- Saved server entries with custom labels and favourites.
- Server details and connection feedback.
- Password-protected server support.
- Server checks for game-version compatibility and required mods.
- No separate browser application needed while playing.

The host supplies the server address; the browser does not provide an automatic public server directory. The original **JOIN GAME** option continues to use the game's Steam lobby system.

## Requirements

- An owned Steam copy of **Moon Sweepers 1.0.5** on **Windows x64**.
- **Steam running and online** when joining a server.
- **BepInEx 6, Unity IL2CPP, Windows x64**. Use the build specified by the [Moon Sweepers BepInEx Patcher](https://www.nexusmods.com/moonsweepers/mods/9); Mono and CoreCLR loader packages are not interchangeable with the IL2CPP package.
- The [Moon Sweepers BepInEx Patcher](https://www.nexusmods.com/moonsweepers/mods/9), installed separately. Keep it installed alongside the compatibility patcher included here.
- Both included DLLs: `MoonDedicated.dll` and `MoonLoaderCompatibility.dll`.
- A compatible dedicated server, its address/query port, and its password if required. Use matching client/server releases and install any additional mods required by the host.
- **For Vortex installation only:** [Vortex](https://www.nexusmods.com/about/vortex/) and the [Moon Sweepers Vortex Extension](https://www.nexusmods.com/site/mods/2517). Vortex is optional for manual installation.

The game, BepInEx, the required Nexus patcher and the Vortex extension are separate requirements; they are not bundled in the client ZIP.

## Shout outs

- The Moon Sweepers developers for creating the game.
- **boverson777**, author of the [Moon Sweepers BepInEx Patcher](https://www.nexusmods.com/moonsweepers/mods/9).
- **ChemBoy1**, author of the [Moon Sweepers Vortex Extension](https://www.nexusmods.com/site/mods/2517).
- The BepInEx, Il2CppInterop and MonoMod contributors.
- Everyone helping test connections, cleaning synchronization and saving.

**Development disclosure:** OpenAI Codex was used for code generation, debugging, testing and documentation.

**Source, server downloads and bug reports:** [GitHub project](https://github.com/MystifiedSky/moon-sweepers-dedicated-server)

Unofficial community project. Not affiliated with or endorsed by the Moon Sweepers developers or Valve. Online play uses Steam authentication and exchanges player identity, connection information, installed-mod information and game state with the selected server.
