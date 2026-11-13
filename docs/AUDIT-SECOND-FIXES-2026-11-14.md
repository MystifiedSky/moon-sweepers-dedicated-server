# Second audit fixes — 0.1.2

All eleven open findings from `AUDIT-SECOND-2026-11-14.md` have code fixes. The table distinguishes executed regression coverage from checks that still require a Linux runtime. This does not certify every gameplay mechanic or AMP deployment.

| Finding | Change | Verification |
|---|---|---|
| B02 Shared Windows world storage | Server-only persistent-data-path hook redirects worlds to `<MOON_DATA_DIR>/world`. Serve/restore/offline backup lock the actual world root, outside replaceable slot folders. Client storage is separately reserved for its process lifetime. | Different data roots and shared-lock exclusion/release tested. Real Windows server wrote its own slot while the client joined concurrently. |
| B03 Stale snapshot failure | Manager commands and mod state carry a unique snapshot request ID. Only that request's success/error can finish the pending backup. New requests clear previous snapshot metadata. | Actual supervisor regression starts with a previous error, completes a new snapshot and produces a backup. Live native backup succeeded. |
| B05 Stale imports | Equal metadata no longer implies equal contents: import compares bytes with bounded buffers. | Equal-size/timestamp files with different bytes are replaced. |
| B06 Argument quoting | Quote-aware tokenizer preserves grouped paths, empty arguments and backslashes; incomplete quoting reports an error. | Quoted values, Windows paths, trailing slash and malformed quoting checked. |
| B07 Bookmark loss | Corrupt/unreadable saved lists surface an error and block overwriting the original. Writes use a temporary file, flush and rename under a file lease. Both browsers report failures. | Corrupt input remains byte-for-byte intact; save refuses. Native add/save and reopen exercised. |
| B08 Truncated rules | Every rule value requires its NUL terminator. Malformed/unavailable rules produce a query error instead of an empty requirements list. | Rust truncated-packet regression and C# every-truncation parser check. |
| B09 Refresh workers | Standalone browser coalesces duplicate pending addresses, bounds the queue and runs at most four queries. Results for deleted entries are discarded. DNS uses four bounded workers and callers have a total query deadline. Embedded browser permits one query at a time and bounds unresolved DNS work. | Scheduler deduplication/capacity test; query integration tests. No user-client exhaustion stress test. |
| B10 Unrestorable backups | Creation enforces the same slot, entry-count, manifest-size and aggregate-byte limits as restore before publishing the archive. Failed creation removes its partial output. | The 10,000-file/4.6 MB manifest reproduction now refuses creation and leaves no archive. Existing restore/retention regressions pass. |
| B11 Forged console events | Admission removes control characters from player names; the manager escapes control and Unicode line separators at its output boundary. | Actual supervisor receives a multiline name and emits one escaped event. Admission sanitizer checked separately. |
| B12 Positional CLI parsing | Shared positional/options parser consumes option values and accepts options before/after the filename, equals form and `--`; extra/missing positionals fail. | Parser cases plus actual restore CLI into a private configured world directory. |
| B13 Xvfb collisions/cleanup | Xvfb chooses a free display with `-displayfd`; manager waits for the ready display with a deadline. A scoped helper owner kills/reaps it on spawn failure and every other exit path. | Windows and Linux target compile checks. Actual Xvfb/Wine execution still requires a Linux host. |

## Embedded browser and simple hosting

`JOIN SERVER` now opens a browser **inside Unity**, with typed address/label fields, saved servers, edit/delete, favourites, pagination, password entry, version/capacity/mod checks, query details, Join and Cancel. The menu does not launch another executable. It uses the existing transport and the same bookmark format as the optional standalone browser. Mouse and typed input were exercised on the user's client. Controller navigation is not implemented.

`moon-server.exe` is the Windows host entry. Its first run detects a prepared installed game or requests its path, creates a separate copy under `server/game`, and writes `server/server.properties`. Subsequent runs start that instance. Its world and backups are under `server/data`. The manager and `mod/` payload must remain together in the extracted kit. `moon-ds serve` remains the advanced/AMP entry.

Live testing exposed and fixed two additional blockers: the dedicated crew session was not binding to the loaded world, and a non-advertised Steam server did not answer address queries in this environment. The server now explicitly creates/binds the native crew session when needed. Private-server queries are served by the manager with challenge cookies, a rate bound, bounded fragmentation, and fresh mod state, independently of Steam master-list advertising. Public-listing mode retains Steam's configured query endpoint.

## Executed acceptance evidence

The local host logged in anonymously to Steam, reached `world-bound`/`ready=true`, and wrote to `server/data/world/MoonSessions/slot-1`. The user's client queried `127.0.0.1:27026`, authenticated its Steam ticket/license, downloaded the native world, and reported `Joined Local Moon Sweepers Server` with `connected=true`, `in_game=true`, `loading=false`. The host showed the authenticated player. A native live snapshot backup and a graceful final save/stop backup completed. A subsequent startup reused the server world.

Regression coverage includes the original 20-test Rust audit harness, the second audit harness (19 default checks plus its explicitly executed heavy manifest check), 13 C# protocol/native-boundary checks, seven manager/child process scenarios, and Windows client/server native hook validation. Strict Clippy/build/format checks and Linux target compilation are included in final verification. Raw receipts are under `checks/audit-2-2026-11-14` and `checks/audit-2026-11-14`.

Remaining acceptance scope: a second simultaneous player, vehicle hand-back/persistence, long-running gameplay/desync, Linux/Wine/Xvfb runtime behavior, and an actual AMP instance. The older audit reports are historical; their open-status tables describe the state before this release.
