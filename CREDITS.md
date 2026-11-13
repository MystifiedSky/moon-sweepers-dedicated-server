# Credits

## Original game files
No game binaries, generated game assemblies, art, sound, models, fonts, maps or saves are included in the public release packages. You must supply your own licensed game installation.

Development and runtime testing use private, Git-ignored copies of the installed game, loader and disposable saves. Game metadata and native behavior were inspected with Ghidra and local analysis tools to identify method signatures, ABI layouts and simulation behavior. Private diagnostic captures and decompilation output are excluded from releases. The distributed adapter is original source; it does not include decompiled game implementations.

At run time the tools read or copy your own installed copy (`moon-ds import`, `moon-ds serve`); that private copy must not be shared.

## Other work
* BepInEx (LGPL-2.1) and Il2CppInterop, MonoMod — referenced at build time by the mod; not redistributed here.
* Mono.Cecil — referenced by the loader compatibility patcher to repair malformed generated metadata; supplied by the user's BepInEx installation. The compatibility patcher uses Il2CppInterop's documented source behavior for its exported metadata initialization entry; it does not bundle a modified loader or generated game assemblies.
* Rust crates: macroquad, serde, serde_json, zip, ctrlc, windows-sys (MIT/Apache-2.0).
* AMP template format: CubeCoders/AMPTemplates (reference only).
* Steamworks SDK public API names and the Valve query protocol (A2S) are used as documented by Valve.
