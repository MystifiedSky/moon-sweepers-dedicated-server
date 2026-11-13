# Cleaning and persistence audit

This follow-up investigates the live report that a narrow brushed number changes into broad cleared patches or gains a straight cut after leaving and rejoining. It supplements the earlier repository audits. A clean loader log and a straight-line snapshot fixture did not establish faithful multiplayer cleaning.

## Findings and changes

| Priority | Finding | Change |
|---|---|---|
| P1 | The headless `RestoreRenderingBindings` hook returned false. Native `MoonWorld.Update` treats that as not ready and returns before normal simulation, including processing queued tool strokes. Saving later forces those delayed strokes to run against newer repaired pages. | Report headless rendering readiness as true while continuing to suppress the actual GPU operations. Keep the native world update running. |
| P1 | Native page repair uses a larger polishing radius than the originating hand-brush stroke. Captured live calls used approximately 0.0301 for the brush and 0.35 during repair, which removed hundreds of extra samples inside and between marks. | Suppress additional polishing only inside dedicated page repair. Finish older queued strokes before merging cumulative raw page state. Normal tool polishing remains native. |
| P1 | Captures before leaving and after saving exposed an extra straight cut in raw samples, not only a rendering difference. Distant crew and queued-stroke joining can collapse received segments. | Preserve individual dedicated crew segments through sender flush, distant-run joining and queued-stroke joining. Preserve all original width, depth, dose, right-vector and edge-width parameters. |
| P1 | Client leave canceled authentication and closed the Steam session immediately after sending, without waiting for pending native strokes or reliable acknowledgments. | Defer menu/quit, stop new local tool input, finish pending strokes, request dust synchronization and wait for native queues and queued/unacknowledged reliable bytes to drain. An unconfirmed 15-second timeout is reported rather than called a successful sync. |
| P1 | A stop or backup request arriving during an existing asynchronous save could accept the older snapshot. | Queue a fresh save and wait for it before publishing the snapshot or quitting. |
| P1 | Headless mask `Upload` also publishes CPU bank snapshots and copy-on-write flags. Suppressing it skipped state maintenance. | Keep both CPU upload entry points; suppress only `UploadVisualSnapshot` and actual texture GPU operations. |
| P1 | Unity 6 GC handles are pointer-sized; using 32-bit handles truncated native object references. | Use pointer-sized handles throughout object pinning, lookups and release. |
| P1 | Failed dedicated initialization could roll hooks back and fall through into an ordinary client process. | Abort the dedicated process before gameplay if required initialization fails. |
| P2 | The supervisor and native plugin both scheduled autosaves for managed instances. | Let the supervisor schedule managed autosaves; retain the plugin fallback for standalone instances. |
| P2 | A log line exceeding the read budget could permanently stall log tailing and hide later events. | Consume oversized lines in bounded portions and continue reading following events. |

## Verification criteria

### Completed local results for 0.1.6

* A real hand-brushed curved mark crossed pages 255 and 319 of a loaded chunk. All 49 loaded pages matched the client's pre-leave SHA-256 values for both raw and processed arrays during play, after leave/save, after a full server shutdown/restart, and after the client rejoined. There were no missing pages or differing samples. This passed only after correcting headless world-update readiness; the earlier intermediate 0.1.6 build failed and was replaced.
* The final running build passed the 20-case native repair matrix, including partial removal, duplicate delivery and stale delivery. The existing native upload/snapshot/restore fixture also passed.
* Native save followed by snapshot while the writer was active completed two saves before publishing the requested snapshot. Native save followed by stop also completed two saves before exiting. A supervisor-requested live backup then created a new archive successfully.
* The final client and server BepInEx startup/runtime logs contained no warnings or errors. Both installed DLLs matched the built files by SHA-256. A deliberately invalid-port startup test correctly exited with failure before starting a world; its expected error messages are separate test evidence.
* Rust unit tests passed for both binaries, the earlier two audit regression suites passed (including the large-manifest case), eight manager process regressions passed, 16 C# protocol/state regressions passed, two loader-metadata checks passed, and two release-packaging tests passed. Formatting, strict Clippy, Linux cross-checking and Windows release packaging passed.
* Temporary client/server capture switches were removed after verification. The client is rejoined to the running local server. Public release artifacts were rebuilt as 0.1.6; no GitHub release was published from this local-only project.

The native repair matrix uses 20 combinations of widths (1, 2, 5, 12 and 32 samples) and partial-removal values (0, 64, 128 and 224), closed loops, untouched interiors, separated gaps, page-edge strokes, duplicate delivery and stale delivery. Every data/raw sample must match; checking only the number of clean samples is insufficient.

The native snapshot fixture checks CPU bank publication, copy-on-write protection and exact data/raw samples through two snapshot/restore cycles. It covers three separated narrow marks. It does not replace the closed-loop repair matrix or actual queued-tool simulation.

Live acceptance must compare a new user-drawn curved mark before leave, after server save, after client rejoin and after a full server restart. Raw geometry must not acquire new cuts. Record processed-mask differences separately. Earlier intermediate builds failed this comparison despite passing smaller fixtures; they are not accepted as proof of a fix.

Save overlap acceptance queues save followed by snapshot or stop during the active writer, requiring a second completed native save. The supervisor process tests also cover failure, forced termination, restart generations and truthful backup outcomes.

The local diagnostics are opt-in through `native-tests.enabled`; normal installations do not capture brush data or run test mutations. Private captures and game analysis files remain Git-ignored and outside the release allowlist.

## Scope limits

The repair fix does not substitute one fixed brush radius for all tools. All actual tool parameters remain native. Sample-level width/depth cases do not establish that every unlockable tool, vehicle, robot or upgrade has been operated through the UI. Two-account concurrency, every vehicle/Joonas interaction, AMP and a live Linux/Wine host require separate runtime acceptance.

A change cannot reconstruct the original design from an old processed mask that already contains additional removal. Preserve backups or test new marks on untouched ground when evaluating a new build.
