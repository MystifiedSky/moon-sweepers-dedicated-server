# Moon Sweepers audit fixes and validation

All 18 findings from the November 14 audit have implementation changes. Windows regression tests and real client startup pass. Dedicated world simulation with two licensed accounts, vehicle persistence, Linux/Wine, and AMP still require acceptance testing; passing the manager's fake-game tests does not establish those behaviors.

## Fixes

| Finding | Change | Verification |
|---|---|---|
| A01 | Resolve game, data, backup, runtime, log and Wine-prefix paths before spawning | Absolute-path regression; process fixtures use a data path containing spaces |
| A02 | Reject an unreadable explicitly selected config, unknown settings, and missing option values | Missing config and misspelled password-setting regressions; recognized AMP installation metadata remains supported |
| A03 | Validate archive inventory and CRCs in a staging directory before swapping the save; preserve unique recovery copies and roll back a failed swap | Corrupt restore preserves current and prior recovery data; complete nested-file restore; empty-archive refusal and legacy restore |
| A04 | Retain only recognized manifest-backed archives for the current slot; order by creation metadata | Unrelated ZIP and other-slot preservation; ten rapid backups each retain the newly created archive |
| A05 | Hold an OS-backed runtime lock across startup, supervision and restarts; restore and offline backup use the same lock; reject stale process generations | Exclusive-lock regression and competing manager process with no heartbeat |
| A06 | Invalidate roster publication state and cached roster on disconnect/new join and server Steam loss | Actual client Disconnect regression; client startup with native hooks |
| A07 | Reserve capacity for pending peers, check again at ticket validation, and enforce an independent 30-second authentication deadline | Pending-capacity and late-validation regressions against the actual server class |
| A08 | Send all external administrative commands through the supervisor | External stop with auto-restart enabled saves, backs up and exits without restarting |
| A09 | Route both live-backup CLI forms through the manager; publish an immutable snapshot after the save task; never silently skip unreadable files | Both CLI forms produce valid archives; snapshot remains unchanged after live-file edits |
| A10 | Distinguish clean final-save acknowledgment from failed, crashed and forced shutdown | Three process tests return exit code 3 and do not create misleading stop backups |
| A11 | Use a hook transaction, validate method/delegate argument counts, roll back required-hook failures, and use BepInEx's native IL2CPP detour provider | Failure rollback regression; real client/server hook validation; full client menu startup with active callbacks |
| A12 | Check native invocation success before marking a join started; report success only after world entry; bound the world-download wait | Explicit invocation-failure regression; native world-download acceptance remains outstanding |
| A13 | Reject overlapping source/destination trees and directory links; skip transient BepInEx state; permit Vortex file links to copy as files | Nested import is refused before destination creation |
| A14 | Check Unity's actual running version before installing hooks | Real game 1.0.5 accepted during both hook validation and full client startup; no claim of compatibility with another build |
| A15 | Retain a timed-out writer task and block further saves/quit until it finishes; expose timeout as failure | Timeout-state regression confirms the original task remains tracked |
| A16 | Group query fragments by response ID, require consistent headers and complete indexes, and bound memory | Mixed response IDs rejected; out-of-order fragments from one reply accepted |
| A17 | Validate aggregate payload/count/string limits on send; require exact field lengths, valid UTF-8 and complete payload consumption on receive; bound rejection details | Oversized inventory fails locally, all auth truncations rejected, trailing data rejected, large rejection stays within limits |
| A18 | Honor the advertised disabled mod policy in browser requirements | Missing advertised mods do not block a policy=off server |

New backups contain `moon-ds-backup.json`. Retention intentionally preserves older unmanifested archives and unknown ZIPs. Earlier kit backups remain restorable if their names identify a slot and they contain the known root save marker. Restore rejects unrecognized archives rather than replacing a world with arbitrary ZIP contents.

`moon-ds backup` is asynchronous while a server is running: exit zero confirms queuing, and the manager's `BACKUP <file>` line confirms completion. Stopped-server backups execute synchronously under the runtime lock. Windows Unity save directories remain tied to the Windows user; keep the existing separate-user guidance for server hosting.

## Launch failure and correction

The first installed client build reached plugin loading but crashed in CoreCLR when normal game startup exercised the hooks. The initial validation mode had installed and rolled back the hooks before those callbacks ran, which was insufficient coverage.

The corrected build uses `BepInEx.Unity.IL2CPP.Hook.INativeDetour`, including its native trampoline and delegate lifetime handling, instead of MonoMod's legacy raw `NativeDetour` trampoline. The upstream implementations document the difference: [BepInEx native detours](https://github.com/BepInEx/BepInEx/blob/dbc82f05b8ea34833de5b6a723eb60dd8398e0b1/Runtimes/Unity/BepInEx.Unity.IL2CPP/Hook/INativeDetour.cs) and [legacy native trampolines](https://github.com/MonoMod/MonoMod/blob/v22.07.31.01/MonoMod.RuntimeDetour/NativeDetour.cs).

The corrected client reached the actual game's menu, initialized Steam and ran more than 100 update callbacks in the private game copy. Before/after SHA-256 comparisons confirmed the existing world-save files were unchanged. The updated DLL was then installed into the user's Steam copy and launched through Steam. Its live status confirmed menu=true, Steam ready=true and thousands of update callbacks; Windows reported the game responding.

The installed plugin is `F:\SteamLibrary\steamapps\common\Moon Sweepers\BepInEx\plugins\MoonDedicated\MoonDedicated.dll`. The installation receipt and checksum are in `checks/audit-2026-11-14/client-install.json`. The older failing DLL is retained with `.disabled` appended and is not loaded as a plugin.

## Validation

| Check | Result |
|---|---|
| Rust formatting and strict Clippy | Passed |
| Original Rust unit suite | 8 passed |
| Rust audit regression harness | 20 passed, including the 8 imported unit tests |
| C# mod regression harness | 11 passed |
| Manager process regressions | 7 passed |
| Built-in manager and browser self-test | 6 steps passed |
| Release plugin build | Passed with zero warnings and errors |
| Real-game client and server hook validation | Both passed; hooks rolled back before world startup |
| Full client startup in private game copy | Passed with active callbacks and Steam ready; no world started |
| Installed Steam client | Reached menu with mod loaded and process responding |

The C# harness compiles the actual mod classes against .NET 10 for isolated logic tests, with test doubles for logging and BepInEx's plugin inventory. It does not simulate Unity or Steam authentication. The real-game tests separately exercise the installed .NET 6 loader and native hooks. The release dependency versions are pinned and NuGet lock files are included.

Evidence is under `checks/audit-2026-11-14/`: `runtime-regressions.json`, `mod-regressions.json`, `self-test-fixed.json`, `smoke-result.json`, `client-startup-result.json`, `client-live-status.json`, and `client-install.json`. The private copy of the game is ignored by Git and excluded from release packaging. The Windows release ZIP and its SHA-256 file are in `dist/`.

## Repeating the checks

Run from the repository root:

```powershell
cargo fmt --check
cargo clippy --locked --all-targets -- -D warnings
cargo test --locked
cargo test --locked --manifest-path checks/audit-2026-11-14/Cargo.toml
cargo build --locked
python checks/audit-2026-11-14/runtime_regressions.py
dotnet restore mod/MoonDedicated --locked-mode
dotnet build mod/MoonDedicated -c Release --no-restore
dotnet run --project checks/audit-2026-11-14/protocol/ProtocolAudit.csproj
.\target\release\moon-ds.exe --self-test checks/audit-2026-11-14/self-test-fixed.json
```

`game_smoke.py` validates hooks in a private copy of the locally installed game and exits before starting a world. `client_startup.py` requires that copy, exercises the actual menu with active client hooks and then terminates only the private test process. Those local scripts use this machine's Steam installation path and are not generic CI tests. The CI workflow runs the platform-independent manager and C# regressions on Windows and Linux; it has not been executed remotely as part of this session.

## Remaining acceptance tests

Use a disposable server world and two licensed Steam accounts for actual authentication, join/download, leave/rejoin, capacity contention, invalid password/mod rejection, Steam loss/recovery, vehicles and Joonas behavior. Confirm saved content survives restoration and real gameplay. Linux/Wine/Xvfb and AMP also need their own runtime environments. A local Windows client startup does not establish those results.
