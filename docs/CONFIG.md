# Settings

Order of precedence: built-in defaults < `server.properties` < environment variables < command-line options.
Every setting has one name: `MOON_SERVER_PASSWORD` in the file and the environment, `--server-password` on the command line. Quotes around values are optional (`MOON_SERVER_PASSWORD=''` is the same as an empty value).

| Setting | Default | Meaning |
|---|---|---|
| `MOON_SERVER_NAME` | Moon Sweepers Dedicated Server | Name players see (max 63 chars). |
| `MOON_MOTD` | *(empty)* | Short message shown in the browser. |
| `MOON_SERVER_PASSWORD` | *(empty = **disabled**)* | Join password, up to 128 printable ASCII characters. |
| `MOON_ALLOWED_STEAM_IDS` | *(empty = **disabled**)* | Comma-separated SteamID64s (max 15). Anyone else is refused. A typo in the list blocks everyone instead of silently opening the server. |
| `MOON_GAME_PORT` | 27025 | UDP, Steam game-server port. 1024–65535, different from the query port. |
| `MOON_QUERY_PORT` | 27026 | UDP, server-browser/query port. Players join with `address:<this>`. |
| `MOON_MAX_PLAYERS` | 8 | 1–15 players besides the server. |
| `MOON_SLOT` | 1 | Which of the game's 5 save slots the server uses. |
| `MOON_AUTOSAVE_MINUTES` | 10 | World save interval; 0 = only on leave/stop/command. |
| `MOON_REQUIRED_MODS` | *(empty)* | BepInEx plugin GUIDs players must have: `a.b@1.2.0,c.d`. |
| `MOON_MOD_POLICY` | required | `required`: needs those mods, extras allowed. `strict`: no other mods. `off`: no check. |
| `MOON_BACKUP_KEEP` | 10 | Backups to keep (`moon-data/backups/*.zip`). 0 = keep all. |
| `MOON_BACKUP_ON_STOP` | true | Backup after each clean stop. |
| `MOON_GAME_DIR` | `game` | Game folder (with BepInEx). |
| `MOON_DATA_DIR` | `data` | Runtime files, backups, Wine prefix. |
| `MOON_BACKUP_DIR` | `<data>/backups` | Where backups go. |
| `MOON_STEAM_REDIST_DIR` | *(empty)* | Folder holding Valve's Steamworks redistributable (app 1007). |
| `MOON_SERVER_FPS` | 30 | Frame cap of the headless game, 10–120. Steady pacing keeps the simulation in step with players. |
| `MOON_STEAM_LISTING` | false | Advertise in Steam's public server list. |
| `MOON_WINE` | wine | Linux: wine executable. |
| `MOON_STOP_TIMEOUT` | 120 | Seconds allowed for the final save before the game is ended. |
| `MOON_AUTO_RESTART` | 0 | Restarts after a crash. |
| `MOON_CONFIG` | `server.properties` | Path of the settings file (or `--config`). |

Console lines the manager prints (used by AMP's regular expressions): `[moon-ds] READY …`, `JOIN name (id)`, `LEAVE name (id)`, `SAVED`, `BACKUP file`, `STOPPED`.
