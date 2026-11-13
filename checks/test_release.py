"""Exercise version stamping and the actual distribution allowlist in private trees."""
import hashlib
import importlib.util
import pathlib
import tempfile
import unittest
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]


def module(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / "scripts" / f"{name}.py")
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


class Releases(unittest.TestCase):
    def test_stamping_keeps_client_and_server_versions_equal(self):
        with tempfile.TemporaryDirectory() as folder:
            root = pathlib.Path(folder)
            for name in ("Cargo.toml", "mod/MoonDedicated/MoonDedicated.csproj", "mod/MoonLoaderCompatibility/MoonLoaderCompatibility.csproj", "mod/MoonDedicated/BuildVersion.cs"):
                dest = root / name
                dest.parent.mkdir(parents=True, exist_ok=True)
                dest.write_bytes((ROOT / name).read_bytes())
            module("set-release-version").stamp(root, "0.1.57")
            for name in ("Cargo.toml", "mod/MoonDedicated/MoonDedicated.csproj", "mod/MoonLoaderCompatibility/MoonLoaderCompatibility.csproj", "mod/MoonDedicated/BuildVersion.cs"):
                self.assertIn("0.1.57", (root / name).read_text())
            with self.assertRaises(ValueError):
                module("set-release-version").stamp(root, "bad-version")

    def test_packages_include_required_files_and_exclude_private_files(self):
        packer = module("package-release")
        with tempfile.TemporaryDirectory() as folder:
            root = pathlib.Path(folder)
            packer.ROOT = root
            files = ["target/release/moon-ds.exe", "target/release/moon-server.exe", "target/release/moon-ds", "target/release/moon-server", "mod/MoonDedicated.dll", "mod/MoonLoaderCompatibility.dll", "scripts/install-client.ps1", "README.md", "LICENSE.md", "CREDITS.md", "server.properties.example", "amp/template.json"]
            files += [f"docs/{name}" for name in packer.DOCS]
            files += ["craft.json", "agent-session.jsonl", "server/game/MoonSweepers.exe", "server/data/world/private.moon", "docs/AUDIT-private.md"]
            for name in files:
                path = root / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(name)
            for platform in ("windows", "linux"):
                packer.package(platform, "0.1.57", root / "dist")
            for name in ("moon-client.zip", "moon-ds-windows-x86_64.zip", "moon-ds-linux-x86_64.zip"):
                path = root / "dist" / name
                with zipfile.ZipFile(path) as bundle:
                    self.assertIn("mod/MoonDedicated.dll", bundle.namelist())
                    self.assertIn("mod/MoonLoaderCompatibility.dll", bundle.namelist())
                    self.assertEqual(bundle.read("VERSION.txt"), b"0.1.57\n")
                    self.assertFalse(any("craft" in p or p.startswith("server/") or "private" in p for p in bundle.namelist()))
                    if name == "moon-client.zip":
                        self.assertFalse(any(p.endswith(".exe") for p in bundle.namelist()))
                        self.assertIn("scripts/install-client.ps1", bundle.namelist())
                    else:
                        self.assertIn("docs/INSTALL.md", bundle.namelist())
                self.assertEqual(path.with_name(name + ".sha256").read_text().split()[0], hashlib.sha256(path.read_bytes()).hexdigest())


if __name__ == "__main__":
    unittest.main()
