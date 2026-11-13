"""Process regressions against the actual manager and its fake game only."""
import json
import os
import pathlib
import queue
import subprocess
import tempfile
import threading
import time

repo = pathlib.Path(__file__).resolve().parents[2]
exe = pathlib.Path(os.environ["MOON_TEST_EXE"]) if "MOON_TEST_EXE" in os.environ else repo / "target" / "debug" / ("moon-ds.exe" if os.name == "nt" else "moon-ds")
results = []

class Server:
    def __init__(self, root, mode="", restart=0):
        self.data = pathlib.Path(root) / "server data with spaces"
        self.lines = []
        self.queue = queue.Queue()
        env = dict(os.environ, MOON_FAKE_STOP_MODE=mode)
        self.process = subprocess.Popen(
            [str(exe), "serve", "--fake", "--data-dir", str(self.data),
             "--auto-restart", str(restart), "--stop-timeout", "10"], cwd=repo,
            env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            text=True, encoding="utf-8", errors="replace", bufsize=1)
        def read():
            for line in self.process.stdout:
                self.lines.append(line.rstrip())
                self.queue.put(line)
        self.reader = threading.Thread(target=read, daemon=True)
        self.reader.start()
        self.until("READY")

    def until(self, fragment, seconds=15):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            try:
                if fragment in self.queue.get(timeout=0.1):
                    return
            except queue.Empty:
                if self.process.poll() is not None:
                    break
        raise AssertionError(f"Missing {fragment}: {self.lines}")

    def cli(self, *args):
        result = subprocess.run([str(exe), *args, "--data-dir", str(self.data)],
                                cwd=repo, capture_output=True, text=True, timeout=5)
        assert result.returncode == 0, result.stderr
        return result

    def close(self):
        if self.process.poll() is None:
            self.process.stdin.write("stop\n")
            self.process.stdin.flush()
            try:
                self.process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                # Only the process started by this fixture; never the live game.
                self.process.kill()
                self.process.wait()
        self.reader.join(timeout=2)

def run(name, test, **kwargs):
    with tempfile.TemporaryDirectory(prefix="moon-regression-") as root:
        server = Server(root, **kwargs)
        try:
            test(server)
            results.append({"test": name, "passed": True, "lines": server.lines})
        finally:
            server.close()
    print(name + ": passed", flush=True)

def external_stop(s):
    s.cli("send", "stop")
    assert s.process.wait(timeout=10) == 0
    s.reader.join(timeout=2)
    assert not any("restarting" in line for line in s.lines)
    assert list((s.data / "backups").glob("*.zip"))
run("external stop saves and does not restart", external_stop, restart=1)

def live_backups(s):
    for args in [("backup",), ("send", "backup")]:
        s.cli(*args)
        s.until("BACKUP ")
    archives = list((s.data / "backups").glob("*.zip"))
    assert len(archives) == 2
    import zipfile
    for archive in archives:
        with zipfile.ZipFile(archive) as z:
            assert z.testzip() is None
            assert "moon-ds-backup.json" in z.namelist()
            assert "session.moon" in z.namelist()
run("both backup entry points snapshot the live server", live_backups)

def competing_server(s):
    state_path = s.data / "runtime/state.json"
    # The OS lock must protect even if the heartbeat disappears entirely.
    state_path.unlink(missing_ok=True)
    result = subprocess.run([str(exe), "serve", "--fake", "--data-dir", str(s.data)],
                            cwd=repo, capture_output=True, text=True, timeout=5)
    assert result.returncode != 0 and "in use" in result.stdout
run("concurrent startup is refused without a heartbeat", competing_server)

def warning_recovery(s):
    commands = s.data / "runtime/commands"
    commands.mkdir(parents=True, exist_ok=True)
    for cycle in range(2):
        (commands / f"error-{cycle}.cmd").write_text("fake-save-error", encoding="utf-8")
        s.until("WARNING Injected write failure")
        time.sleep(1.2)  # Multiple manager status polls must not duplicate warnings.
        (commands / f"recovered-{cycle}.cmd").write_text("fake-save-recovered", encoding="utf-8")
        deadline = time.monotonic() + 3
        while json.loads((s.data / "runtime/state.json").read_text())["save_failed"]:
            assert time.monotonic() < deadline
            time.sleep(.05)
        time.sleep(.4)  # Let the manager observe recovery before another failure.
    assert sum("WARNING the last world save failed" in line for line in s.lines) == 2
    assert sum("WARNING Injected write failure" in line for line in s.lines) == 2
run("save warnings report each failure once and reset after recovery", warning_recovery)

def crashed_game(s):
    state_path = s.data / "runtime/state.json"
    old_instance = json.loads(state_path.read_text())["instance_id"]
    command = s.data / "runtime/commands/test-crash.cmd"
    command.parent.mkdir(parents=True, exist_ok=True)
    command.write_text("fake-crash", encoding="utf-8")
    s.until("restarting (1/1)")
    s.until("READY")
    assert json.loads(state_path.read_text())["instance_id"] != old_instance
run("real crash restarts with a new status generation", crashed_game, restart=1)

def failed_shutdown(s):
    s.cli("send", "stop")
    assert s.process.wait(timeout=15) == 3
    assert not list((s.data / "backups").glob("*.zip"))
run("failed final save returns failure", failed_shutdown, mode="save-failed")
run("crash during shutdown returns failure", failed_shutdown, mode="crash")
run("forced shutdown returns failure", failed_shutdown, mode="hang")

path = pathlib.Path(__file__).parent / "runtime-regressions.json"
path.write_text(json.dumps({"tests": results}, indent=2), encoding="utf-8")
print(f"{len(results)} process regressions passed")
