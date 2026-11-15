# Publishing and release downloads

## First push

Create a GitHub repository, add it as this project's remote, and push your reviewed source to a branch named **main**. No upload has been performed by this local preparation work. Ensure `.github/workflows/`, `src/`, `mod/` source and lockfiles, `scripts/`, `checks/` test source, `amp/`, `docs/`, Cargo files, README, licence and credits are included.

The local `.git/info/exclude` rules keep machine-specific session files out of ordinary staging. They are not committed or shared. Installed games, save data, build output and logs must not be committed. Do not force-add ignored local files. Only README and LICENSE were present in this repository's original commit; excluded session files were never in its committed history.

GitHub Actions must be enabled for the repository. The workflow gives only its final publish job `contents: write`, using the repository's built-in token; no personal token is required for normal releases. Organization policy or repository rules may restrict Actions, tag creation or releases; if so, the repository administrator must permit this workflow. Check the Actions run if the first release does not appear.

## What runs on each main push

`.github/workflows/release.yml` builds for each push to main, including the first push. It can also be run manually on main. Feature-branch pushes and pull requests run checks but do not publish this workflow's releases.

1. Read the base version from Cargo.toml and add this release workflow's `github.run_number` to its patch component. For example, base `0.1.0` and run 12 produce **0.1.12**. Each new workflow run gets a new version; re-running the same run targets the same version.
2. Stamp that version into Cargo, both DLL projects, and shared plugin metadata. These build-time edits are not committed or pushed back to main.
3. Build and test the plugin, then build/test Windows and Linux managers. Protocol, manager process, backup, parser and audit regression checks gate packaging. CI cannot run the copyrighted game; the native acceptance checks remain a maintainer responsibility.
4. Package only an explicit set of distribution files. The client ZIP uses explicit BepInEx plugin/patcher folders for Vortex and manual extraction. Verify its actual DLL/document payloads, layout, version, CRCs and SHA-256 before uploading artifacts. Installed games, personal saves, local investigation/session files and raw test captures are excluded.
5. After **both server platforms** succeed, publish one release tagged `v<version>` at the exact triggering commit, with all files attached together. A failed build publishes no release. There is no cancellation policy that deliberately discards earlier main pushes.

## Release assets

| Asset | Audience |
|---|---|
| `moon-client.zip` | Players: Vortex/manual-install BepInEx folder layout with both DLLs and instructions |
| `MoonDedicated.dll` and `MoonLoaderCompatibility.dll` | Manual updates; install in plugins and patchers respectively |
| `moon-ds-windows-x86_64.zip` | Windows hosts: moon-server.exe, advanced manager, DLL, AMP files and guides |
| `moon-ds-linux-x86_64.zip` | Linux hosts: native manager/host entry, Windows DLL, AMP files and guides |
| Each asset's `.sha256` | SHA-256 checksum for that file |

Versioned release pages retain old builds. The Latest badge uses GitHub's semantic-version ordering. AMP's expected server ZIP names remain stable; set its Release Repository to this repository's `owner/name` after creating it.

## Local build

`scripts/package.ps1` builds the Windows DLL and executables and calls the same allowlist packager used in CI. Its output is under `dist/`; it does not publish to GitHub. Local versions use the source's base version. `scripts/set-release-version.py MAJOR.MINOR.PATCH` updates the synchronized source version, followed by `cargo update --workspace` to refresh the workspace package version in Cargo.lock.

Before upgrading, stop hosts and clients, retain backups, and replace the executable/DLL files without deleting the existing server directory or configuration. Read README's migration note for worlds from before 0.1.2.
