# Moon Sweepers second audit and menu integration

This pass found **13 issues: one P1 and twelve P2**. Two P2 issues are addressed in the 0.1.1 client changes: the missing menu entry and the pending-join workflow. **Eleven findings remain open**, including gaps in the earlier fixes. Nine open findings have executable reproductions; two follow from source inspection. P1 means resolve before hosting multiple persistent Windows instances under one account. P2 means an important conditional reliability or usability defect.

The audit covered the current Rust manager/browser, C# transport and join state, backup/restore, imports, configuration, process supervision, packaging and installation. The original regression suite still passes; those tests did not cover the cases below. No world was started during client UI testing.

## Joining a dedicated server

The previous client had no dedicated-server menu control. Its normal workflow required manually running the separate `moon-ds` browser. The game's existing **JOIN GAME** menu opens the original Steam lobby flow.

The new Windows client adds a native **JOIN SERVER** row below Quit Game, built through the game's own `MoonMainMenu.HomeRow` and layout methods. The button opens the dedicated browser installed beside the DLL. Mouse activation, browser launch, the Add Server dialog and mouse cancellation were observed on the user's client. Keyboard/controller activation of the new row has not been established by this audit.

To join: **JOIN SERVER → Add Server → enter a label and the host's address/query port → Save → select the server → Join Server**. The default query port is **27026**. The host must supply the address; there is no automatic public dedicated-server directory. Keep the game at its main menu while connecting. Password and mod checks still apply.

Install both `MoonDedicated.dll` and `moon-ds.exe` in `BepInEx/plugins/MoonDedicated/`, or use `scripts/install-client.ps1` with the game closed. The native launcher targets Windows; Linux clients continue to use the separate browser. Failure to start the companion browser is surfaced through the game's own dialog. The new launcher also supplies its actual game directory to the browser, avoiding reliance on Steam-library autodetection.

## Findings and disposition

| ID | Priority | Issue | Status |
|---|---|---|---|
| B01 | P2 | No in-game entry point for dedicated joining | Implemented and mouse-tested |
| B02 | P1 | Separate Windows data directories can share the same world slot | Open; path reproduction |
| B03 | P2 | A backup retry consumes a previous snapshot error and aborts prematurely | Open; supervisor/child reproduction |
| B04 | P2 | Pending joins ignore replacement requests and can wait indefinitely for a world | Implemented; native reconnect acceptance still required |
| B05 | P2 | Import skips different content with equal size and timestamps | Open; file reproduction |
| B06 | P2 | Extra game arguments destroy quoting | Open; parser reproduction |
| B07 | P2 | Corrupt browser state silently resets and can be overwritten | Open; file reproduction |
| B08 | P2 | A truncated A2S rule value is accepted as empty | Open; packet reproduction |
| B09 | P2 | Browser refresh has no worker bound, deduplication or result generation | Open; source inspection |
| B10 | P2 | Backup creation can produce a manifest its own restore rejects | Open; archive reproduction |
| B11 | P2 | Player names can inject apparent manager console events | Open; supervisor/child reproduction |
| B12 | P2 | Options before the restore/import positional argument are misparsed | Open; CLI reproduction for restore |
| B13 | P2 | Linux Xvfb display allocation collides and helper cleanup misses spawn failure | Open; source inspection |

### B02 Shared world storage bypasses the runtime lock

**Sources:** `src/supervisor.rs`, `Supervisor::run` and `guess_save_dir`; `src/state.rs`, `lock_runtime`.

On Windows, two settings objects with different `data_dir` values resolve to different runtime lock files but the same `%USERPROFILE%/AppData/LocalLow/DragDropGames/MoonSweepers/MoonSessions/slot-N`. Both instances can obtain their data locks and then write the same world. The same limitation applies to a client and server running under the same Windows account. Existing installation guidance warns about shared saves, but the code does not enforce isolation.

The diagnostic compares the paths only; it does not start two live writers. Linux's default distinct Wine prefixes behave differently, so this reproduction specifically establishes the Windows collision. Acquire an additional exclusive lock keyed to the actual save directory, shared by serve/restore/offline backup, or introduce a verified per-instance save root. Do not rely on different data directories alone.

### B03 Backup retry acts on stale failure state

**Source:** `src/supervisor.rs`, console backup handling and the pending-backup state machine.

The supervisor reads state, queues `snapshot`, and immediately examines that same previously read state. If it contains `save_failed=true` or a nonempty `snapshot_error`, the new request is marked failed before the plugin can clear the old error. The plugin may successfully create the new snapshot afterward, leaving an orphaned directory and no backup. A subsequent retry may then work, which makes this intermittent to operators.

The diagnostic runs the actual Supervisor with a small isolated child that starts with an old snapshot error and successfully handles the new snapshot command. The manager still logs failure and no BACKUP event. Add a unique request ID and require a matching snapshot acknowledgment/error; timestamps alone do not identify which operation failed. Preserve error details in the manager result.

### B04 Pending join replacement and total wait

**Source:** `mod/MoonDedicated/PlayerClient.cs`, `Tick`, `PollRequest`, `OnServerMessage` and `Disconnect`.

Previously, request-file polling happened only while inactive. A second browser selection was ignored during an existing attempt. In addition, each `WaitWorld` refreshed `lastHeard`, so a reachable server that never finished starting could hold the client indefinitely. There was no overall deadline covering that phase.

The client now polls replacement requests at the menu, validates the new request before disconnecting the old attempt, closes the old Steam messaging session, and bounds the overall attempt to five minutes. Requests remain queued while loading or in a world. The existing world-download deadline still applies. Builds, hook validation and idle/menu startup pass. Real replacement during Steam authentication, world download and reconnect still need an actual server acceptance test; they are not represented as completed multiplayer tests.

### B05 Import treats metadata equality as content equality

**Source:** `src/import.rs`, `same`.

The copy skips a file if its length matches and the destination modification time is at least the source's. Two different DLLs with equal sizes and timestamps are therefore treated as up to date. The reproduction writes `newer` to the source and `stale` to the destination, sets equal timestamps, and confirms the stale contents remain while the skipped count increases.

Use a content hash or byte comparison for candidate matches. Timestamps can optimize the comparison but should not establish equality when incorrect game/mod payloads could survive an update.

### B06 Extra arguments lose quoted values

**Source:** `src/config.rs`, `Settings::apply`, `MOON_EXTRA_ARGS`.

Splitting on whitespace changes `--custom-path "two words"` into three arguments, retaining literal quote fragments. The game receives a broken path rather than one value containing a space. The parser probe confirms the exact resulting vector. Prefer an explicitly documented structured argument array, or a parser whose quoting and Windows-path escaping rules are defined and tested. Do not pass these arguments through a shell to compensate.

### B07 Corrupt browser state is silently overwritten

**Source:** `src/client.rs`, `Store::load` and `Store::save`.

Read and JSON errors collapse to an empty default store. A later save overwrites the damaged file without preserving it or telling the user. Writes are also direct replacements rather than staged writes. The isolated probe creates truncated server JSON, loads defaults, saves, and confirms the original server text was removed. It uses a private `MOON_DS_HOME`, not the user's bookmarks.

Preserve malformed data in a uniquely named recovery file, show a recoverable error, and use an atomic, flushed replacement for normal writes. Multiple browser instances also need a merge/locking policy to avoid lost updates.

### B08 Truncated rule strings become empty requirements

**Source:** `src/a2s.rs`, `parse_rules`.

The value parser uses `cstr().unwrap_or_default()`. Removing the terminator from the final `moon_mods=required.mod@1` rule still yields a successful parse with an empty value. The browser then shows no required mods. The server's independent authentication check remains authoritative, so this is not demonstrated as an admission bypass.

Require complete values and reject malformed replies. The packet probe constructs a valid rules reply, removes its final byte and confirms the empty-value result.

### B09 Refresh work and stale results are unbounded

**Source:** `src/gui.rs`, `refresh_all`, `refresh_one` and `pump`; `src/a2s.rs`, `resolve` and `query`.

Each refresh creates a new native thread per server, including when a query for that address is already pending. Responses carry only the address, so an older query can overwrite the result of a newer refresh. DNS resolution occurs before the socket-query timeout begins, which also means the advertised socket timeout does not bound the entire job.

This is a source finding; the user's client was not stress-tested to resource exhaustion. Use a bounded worker pool, coalesce outstanding queries and associate results with a generation. Define a total-resolution/query deadline and report stale state explicitly.

### B10 Creation and restore disagree on aggregate limits

**Source:** `src/backup.rs`, `create`, `manifest` and `restore`.

Restore refuses a manifest larger than four MiB, but creation never checks its manifest size. The reproduction creates ten thousand valid small files with long Unicode names. Creation succeeds with a **4,610,077-byte manifest**, then the same code rejects restoration with `invalid backup manifest`. This is a newly exposed gap in the manifest-based backup fix.

The entry-count and aggregate restore-byte limits also have no equivalent preflight in creation. Apply shared bounds before publishing an archive and verify that every successfully created archive satisfies the reader's contract. The probe is marked ignored by default because it creates many files; it was explicitly executed during this audit and cleaned up its private temporary directory.

### B11 Names can forge extra console lines

**Sources:** `mod/MoonDedicated/DedicatedServer.cs`, `OnAuthRequest`; `src/supervisor.rs`, JOIN/LEAVE output; `amp/moonsweepers.kvp`, readiness regex.

The server limits names to forty characters but does not remove line breaks. The manager interpolates names into its console protocol. A short name containing a newline followed by `[moon-ds] READY server_steam_id=1` creates an apparent standalone READY line that matches the AMP readiness pattern. The diagnostic child supplies that name in the same state schema used by the real mod, and the actual Supervisor emits it unsanitized.

Escape control characters in all untrusted log fields and keep machine events structurally separate from display names. Validate at admission and at the console boundary. The reproduction sends no messages to other users and uses no external Steam endpoint.

### B12 Positional CLI parsing mistakes option values for filenames

**Source:** `src/main.rs`, `restore` and `import_game`.

Both select the first argument not starting with `--`, which can be the value of an earlier option. In `restore --config settings.properties backup.zip`, the config path is chosen as the archive, then removed from the config arguments; the ZIP becomes the config value. The executable reproduction reports a UTF-8 config-read error for the ZIP instead of opening the supplied settings file. It errors before resolving or touching a real save directory.

Use one positional/options parser that consumes option values and validates extra/missing positional arguments. Test options before and after the filename, equals-form options and paths containing spaces. Import uses the same faulty selection pattern; only restore was exercised by the CLI probe.

### B13 Xvfb collisions and leaked helpers

**Source:** `src/supervisor.rs`, `spawn`, `start_xvfb` and the spawn-error branch in `run`.

With DISPLAY unset, the display number is `100 + query_port % 800`; ports 27026 and 27826 both select display 726. Spawn success only means Xvfb started as a process, not that it owns a ready display. A second Xvfb may exit while Wine uses the first instance's display. Stopping the first helper can then break the other server. If Wine spawning fails after a helper starts, the early error return skips helper termination entirely.

These are source findings; no Linux host was available for runtime reproduction. Use race-safe display allocation/readiness checks and RAII cleanup for every child/helper exit path. The current formula is not a unique allocation mechanism.

## Evidence and checks

The isolated diagnostic crate is `checks/audit-2-2026-11-14`. Seven default probes assert the still-buggy behavior, with eight existing imported unit tests also executed. The manifest-limit probe and CLI probe bring the total to **nine executable defect reproductions**. Passing these diagnostic probes means the defect remains reproducible; they are intentionally separate from desired-behavior CI regressions.

```powershell
cargo build --offline --manifest-path checks/audit-2-2026-11-14/Cargo.toml --bin audit-game
cargo test --offline --manifest-path checks/audit-2-2026-11-14/Cargo.toml --lib -- --test-threads=1
cargo test --offline --manifest-path checks/audit-2-2026-11-14/Cargo.toml --lib backup_can_create_a_manifest -- --ignored --nocapture
python checks/audit-2-2026-11-14/cli_probe.py
```

The existing 20-test Rust audit regression suite and 11-check C# harness pass. Strict Clippy and normal builds pass. Real-game client hook validation and private-copy startup pass with one additional menu row, Steam ready, no world entered and unchanged live world-file hashes. The main menu and separate browser were also inspected through the computer-use skill. An actual remote server join was not performed, and vehicle persistence, full host simulation, Linux/Wine and AMP acceptance remain unverified.

Final 0.1.1 verification also passed all seven manager/child process regressions. The installed Steam client reported plugin version 0.1.1.0, Steam ready, one added row and no menu error. Temporarily renaming only the companion executable produced the native error dialog; dismissal worked, the executable was restored, and the button then opened the installed browser successfully. The live game was left at its menu with the browser open.

The next engineering priorities are the shared-world lock, backup request correlation, console-field escaping, and matching backup writer/reader limits. These open findings should be addressed before treating the project as production-ready.
