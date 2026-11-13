# Vehicle, Joonas and synchronization audit — 2026-11-14

Audit only. No implementation fixes, deployment, game restarts or save mutations were performed. Reviewed local mod 0.1.6, existing logs and tests, and the corresponding native game methods. The findings below distinguish defects visible in code from gameplay symptoms that still require reproduction.

## Prioritized work list

| ID | Priority | Finding | Evidence level |
|---|---|---|---|
| V01 | P1 | Final shutdown save has no boundary that stops player changes and completes departure/handover | Confirmed lifecycle gap; resulting vehicle loss not reproduced in this audit |
| V02 | P2 | Goodbye is sent after draining, then authentication/session are immediately closed | Confirmed ordering race; existing logs show departures handled through auth cancellation |
| V03 | P2 | Unconfirmed leave-sync timeout becomes a successful leave result | Confirmed control flow |
| V04 | P2 | Failed stroke completion discards the current repair call instead of deferring it | Confirmed failure-path defect; native failure not induced in the live game |
| V05 | P2 | Leave checks interpret failed native reads as empty queues | Confirmed failure-path defect; native failure not induced in the live game |
| I01 | Investigate first | Vehicle ownership, upgrades and parked/driven state across roster changes | Upstream reports; not established locally |
| I02 | Investigate first | Joonas movement, near/far simulation and capsule synchronization | Upstream report; not established locally |
| I03 | Investigate | Multiple tools/robots/players under the segment-preservation cleaning hooks | Missing gameplay and load coverage; not a demonstrated regression |

P1 means potential loss of accepted world changes during an ordinary administrative operation. P2 items are narrower races, misleading outcomes or error-recovery failures. Investigation items are not instructions to change native behavior without reproducing a fault.

## V01 — Establish a final state before the shutdown save

**Locations:** [DedicatedServer.cs:744](<C:/Users/MystifiedSky/Documents/GitHub Projects/moon-sweepers-dedicated-server/mod/MoonDedicated/DedicatedServer.cs:744>), [SaveTick:646](<C:/Users/MystifiedSky/Documents/GitHub Projects/moon-sweepers-dedicated-server/mod/MoonDedicated/DedicatedServer.cs:646>), [Shutdown:270](<C:/Users/MystifiedSky/Documents/GitHub Projects/moon-sweepers-dedicated-server/mod/MoonDedicated/DedicatedServer.cs:270>), [NetReceive/Member:139](<C:/Users/MystifiedSky/Documents/GitHub Projects/moon-sweepers-dedicated-server/mod/MoonDedicated/DedicatedServer.cs:139>).

`stop` sets `stopAfterSave` and requests a save. That flag rejects new authentication requests, but existing authenticated members remain valid and native gameplay messages can still be received. There is no client stop-input/drain handshake or final world-change barrier. Once the async save completes, `SaveTick` calls `QuitNow` without checking for changes accepted since capture. Queuing a second save when another writer was already active fixes an older-snapshot case, but does not close this later-change window.

There is also no normal departure pass before this final save: `Shutdown` clears peers and shuts down Steam after saving, instead of running `Drop`/roster removal and allowing native vehicle handover to finish. The delayed departure saves at 3 and 25 seconds do not delay `QuitNow`.

Native evidence: `DropMissing` calls `ForgetCrewmate`; that calls `MoonWorld.CrewmateLeft`, which calls `ReleaseCrewCarrier` and `ReleaseCrewVehicles`. `ReleaseCrewVehicles` clears matching holders and marks the vehicle collection changed. This establishes that departure has stateful vehicle work, not just UI cleanup. It does **not** establish that every saved vehicle will disappear, or that the save format necessarily persists a stale holder.

**Failure scenario:** stop while a player is driving, cleaning, parking or making a purchase; changes continue after the final snapshot begins, or handover occurs only after that snapshot. The manager can report successful shutdown for an earlier state. Robot state should be included in the same final-state test; the exact simulation steps that run during a native save need runtime confirmation.

**Repair requirement:** define a shutdown boundary, finish accepted gameplay and vehicle handover, capture the final state, and acknowledge shutdown only after that save completes. Avoid merely adding another fixed delay.

**Acceptance:** use a disposable world and slow the writer. Stop with a player driving, then repeat parked, immediately after purchase/upgrade, and with working Joonas. Compare stable vehicle IDs, ownership, upgrades, positions, storage and robot state against the defined final boundary after cold restart. Repeat `leave → stop` inside the 3-second departure-save window. Verify that no accepted action after the snapshot silently disappears.

## V02 — The disconnect message is outside the leave drain

**Locations:** [PlayerClient.cs:184](<C:/Users/MystifiedSky/Documents/GitHub Projects/moon-sweepers-dedicated-server/mod/MoonDedicated/PlayerClient.cs:184>), [DrainLeave:150](<C:/Users/MystifiedSky/Documents/GitHub Projects/moon-sweepers-dedicated-server/mod/MoonDedicated/PlayerClient.cs:150>).

`DrainLeave` waits for native dust queues and previously sent reliable bytes. Only afterward, `Disconnect` sends `Goodbye`, immediately cancels the Steam ticket, and closes the session. There is no acknowledgement or drain for this newly sent message. Its send outcome is not checked.

Existing server log lines 44–45 and 51–52 show departures processed as ticket response 6 rather than the `Goodbye` path's `Disconnected` reason. This supports the cancellation race; it does not prove that brush or vehicle packets were lost during those departures. The auth-callback fallback does eventually remove the peer in those examples.

**Impact:** graceful departure depends on an asynchronous authentication-failure fallback. Native handover and departure-save scheduling can happen later than the client believes, particularly around an immediate stop/restart or reconnect.

**Repair requirement:** include goodbye/server acceptance in the graceful-leave protocol before ticket cancellation and session close. Keep a bounded fallback for genuine connection loss.

**Acceptance:** introduce delay, leave normally, and verify one server-confirmed departure followed by completed vehicle handover. Test immediate reconnect and immediate server stop. Do not treat a Steam transport acknowledgement alone as proof that gameplay and handover were applied.

## V03 — Preserve an unconfirmed leave result

**Locations:** [PlayerClient.cs:172](<C:/Users/MystifiedSky/Documents/GitHub Projects/moon-sweepers-dedicated-server/mod/MoonDedicated/PlayerClient.cs:172>), [lobby leave hook:70](<C:/Users/MystifiedSky/Documents/GitHub Projects/moon-sweepers-dedicated-server/mod/MoonDedicated/PlayerClient.cs:70>), [Result:110](<C:/Users/MystifiedSky/Documents/GitHub Projects/moon-sweepers-dedicated-server/mod/MoonDedicated/PlayerClient.cs:110>).

After 15 seconds with synchronization still pending, `DrainLeave` only writes a warning. It then calls the normal menu/quit path. The lobby leave hook calls `Disconnect("You left the server.", true)`, which overwrites `LastNotice` and writes `last-join.json` with `ok=true`. The timeout outcome is not carried through teardown.

**Impact:** the user/browser receives a successful leave result even though the mod explicitly could not confirm synchronization. This makes a subsequent rollback harder to diagnose and contradicts the earlier audit's claim that an unconfirmed timeout is not called successful.

**Repair requirement:** preserve a distinct unconfirmed/failed synchronization outcome through disconnect and show it to the user. Keep ordinary successful departure separate.

**Acceptance:** keep a native queue or reliable acknowledgement pending beyond 15 seconds. Verify the visible notice and result file remain unconfirmed after reaching the menu; verify ordinary successful leave still reports success.

## V04 — A repair logged as deferred has no deferred work

**Location:** [CleaningSync.cs:50](<C:/Users/MystifiedSky/Documents/GitHub Projects/moon-sweepers-dedicated-server/mod/MoonDedicated/CleaningSync.cs:50>).

When `CompletePendingToolStrokes` fails, the `CleanDownTo` hook logs that dust repair was deferred and returns zero. It neither calls `originalRepair` nor retains the chunk/key/payload, requests a retry, or propagates a distinct failure. Zero is also a normal changed-sample count, so it cannot communicate a deferred repair by itself.

Native evidence: `MoonCrewSession.HearDustPage` invokes `MoonWorld.ApplyCrewDustPage` through a void interface. The new hook adds no retry protocol to that path. Later native truth reconciliation may request the page again; that is possible recovery, not a queued retry implemented by this branch.

**Impact:** under a native completion exception, the received repair is skipped while the rest of the synchronization flow can continue. The log claims a recovery action that has not been arranged. This is a failure-path finding, not evidence that current ordinary brush calls are throwing.

**Repair requirement:** retain/re-request failed repairs with safe native object/data lifetimes, or expose an explicit failed synchronization state. Preserve the required stroke-before-page ordering.

**Acceptance:** inject one completion failure, then restore normal execution. The exact raw/processed page must eventually match without extra brushing or reconnecting, and leave must not report synchronized while that repair remains outstanding. Test repeated failures without unbounded retries or memory growth.

## V05 — Failed queue checks can look like successful synchronization

**Locations:** [PlayerClient.cs:134](<C:/Users/MystifiedSky/Documents/GitHub Projects/moon-sweepers-dedicated-server/mod/MoonDedicated/PlayerClient.cs:134>), [NativeCount/DrainLeave:144](<C:/Users/MystifiedSky/Documents/GitHub Projects/moon-sweepers-dedicated-server/mod/MoonDedicated/PlayerClient.cs:144>), [Il2.Invoke:194](<C:/Users/MystifiedSky/Documents/GitHub Projects/moon-sweepers-dedicated-server/mod/MoonDedicated/Il2.cs:194>), [Il2.Int:234](<C:/Users/MystifiedSky/Documents/GitHub Projects/moon-sweepers-dedicated-server/mod/MoonDedicated/Il2.cs:234>).

The leave path invokes stroke completion, flush and dust requests without checking success. Queue inspection also uses `Il2.Invoke`; a missing/throwing managed method returns a null result, and `Il2.Int(null)` becomes zero. Missing field reads similarly become null/default, and `NativeCount` interprets null as empty. Consequently, a failed inspection can satisfy the quiet-queue test when transport bytes are drained.

**Impact:** native invocation failures or an incompatible game field/method can produce a false synchronized result. The current build's fields exist; this audit did not force a live IL2CPP failure. Existing tests verify that `TryInvoke` exposes failure, but this leave path does not use that success result.

**Repair requirement:** distinguish a verified empty queue from an unavailable/failed read, validate required members, and preserve completion/flush failures through the leave outcome. Do not globally change legitimate null collection semantics without checking native initialization.

**Acceptance:** separately fail completion, `get_Count`, and `get_PendingToolStrokes` in a controlled harness. None may result in a successful drain. Verify initialized empty queues still complete normally.

## Investigation queue — reproduce before implementing fixes

### I01 — Vehicle ownership and upgrades across joins/leaves

Previously reviewed Upstream issue 4 reports disappearing vehicles after departure and upgrade failures associated with roster changes. Previously reviewed Issue 2 also reports a mega vacuum rover ownership/garage-upgrade problem. These were reported against game 1.0.5 and were open when checked. They are evidence for test selection, not proof of the same defect in this implementation.

Inspect native `TickOwnedVehicles`, `WriteCrewVehicleReport`, `HandleOwnedVehicles`, `SnapshotOwnedVehicles`, `RestoreOwnedVehicles`, and `ReleaseCrewVehicles`, alongside local roster publication and departure saves. The local code does publish roster changes and schedule post-departure saves; do not report those mechanisms as entirely absent.

Test two players with different owned vehicles and upgrade sets. Keep one connected while the other leaves/rejoins, then reverse them; include passenger/driver changes, parking, storing, recall, last-player departure and cold restart. Record vehicle identity, owner versus current holder, upgrade bytes, position/rotation, cargo and odometer. Confirm garage interactions and another player's view, not just object existence. Capture before departure, after native handover, on disk and after reload to locate the first divergence.

### I02 — Joonas visible motion versus productive simulation

Previously reviewed Upstream issue 2 describes stationary Joonas with dust reaching the catcher, missing visible capsules and stalled displayed hex progress. No equivalent local runtime reproduction was performed in this audit.

The native robot path is separate from astronaut pose sending: `UpdateWorld → TickRobotPoses → SendRobotPoses`, plus `TickFarRobotPoses`. Disabling server `SendPose` therefore does **not**, by itself, establish that all robot poses are disabled. `SendRobotPoses` selects nearby remote observers; `MoonRobotVacuum.Update` has simulation/following branches and stroke-backlog pacing. The headless hooks leave those methods intact, but suppress local walker updates. The interaction with loaded/far simulation and observer state needs testing.

Test one and several Joonas near spawn and far away, with one observer, two observers in different areas, no players, a new observer entering range, and a restart. Record server and client position/rotation over time, activity, hex progress, dust collected, launch/catcher events and raw cleaned pages. Distinguish a stationary client model from a stationary authoritative robot, and distinguish visual capsules from resource accounting. Do not accept increasing dust totals alone as proof of correct motion or cleaning.

### I03 — Segment preservation under realistic tool and robot load

**Locations:** [CleaningSync.cs:28](<C:/Users/MystifiedSky/Documents/GitHub Projects/moon-sweepers-dedicated-server/mod/MoonDedicated/CleaningSync.cs:28>), [repair ordering:40](<C:/Users/MystifiedSky/Documents/GitHub Projects/moon-sweepers-dedicated-server/mod/MoonDedicated/CleaningSync.cs:40>).

Dedicated mode disables both joining paths and flushes each outgoing stroke. Each received repair also invokes pending-stroke completion. This changes batching and scheduling across tools, not only the hand brush. The existing sample-width matrix and one real curved-mark test do not establish performance or ordering with vehicle brushes, multiple Joonas, upgraded tools or two simultaneous players.

Measure pending stroke count/age, robot hold state, repair duration, frame time and network backlog under those combinations. Compare exact raw and processed arrays after live synchronization, leave/rejoin, save and restart. Include overlapping and separate marks, curves, partial removal, page/chunk boundaries and delayed/duplicate/stale delivery. Report a regression only if a measurable failure occurs; do not re-enable lossy segment joining simply to reduce the queue.

## Evidence and verification limits

- Ran the existing C# protocol/state suite against current source: **16 passed**. It covers protocol validation, admission, reconnect reset, save-task handling, native handle width, hook rollback and snapshot immutability. It does not operate vehicles or Joonas.
- Read current server logs. Auth-cancellation departures are recorded; absence of error-level logs does not prove world-state fidelity.
- Inspected native function bodies using bounded PE extractions from the local GameAssembly, address-matched to the existing class dump. GameAssembly SHA-256: `14f711a6a3b1b7cd803a03f85cc6c6201c5b546ab9b90329533f5cb8ae2e7f13`. Relevant entries include `DropMissing` `0x180798FB0`, `ForgetCrewmate` `0x180799E00`, `CrewmateLeft` `0x1806CAF60`, `ReleaseCrewVehicles` `0x180725A70`, `TickOwnedVehicles` `0x1807B16D0`, `UpdateWorld` `0x1807B46E0`, `TickRobotPoses` `0x1807B1AE0`, `SendRobotPoses` `0x1807AD7B0`, `MoonRobotVacuum.Update` `0x180537CA0`, and `HearDustPage` `0x1807A1890`.
- Decompiler output is approximate, especially external/generic calls and shared function bodies. Used named native calls and inspected instructions where needed; unknown branches are not treated as confirmed gameplay defects. Private extraction/provenance files remain under the locally excluded `checks/local-tools` directory and are not release assets.
- The previous exact dust-page comparison remains evidence for that particular cleaning scenario. It was not rerun here and does not cover the investigation queue above.
- No new live gameplay acceptance, two-account test, Linux/Wine or AMP test was performed. This report is not a claim that every remaining game bug has been found.

For implementation handoff: address V01 first, then V02–V05, with focused regression checks. Reproduce I01–I03 before changing native ownership, robot authority, culling or cleaning algorithms. Keep the already-passing exact dust-shape checks as regression requirements.
