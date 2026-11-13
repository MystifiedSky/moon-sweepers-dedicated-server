# Audit: issues, optimisations, improvements

Done after the decompile pass (2026-11-13). "Fixed" items are in this build; "Open" items are not.

## Fixed
| # | Severity | Finding | Fix |
|---|---|---|---|
| 1 | High | The server's replacement of the game's member refresh never raised the lobby change event that the real `RefreshMembers` (0x18059e1a0) raises, so the game was never told when players left (likely the "vehicles don't save when a player leaves" bug). | `Game.RaiseLobbyChanged()` after every roster change, server and player side. |
| 2 | High | Player side consumed `connect.json` before the main menu was up, so a join started during the intro was lost. | Request is kept until the menu is ready. |
| 3 | Medium | A `save` command sent before the world was ready flagged the server as failed (`ready=false`) until the next save. | Logged and ignored instead. |
| 4 | Medium | `SteamInternal_GameServer_Init` fails when the app id is 0 (0x13b408be0). | `steam_appid.txt` written by `prepare`/`serve` in addition to the env vars. |
| 5 | Medium | Fixed Xvfb display `:97` clashed when several servers ran on one Linux host. | Display = 100 + query port % 800. |
| 6 | Medium | A forced stop left Wine helper processes running. | Own process group; the whole group is killed. |
| 7 | Medium | Two `moon-ds serve` on one data folder fought over the status/command files. | Start refuses while another server's status file is fresh. |
| 8 | Low | Server browser re-read files and scanned the plugin folder every frame. | 2-second cache. |
| 9 | Low | Backups loaded whole files into memory. | Streamed into the zip. |
| 10 | Low | State file rebuilt the plugin list every second; the frame-cap call ran 4×/s. | Cached; re-applied every 5 s. |
| 11 | Low | Wrong assumption in the docs: game channels are 0, 1, 2 (not 1, 2). | Corrected from the decompiled pump (0x1807aa690). |
| 12 | Low | Send flags were guessed. | Taken from `SendBytes` 0x1807abcd0: reliable 0x28, unreliable 0x25. |

## Open (worth doing next)
* **Rate limit join requests** per SteamID (a client can spam `AuthRequest`; each is cheap but unbounded).
* **Live test** of everything in `docs/HOW-IT-WORKS.md#what-is-untested`, especially that raising the lobby event makes the game release and save a leaver's vehicles.
* **Locate the vehicle/Joonas code** once a tool can read IL2CPP metadata v108 (or from a runtime class dump: the server writes `BepInEx/moon-ds/class-dump.txt`).
* **Mod check is by plugin GUID and version only**, not file hash: a determined player can fake it. Fine for friendly servers, not for competitive ones.
* **Secrets**: the join password travels in the server's environment (same user only) and, for a moment, in `connect.json` on the player's PC. Steam messages are encrypted in transit.
* **Release integrity**: the AMP stages download release zips without a checksum. Publish SHA-256 sums and add a verify step when a release process exists.
* `say` is only logged; no in-game broadcast. No in-game server list (the browser is a separate window).
* Backups cover `MoonSessions/slot-N` only, not the game's own `Backups/slot-N`.
* No automatic restart on a *frozen* (not crashed) game: the manager only warns when the heartbeat goes stale.
