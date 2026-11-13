"""CLI regression: restore only touches a private configured save tree."""
import json
import pathlib
import subprocess
import tempfile
import zipfile

repo = pathlib.Path(__file__).resolve().parents[2]
with tempfile.TemporaryDirectory(prefix="moon-cli-probe-") as root:
    root = pathlib.Path(root)
    config = root / "server.properties"
    config.write_text("MOON_DATA_DIR=" + str(root / "data"), encoding="utf-8")
    archive = root / "moon-slot1-test.zip"
    with zipfile.ZipFile(archive, "w") as z:
        z.writestr("slot.json", "{}")
    result = subprocess.run([str(repo / "target/release/moon-ds.exe"), "restore", "--config", str(config), str(archive)],
                            cwd=repo, capture_output=True, text=True, timeout=5)
    reproduced = result.returncode == 0 and (root / "data/world/MoonSessions/slot-1/slot.json").read_text() == "{}"
    evidence = {"restored_into_private_configured_world": reproduced, "exit_code": result.returncode, "stderr": result.stderr}
    (pathlib.Path(__file__).parent / "cli-result.json").write_text(json.dumps(evidence, indent=2), encoding="utf-8")
    print(json.dumps(evidence, indent=2))
    assert reproduced
