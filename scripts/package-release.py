"""Package only public distribution files, never an installed game or session captures."""
import argparse
import hashlib
import pathlib
import shutil
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
DOCS = ("INSTALL.md", "CONFIG.md", "HOW-IT-WORKS.md", "CLIENT.md", "RELEASES.md", "LOADER-COMPATIBILITY.md", "AUDIT-CLEANING-2026-11-14.md")


def archive(path, files, version):
    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED) as bundle:
        for source, target in files:
            bundle.write(source, target)
        bundle.writestr("VERSION.txt", version + "\n")
    with zipfile.ZipFile(path) as bundle:
        bad = bundle.testzip()
        if bad:
            raise ValueError(f"Archive verification failed: {bad}")
    checksum(path)


def checksum(path):
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    path.with_name(path.name + ".sha256").write_text(f"{digest}  {path.name}\n", encoding="ascii")


def package(platform, version, output):
    output.mkdir(parents=True, exist_ok=True)
    extension = ".exe" if platform == "windows" else ""
    manager = ROOT / f"target/release/moon-ds{extension}"
    host = ROOT / f"target/release/moon-server{extension}"
    mod = ROOT / "mod/MoonDedicated.dll"
    compatibility = ROOT / "mod/MoonLoaderCompatibility.dll"
    files = [(manager, manager.name), (host, host.name), (mod, "mod/MoonDedicated.dll"), (compatibility, "mod/MoonLoaderCompatibility.dll")]
    files += [(ROOT / name, name) for name in ("README.md", "LICENSE.md", "CREDITS.md", "server.properties.example")]
    files += [(ROOT / "docs" / name, f"docs/{name}") for name in DOCS]
    files += [(p, f"amp/{p.name}") for p in sorted((ROOT / "amp").iterdir()) if p.is_file()]
    files += [(ROOT / "scripts/install-client.ps1", "scripts/install-client.ps1")]
    archive(output / f"moon-ds-{platform}-x86_64.zip", files, version)
    if platform == "windows":
        archive(output / "moon-client.zip", [(mod, "mod/MoonDedicated.dll"),
                (compatibility, "mod/MoonLoaderCompatibility.dll"),
                (ROOT / "scripts/install-client.ps1", "scripts/install-client.ps1"),
                (ROOT / "docs/CLIENT.md", "README.md"), (ROOT / "LICENSE.md", "LICENSE.md"),
                (ROOT / "CREDITS.md", "CREDITS.md")], version)
        for payload in (mod, compatibility):
            shutil.copy2(payload, output / payload.name)
            checksum(output / payload.name)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--platform", choices=("windows", "linux"), required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--output", type=pathlib.Path, default=ROOT / "dist")
    args = parser.parse_args()
    package(args.platform, args.version, args.output)
