"""Exercise native callbacks through the real game's menu in the private copy."""
import hashlib
import json
import os
import pathlib
import shutil
import subprocess
import time

here = pathlib.Path(__file__).resolve().parent
game = here / "game-smoke"
repo = here.parents[1]
plugin = game / "BepInEx/plugins/MoonDedicated/MoonDedicated.dll"
shutil.copy2(repo / "mod/MoonDedicated.dll", plugin)
status_path = game / "BepInEx/moon-ds/client-status.json"
status_path.unlink(missing_ok=True)
save_root = pathlib.Path(os.environ["USERPROFILE"]) / "AppData/LocalLow/DragDropGames/MoonSweepers/MoonSessions"
def saves():
    return {str(p.relative_to(save_root)): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in save_root.rglob("*") if p.is_file() and not p.name.endswith(".lock")}
before = saves()
env = dict(os.environ, MOON_VALIDATE_ONLY="0", MOON_DEDICATED="0", SteamAppId="5335510", SteamGameId="5335510")
with (here / "client-startup-console.log").open("w", encoding="utf-8") as console:
    process = subprocess.Popen([str(game / "MoonSweepers.exe"), "-batchmode", "-force-d3d11",
                                "-logFile", str(game / "client-startup-unity.log")],
                               cwd=game, env=env, creationflags=0x08000000,
                               stdout=console, stderr=console)
    status = None
    try:
        deadline = time.monotonic() + 35
        while time.monotonic() < deadline and process.poll() is None:
            plugin_log = game / "BepInEx/LogOutput.log"
            if time.monotonic() > deadline - 30 and plugin_log.exists() and "startup failed:" in plugin_log.read_text(errors="replace"):
                break
            if status_path.exists():
                try:
                    status = json.loads(status_path.read_text(encoding="utf-8"))
                    if status["in_menu"] and status["steam_ready"] and status["tick_count"] >= 100:
                        break
                except (json.JSONDecodeError, PermissionError):
                    pass
            time.sleep(0.1)
        passed = process.poll() is None and status is not None and status["in_menu"] and status["steam_ready"] and status["tick_count"] >= 100
        result = {"passed": passed, "status": status, "unexpected_exit": process.poll(), "world_started": bool(status and status["in_game"])}
    finally:
        if process.poll() is None:
            process.kill()  # Only this private-copy process, still in the menu.
            process.wait(timeout=10)
result["live_world_saves_unchanged"] = saves() == before
(here / "client-startup-result.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
print(json.dumps(result, indent=2))
assert result["passed"] and result["live_world_saves_unchanged"] and not result["world_started"], "Full client startup failed"
