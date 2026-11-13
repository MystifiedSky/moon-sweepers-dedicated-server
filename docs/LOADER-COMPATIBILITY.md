# Loader compatibility

The kit includes `mod/MoonLoaderCompatibility.dll`, installed under `BepInEx/patchers/MoonDedicated/`. It supplements BepInEx 6 IL2CPP and the Moon Sweepers Nexus patcher; keep both prerequisites installed. The tested target is Moon Sweepers 1.0.5 on Unity 6000.6, with BepInEx be.793 and Nexus patcher 1.0.2.

The patcher addresses two observed loader problems before the normal plugins start:

* Il2CppInterop's internal `Class::Init` pattern does not match this Unity build. The compatibility patcher supplies the exported `il2cpp_class_has_references` entry, which initializes class metadata before examining references. It validates the export and a native System.Object class first. This avoids relying on a guessed internal address.
* Generated `UnityEngine.PhysicsCore2DModule.dll` and `UnityEngine.TimelineFoundationModule.dll` contain orphan nested TypeDefs with no enclosing NestedClass row. Duplicate orphan names prevent .NET 6 from loading those assemblies. The patcher reserializes their reachable Cecil ownership graph, refuses to discard an orphan referenced by reachable types or executable bodies, verifies the output, and preserves the original as `.before-moon-repair-<hash>` before replacement. Subsequent starts leave already valid files unchanged. Regenerated files are checked again.

These changes repair initialization and metadata. They do not reduce log levels, filter messages or disable assembly preloading. Unexpected failures remain visible in `BepInEx/LogOutput.log`. Compatibility on another Unity branch is deliberately left to the loader and reported as a warning.

To roll back, close all game/server processes, remove this kit's loader compatibility DLL, and restore the backed-up generated interop DLLs, or regenerate interop with the Nexus patcher's `ForceRegenerate` setting. Keep world saves and their backups untouched. A loader regeneration is separate from a world migration.

Automated checks exercise malformed nested metadata, preservation of valid nesting and originals, repeat-run stability, and refusal of referenced orphan removal. Native startup and interactive acceptance results are recorded separately in the validation report.
