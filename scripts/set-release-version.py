"""Stamp one numeric version into both deliverables; CI does not commit this change."""
import argparse
import pathlib
import re


def stamp(root, version):
    if not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise ValueError("version must be MAJOR.MINOR.PATCH")
    replacements = {
        "Cargo.toml": (r'(?m)^version = "[^"]+"', f'version = "{version}"'),
        "mod/MoonDedicated/MoonDedicated.csproj": (r"<Version>[^<]+</Version>", f"<Version>{version}</Version>"),
        "mod/MoonLoaderCompatibility/MoonLoaderCompatibility.csproj": (r"<Version>[^<]+</Version>", f"<Version>{version}</Version>"),
        "mod/MoonDedicated/BuildVersion.cs": (r'Value = "[^"]+"', f'Value = "{version}"'),
    }
    for name, (pattern, replacement) in replacements.items():
        path = root / name
        value, count = re.subn(pattern, replacement, path.read_text(encoding="utf-8"), count=1)
        if count != 1:
            raise ValueError(f"Cannot stamp {name}")
        path.write_text(value, encoding="utf-8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("version")
    parser.add_argument("--root", type=pathlib.Path, default=pathlib.Path(__file__).resolve().parents[1])
    args = parser.parse_args()
    stamp(args.root, args.version)
