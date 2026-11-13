"""Install in a private game copy and validate/roll back hooks before any world starts."""
import json
import os
import pathlib
import shutil
import subprocess
import sys

here = pathlib.Path(__file__).resolve().parent
repo = here.parents[1]
source = pathlib.Path(r"F:\SteamLibrary\steamapps\common\Moon Sweepers")
game = here / "game-smoke"
game.mkdir(exist_ok=True)
if not (game / "MoonSweepers.exe").exists():
    print("Copying installed game and loader into an ignored, private smoke-test folder", flush=True)
    for name in ["MoonSweepers_Data", "dotnet", "D3D12", "BepInEx"]:
        ignore = shutil.ignore_patterns("*.log", "plugins", "moon-ds") if name == "BepInEx" else None
        shutil.copytree(source / name, game / name, ignore=ignore, dirs_exist_ok=True)
    for entry in source.iterdir():
        if entry.is_file() and entry.suffix.lower() in [".dll", ".exe", ".ini", ".config"]:
            shutil.copy2(entry, game / entry.name)
    (game / "steam_appid.txt").write_text("5335510", encoding="ascii")
if not (game / "MoonSweepers_Data/Plugins/x86_64/steam_api64.dll").exists():
    shutil.copytree(source / "MoonSweepers_Data/Plugins", game / "MoonSweepers_Data/Plugins", dirs_exist_ok=True)
plugins = game / "BepInEx/plugins/MoonDedicated"
plugins.mkdir(parents=True, exist_ok=True)
shutil.copy2(repo / "mod/MoonDedicated.dll", plugins / "MoonDedicated.dll")
patchers = game / "BepInEx/patchers/MoonDedicated"
patchers.mkdir(parents=True, exist_ok=True)
shutil.copy2(repo / "mod/MoonLoaderCompatibility.dll", patchers / "MoonLoaderCompatibility.dll")
results = []
roles = sys.argv[1:] or ["client", "server"]
for role in roles:
    runtime = game / ("runtime-" + role)
    abort_test = role == "server-abort"
    env = dict(os.environ, MOON_VALIDATE_ONLY="0" if abort_test else "1", MOON_DEDICATED="1" if role.startswith("server") else "0",
               MOON_DS_RUNTIME=str(runtime), MOON_DS_INSTANCE_ID="smoke-" + role,
               SteamAppId="5335510", SteamGameId="5335510")
    if abort_test:
        env.update(MOON_GAME_PORT="27026", MOON_QUERY_PORT="27026")
    log = game / (role + "-unity.log")
    process = subprocess.Popen([str(game / "MoonSweepers.exe"), "-batchmode", "-nographics", "-logFile", str(log)],
                               cwd=game, env=env, creationflags=0x08000000 if os.name == "nt" else 0,
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    timed_out = False
    try:
        code = process.wait(timeout=45)
    except subprocess.TimeoutExpired:
        timed_out = True
        process.kill()
        code = process.wait(timeout=10)
    loader = game / "BepInEx/LogOutput.log"
    text = loader.read_text(encoding="utf-8", errors="replace") if loader.exists() else ""
    (here / ("smoke-" + role + ".log")).write_text(text, encoding="utf-8")
    loader_issues = [line for line in text.splitlines() if line.startswith(("[Warning:", "[Error", "[Fatal"))]
    if abort_test:
        passed = not timed_out and code == 1 and "Dedicated startup aborted before gameplay" in text and "starting the world from save slot" not in text
    else:
        passed = not timed_out and code == 0 and "VALIDATION PASSED" in text and not loader_issues
    result = {"role": role, "passed": passed, "exit_code": code, "timed_out": timed_out, "loader_issues": loader_issues,
              "plugin_log": [line for line in text.splitlines() if "Moon Dedicated" in line or "startup failed" in line or "VALIDATION" in line or "Unsupported running" in line]}
    results.append(result)
    print(json.dumps(result, indent=2), flush=True)
(here / "smoke-result.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
assert all(r["passed"] for r in results), "Game hook validation failed; inspect smoke logs"
