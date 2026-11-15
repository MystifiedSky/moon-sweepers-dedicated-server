"""Verify the actual client release archive before uploading it."""
import argparse
import hashlib
import pathlib
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
PLUGIN = "BepInEx/plugins/MoonDedicated/"
PATCHER = "BepInEx/patchers/MoonDedicated/"


def verify(path, version, root=ROOT):
    sources = {
        PLUGIN + "MoonDedicated.dll": root / "mod/MoonDedicated.dll",
        PATCHER + "MoonLoaderCompatibility.dll": root / "mod/MoonLoaderCompatibility.dll",
        PLUGIN + "docs/README.md": root / "docs/CLIENT.md",
        PLUGIN + "docs/LICENSE.md": root / "LICENSE.md",
        PLUGIN + "docs/CREDITS.md": root / "CREDITS.md",
    }
    with zipfile.ZipFile(path) as bundle:
        names = bundle.namelist()
        assert len(names) == len(set(names)), "Duplicate archive paths"
        assert bundle.testzip() is None, "Corrupt archive"
        assert {n for n in names if not n.endswith("/")} == set(sources) | {PLUGIN + "docs/VERSION.txt"}, "Unexpected client payload"
        assert {"BepInEx/plugins/", "BepInEx/patchers/"} <= set(names), "Missing explicit installer folders"
        for name, source in sources.items():
            assert bundle.read(name) == source.read_bytes(), f"Stale payload: {name}"
        assert bundle.read(PLUGIN + "docs/VERSION.txt") == (version + "\n").encode(), "Wrong version"
    expected = path.with_name(path.name + ".sha256").read_text().split()[0]
    assert hashlib.sha256(path.read_bytes()).hexdigest() == expected, "Checksum mismatch"


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=pathlib.Path, default=ROOT / "dist/moon-client.zip")
    parser.add_argument("--version", required=True)
    args = parser.parse_args()
    verify(args.archive, args.version)
    print("Client archive layout, payloads, version, CRCs and SHA-256 verified.")
