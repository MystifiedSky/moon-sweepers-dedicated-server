# Install the Moon Dedicated client

Requires an owned copy of **Moon Sweepers 1.0.5**, Steam, **BepInEx 6 IL2CPP Windows x64**, and the Moon Sweepers BepInEx Patcher: https://www.nexusmods.com/moonsweepers/mods/9 . Follow the patcher's installation instructions, start the game once, then close it.

1. Extract this ZIP.
2. From the extracted folder, run:

   ```powershell
   .\scripts\install-client.ps1 -GameDir "D:\SteamLibrary\steamapps\common\Moon Sweepers"
   ```

   For manual installation, copy `mod/MoonDedicated.dll` into `BepInEx/plugins/MoonDedicated/` and `mod/MoonLoaderCompatibility.dll` into `BepInEx/patchers/MoonDedicated/`. Keep the Nexus patcher installed too. Replace files with the game closed; keep one active copy of each DLL.
3. Start Steam and the game. Choose **JOIN SERVER** below **QUIT GAME**.
4. Add a label and the host's address/query port, for example `play.example.com:27026`. Save, select the entry, refresh its details and choose **Join server**. Enter the password if required.

The browser stays inside the game. No external browser executable is required. Saved servers can be edited, deleted or marked as favourites. Passwords are not saved in that list; the transient join request is deleted when the mod reads it. Mouse navigation and keyboard text entry are supported; controller navigation is not implemented.

For a server on the same PC, use `127.0.0.1:27026` and wait for its host console to report READY. Other players need the host's reachable address, not `127.0.0.1`. The original **JOIN GAME** entry continues to use Steam lobbies.

Use matching client/server releases and install any required mods before joining. If the menu entry is missing, inspect `BepInEx/LogOutput.log` for plugin/patcher errors and verify the supported game version. Connection results and logs are under `BepInEx/moon-ds/`.

This ZIP contains the client plugin, loader compatibility DLL and installation instructions. It does not include the game, BepInEx or the Nexus patcher. The compatibility DLL repairs malformed generated Unity module metadata while preserving backups; it leaves warning/error logging enabled. Licence and third-party acknowledgments are included separately.
