# Synchronization fixes and verification â€” 0.1.7

The five findings in AUDIT-VEHICLES-SYNC-2026-11-14.md now have implementation changes. The original audit remains a record of the pre-change build.

- V01: Stop rejects new admissions, requests connected clients to finish and leave, runs native DropMissing for vehicle handover, waits for closing sessions, pauses autonomous simulation and world Update, then requests a fresh final save. Native gameplay receives are blocked beyond capture. A client-drain timeout is retained as an unsuccessful shutdown outcome even if the disk save completes. Pending authentication cannot admit a new player during stop.
- V02: Goodbye has a server acknowledgement after roster removal and native departure processing. Normal client teardown waits for that acknowledgement. Server transport/authentication cleanup is delayed so acknowledgement delivery can finish. Shutdown also waits for this closing-session period.
- V03: Timeout/failure remains latched through menu/quit and is written as an unconfirmed result. A new connection resets handshake state.
- V04: Stroke-completion failure latches an explicit repair failure instead of claiming a deferred retry exists. Client synchronization/leave and server readiness reflect this failure; reconnect is required for the client, restart for a failed server. This chooses explicit failure rather than retaining native pointers and replaying a possibly stale page.
- V05: Queue method/field failures and missing results no longer become empty queues. Completion, flush and dust-request invocation success is checked. Legitimately uninitialized/null collections still count as empty when the required field exists.

Both ends should use 0.1.7. The control protocol adds PrepareStop and GoodbyeAck messages; older clients cannot participate in the new graceful-stop handshake and will time out as unconfirmed.

## Verification

- Mod and compatibility loader release builds: zero compiler warnings/errors.
- C# protocol/state regressions: 20 passed, including shutdown waiting, wire parsing of new control messages, acknowledgement recording, and failed native queue inspection.
- Rust: 11 unit tests passed for each binary; strict Clippy and formatting passed.
- Supervisor process regressions: 8 passed, including failed/crashed/forced shutdown and immutable backups.
- Release packaging: 2 tests passed; Windows server/client ZIPs rebuilt.
- Live 1.0.5 client/server on Windows: authentication and world download passed. Normal leave now appears as Disconnected on the server, rather than depending on an auth-cancellation callback.
- Connected-client server stop: repeated successfully after fixing an acknowledgement-delivery race caught by the first live attempt. The final client reported drained/successful leave, and server state reported stopped with save_failed=false. An earlier attempt correctly retained an unconfirmed client outcome; it is not counted as a passing shutdown.
- Native repair matrix: 20 variable width/depth, loop and page-edge cases passed. The first rerun refused occupied test pages; the fixture now searches unused pages from the start and still refuses overwriting existing pages. That refusal is diagnostic evidence, not a production repair failure.
- Cold server restart: all 290 captured data/raw arrays matched the pre-stop capture by SHA-256, with no missing or differing arrays.
- Installed client, installed server and built mod matched SHA-256 390BDC424902A732473014F4EE021D406664EBDA49FAB777AEEC07F60B1EBE60.

## Vehicle/Joonas investigation

Added opt-in, read-only entity captures to NativeSyncDiagnostics. They record owned vehicle owner/holder, garage/type, in-world state, position and upgrade bytes, plus active robot position, status, dust, hex and progress. These run only with native-tests.enabled and an explicit entities command.

The actual server world and joined client both reported zero owned vehicles and zero active robots. No populated vehicle/Joonas save was available during this run. Consequently ownership/upgrade persistence, driving/passenger behavior, moving Joonas, near/far observer transitions and two-player synchronization remain unverified. Native paths are documented in the audit; no speculative robot/vehicle algorithm changes were made. A copy of a populated save is needed for those gameplay acceptance tests.

Temporary native diagnostic switches were removed after testing. Existing captures are private local evidence, outside release assets. No commit or GitHub release was published.
