//! Runs the game as a headless dedicated server and looks after it:
//! start, watch the mod's heartbeat, autosave, scheduled backups on demand,
//! graceful stop (save first), and restart after a crash.
//!
//! Console lines other tools rely on (AMP regexes in the template):
//!   [moon-ds] READY server_steam_id=<id>
//!   [moon-ds] JOIN <name> (<steamid>)
//!   [moon-ds] LEAVE <name> (<steamid>)
//!   [moon-ds] SAVED
//!   [moon-ds] BACKUP <file>
//!   [moon-ds] STOPPED

use crate::backup;
use crate::config::{self, Settings};
use crate::state::{self, HostState, Player};
use std::io::{BufRead, Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::Receiver;
use std::sync::Arc;
use std::time::{Duration, Instant};

#[derive(Clone)]
pub enum Launch {
    /// The real game (Windows directly, Linux through Wine).
    Game,
    /// This executable in `fake-game` mode: a stand-in used by the self-test.
    Fake(PathBuf),
}

pub type Sink = Arc<dyn Fn(String) + Send + Sync>;

pub fn stdout_sink() -> Sink {
    Arc::new(|l: String| {
        use std::io::Write;
        let out = std::io::stdout();
        let mut o = out.lock();
        let _ = writeln!(o, "{l}");
        let _ = o.flush();
    })
}

pub struct Supervisor {
    pub s: Settings,
    pub launch: Launch,
    pub sink: Sink,
}

enum Pending {
    None,
    Backup { since: Instant, request_id: String },
}

impl Supervisor {
    fn say(&self, msg: impl AsRef<str>) {
        (self.sink)(format!("[moon-ds] {}", state::console_field(msg.as_ref())));
    }

    /// Where the world lives, as reported by the mod or guessed from the standard Unity location.
    pub fn save_dir(&self, st: Option<&HostState>) -> Option<PathBuf> {
        if let Some(st) = st {
            if !st.save_dir.is_empty() {
                return Some(PathBuf::from(&st.save_dir));
            }
        }
        guess_save_dir(&self.s)
    }

    fn prepare(&self) -> Result<(), String> {
        let rt = self.s.runtime_dir();
        std::fs::create_dir_all(&rt).map_err(|e| format!("cannot create {}: {e}", rt.display()))?;
        let _ = std::fs::remove_file(state::state_path(&rt));
        let _ = std::fs::remove_dir_all(rt.join("commands"));
        let _ = std::fs::remove_dir_all(rt.join("admin"));
        std::fs::create_dir_all(&self.s.backup_dir)
            .map_err(|e| format!("cannot create {}: {e}", self.s.backup_dir.display()))?;
        if let Launch::Game = self.launch {
            config::check_game_dir(&self.s.game_dir)?;
            install_payload(&self.s, &|m| self.say(m))?;
            install_redist(&self.s, &|m| self.say(m));
        }
        Ok(())
    }

    fn spawn(&self, helper: &mut Helper, instance: &str) -> Result<Child, String> {
        let rt = self.s.runtime_dir();
        let log = rt.join("game.log");
        let _ = std::fs::write(&log, b"");
        let mut cmd = match &self.launch {
            Launch::Fake(exe) => {
                let mut c = Command::new(exe);
                c.arg("fake-game")
                    .arg("--runtime")
                    .arg(&rt)
                    .arg("--slot")
                    .arg(self.s.save_slot.to_string());
                c
            }
            Launch::Game => {
                let game_args: Vec<String> = vec![
                    "-batchmode".into(),
                    "-nographics".into(),
                    "-logFile".into(),
                    log.to_string_lossy().into_owned(),
                    "-moon-dedicated".into(),
                ];
                if cfg!(windows) {
                    let mut c = Command::new(self.s.game_dir.join(config::exe_name()));
                    c.args(&game_args);
                    c
                } else {
                    let mut c = Command::new(&self.s.wine_bin);
                    c.arg(config::exe_name()).args(&game_args);
                    c.env("WINEPREFIX", &self.s.wine_prefix)
                        .env("WINEARCH", "win64")
                        .env("WINEDEBUG", "-all");
                    // BepInEx's loader is a proxy winhttp.dll: Wine must prefer the one next to the game.
                    c.env("WINEDLLOVERRIDES", "winhttp=n,b");
                    if std::env::var_os("DISPLAY").is_none() {
                        let (child, display) = start_xvfb()?;
                        helper.0 = Some(child);
                        c.env("DISPLAY", display);
                    }
                    c
                }
            }
        };
        if let Launch::Game = self.launch {
            cmd.current_dir(&self.s.game_dir);
            cmd.args(&self.s.extra_args);
        }
        for (k, v) in self.s.child_env() {
            cmd.env(k, v);
        }
        cmd.env("MOON_DS_INSTANCE_ID", instance);
        cmd.env("SteamAppId", config::APP_ID.to_string())
            .env("SteamGameId", config::APP_ID.to_string());
        cmd.stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null());
        // Own process group, so a forced stop can also end Wine's helper processes.
        #[cfg(unix)]
        {
            use std::os::unix::process::CommandExt;
            cmd.process_group(0);
        }
        cmd.spawn()
            .map_err(|e| format!("cannot start the game: {e}"))
    }

    /// Run until stopped. Returns the process exit code.
    pub fn run(&self, console: Receiver<String>, stop: Arc<AtomicBool>) -> i32 {
        let _lock = match state::lock_runtime(&self.s.runtime_dir()) {
            Ok(lock) => lock,
            Err(e) => {
                self.say(format!("ERROR {e}"));
                return 1;
            }
        };
        let world = match &self.launch {
            Launch::Game => guess_save_dir(&self.s),
            Launch::Fake(_) => Some(
                self.s
                    .runtime_dir()
                    .join("fake-save")
                    .join(format!("slot-{}", self.s.save_slot)),
            ),
        };
        let _world_lock = match world
            .as_deref()
            .ok_or("the save folder is unknown".to_string())
            .and_then(state::lock_world)
        {
            Ok(lock) => lock,
            Err(e) => {
                self.say(format!("ERROR {e}"));
                return 1;
            }
        };
        if let Err(e) = self.prepare() {
            self.say(format!("ERROR {e}"));
            return 1;
        }
        let _query = if matches!(self.launch, Launch::Game) && !self.s.steam_listing {
            match crate::host_query::start(&self.s) {
                Ok(q) => Some(q),
                Err(e) => {
                    self.say(format!("ERROR {e}"));
                    return 1;
                }
            }
        } else {
            None
        };
        let mut restarts = 0u32;
        loop {
            if stop.load(Ordering::SeqCst) {
                self.say("STOPPED");
                return 0;
            }
            let instance = format!("{}-{}-{restarts}", state::now_ms(), std::process::id());
            let _ = std::fs::remove_file(state::state_path(&self.s.runtime_dir()));
            let _ = state::take_commands(&self.s.runtime_dir());
            let mut helper = Helper::default();
            let mut child = match self.spawn(&mut helper, &instance) {
                Ok(c) => c,
                Err(e) => {
                    self.say(format!("ERROR {e}"));
                    return 1;
                }
            };
            self.say(format!("starting game server (pid {})", child.id()));
            let outcome = self.supervise(&mut child, &console, &stop, &instance);
            kill_tree(&mut child);
            let _ = child.wait();
            drop(helper);
            match outcome {
                Outcome::Stopped => {
                    self.say("STOPPED");
                    return 0;
                }
                Outcome::StopFailed => {
                    self.say("STOPPED save_failed=true");
                    return 3;
                }
                Outcome::Crashed(code) => {
                    self.say(format!("game exited unexpectedly (code {code})"));
                    if restarts < self.s.auto_restart && !stop.load(Ordering::SeqCst) {
                        restarts += 1;
                        self.say(format!("restarting ({restarts}/{})", self.s.auto_restart));
                        std::thread::sleep(Duration::from_secs(5));
                        continue;
                    }
                    self.say("STOPPED");
                    return 2;
                }
            }
        }
    }

    fn supervise(
        &self,
        child: &mut Child,
        console: &Receiver<String>,
        stop: &Arc<AtomicBool>,
        instance: &str,
    ) -> Outcome {
        let rt = self.s.runtime_dir();
        let mut tail = LogTail::new(rt.join("game.log"));
        let mut known: Vec<Player> = Vec::new();
        let mut was_ready = false;
        let mut last_saved_ms = 0u64;
        let mut stale_warned = false;
        let mut save_failure_warned = false;
        let mut last_phase_error = String::new();
        let started = Instant::now();
        let mut next_autosave =
            Instant::now() + Duration::from_secs(self.s.autosave_minutes as u64 * 60);
        let mut pending = Pending::None;
        let mut stopping: Option<Instant> = None;
        let mut slow_warned = false;
        loop {
            for line in tail.read_new() {
                (self.sink)(format!("[game] {line}"));
            }
            if let Ok(Some(status)) = child.try_wait() {
                for line in tail.read_new() {
                    (self.sink)(format!("[game] {line}"));
                }
                return if stopping.is_some() {
                    let final_state = state::read_state(&rt);
                    let clean = status.success()
                        && final_state
                            .as_ref()
                            .map(|s| {
                                s.instance_id == instance
                                    && !s.save_failed
                                    && !s.save_pending
                                    && s.phase == "stopped"
                            })
                            .unwrap_or(false);
                    if clean {
                        self.after_stop();
                        Outcome::Stopped
                    } else {
                        self.say("ERROR shutdown did not confirm a successful final save");
                        Outcome::StopFailed
                    }
                } else {
                    Outcome::Crashed(status.code().unwrap_or(-1))
                };
            }
            let st = state::read_state(&rt).filter(|s| s.instance_id == instance);
            if let Some(st) = &st {
                if st.ready && !was_ready {
                    was_ready = true;
                    self.say(format!("READY server_steam_id={}", st.server_steam_id));
                    self.say(format!(
                        "players join by address <host>:{} (query port) or Steam ID {}",
                        self.s.query_port, st.server_steam_id
                    ));
                }
                if !st.ready && was_ready && stopping.is_none() {
                    was_ready = false;
                    self.say(format!("not ready ({})", st.phase));
                }
                for p in &st.players {
                    if !known.contains(p) {
                        self.say(format!("JOIN {} ({})", p.name, p.steam_id));
                    }
                }
                for p in &known {
                    if !st.players.contains(p) {
                        self.say(format!("LEAVE {} ({})", p.name, p.steam_id));
                    }
                }
                known = st.players.clone();
                if st.last_save_unix_ms > last_saved_ms {
                    if last_saved_ms != 0 {
                        self.say("SAVED");
                    }
                    last_saved_ms = st.last_save_unix_ms;
                }
                if st.save_failed && !save_failure_warned {
                    self.say("WARNING the last world save failed; check the game log");
                }
                save_failure_warned = st.save_failed;
                if !st.error.is_empty() && st.phase.contains("error") {
                    if st.error != last_phase_error {
                        self.say(format!("WARNING {}", st.error));
                        last_phase_error.clone_from(&st.error);
                    }
                } else {
                    last_phase_error.clear();
                }
                let age = state::now_ms().saturating_sub(st.heartbeat_unix_ms);
                if st.ready && age > 30_000 && !stale_warned {
                    stale_warned = true;
                    self.say("WARNING the game stopped updating its status (frozen?)");
                } else if age < 10_000 {
                    stale_warned = false;
                }
            } else if !slow_warned && started.elapsed() > Duration::from_secs(300) {
                slow_warned = true;
                self.say("WARNING no status from the game mod after 5 minutes. Is BepInEx installed and the MoonDedicated plugin in BepInEx/plugins?");
            }

            // console commands
            let mut commands = state::take_admin_commands(&rt);
            commands.extend(console.try_iter());
            for line in commands {
                let line = line.trim().to_string();
                if line.is_empty() {
                    continue;
                }
                let (cmd, arg) = match line.split_once(' ') {
                    Some((c, a)) => (c.to_ascii_lowercase(), a.trim().to_string()),
                    None => (line.to_ascii_lowercase(), String::new()),
                };
                match cmd.as_str() {
                    "help" => {
                        let l =
                            "help, status, players, save, backup, say <text>, kick <steamid>, stop";
                        self.say(l);
                    }
                    "status" => self.print_status(st.as_ref()),
                    "players" => {
                        if known.is_empty() {
                            self.say("no players connected");
                        }
                        for p in &known {
                            self.say(format!("player {} ({})", p.name, p.steam_id));
                        }
                    }
                    "save" => {
                        let _ = state::send_command(&rt, "save");
                        self.say("save requested");
                    }
                    "backup" => {
                        if let Pending::None = pending {
                            let request_id = format!("{}-{}", state::now_ms(), std::process::id());
                            if let Err(e) =
                                state::send_command(&rt, &format!("snapshot {request_id}"))
                            {
                                self.say(format!("ERROR cannot request snapshot: {e}"));
                                continue;
                            }
                            pending = Pending::Backup {
                                since: Instant::now(),
                                request_id,
                            };
                            self.say("saving, then backing up…");
                        }
                    }
                    "say" | "kick" => {
                        let _ = state::send_command(&rt, &format!("{cmd} {arg}"));
                    }
                    "stop" | "quit" | "exit" => {
                        if stopping.is_none() {
                            stop.store(true, Ordering::SeqCst);
                        }
                    }
                    other => self.say(format!("unknown command '{other}' (try help)")),
                }
            }

            if stop.load(Ordering::SeqCst) && stopping.is_none() {
                self.say("stopping: saving the world first…");
                let _ = state::send_command(&rt, "stop");
                stopping = Some(Instant::now());
            }
            if let Some(t) = stopping {
                if t.elapsed() > Duration::from_secs(self.s.stop_timeout_secs as u64) {
                    self.say("the game did not stop in time; ending it");
                    kill_tree(child);
                    let _ = child.wait();
                    self.say("ERROR forced shutdown; final save was not confirmed");
                    return Outcome::StopFailed;
                }
            }

            // autosave
            if self.s.autosave_minutes > 0 && stopping.is_none() && Instant::now() >= next_autosave
            {
                next_autosave =
                    Instant::now() + Duration::from_secs(self.s.autosave_minutes as u64 * 60);
                if st.as_ref().map(|s| s.ready).unwrap_or(false) {
                    let _ = state::send_command(&rt, "save");
                }
            }

            // backup state machine
            if let Pending::Backup { since, request_id } = &pending {
                let done = st
                    .as_ref()
                    .map(|s| {
                        s.snapshot_request_id == *request_id
                            && !s.snapshot_dir.is_empty()
                            && s.snapshot_error.is_empty()
                            && !s.save_pending
                    })
                    .unwrap_or(false);
                let failed = st
                    .as_ref()
                    .map(|s| s.snapshot_request_id == *request_id && !s.snapshot_error.is_empty())
                    .unwrap_or(false);
                if done || failed || since.elapsed() > Duration::from_secs(120) {
                    if !done {
                        self.say(format!(
                            "ERROR snapshot did not finish; no backup was created: {}",
                            st.as_ref()
                                .filter(|s| s.snapshot_request_id == *request_id)
                                .map(|s| s.snapshot_error.as_str())
                                .unwrap_or("request timed out")
                        ));
                    } else if let Some(s) = &st {
                        let snapshot = PathBuf::from(&s.snapshot_dir);
                        let root = rt.join("snapshots").canonicalize();
                        if let (Ok(root), Ok(path)) = (root, snapshot.canonicalize()) {
                            if path.parent() == Some(root.as_path()) {
                                match backup::create(
                                    &path,
                                    &self.s.backup_dir,
                                    self.s.save_slot,
                                    self.s.backup_keep,
                                ) {
                                    Ok(p) => self.say(format!("BACKUP {}", p.display())),
                                    Err(e) => self.say(format!("ERROR backup failed: {e}")),
                                }
                                let _ = std::fs::remove_dir_all(path);
                            } else {
                                self.say(
                                    "ERROR snapshot is outside the runtime snapshot directory",
                                );
                            }
                        } else {
                            self.say("ERROR snapshot folder is unavailable");
                        }
                    }
                    pending = Pending::None;
                }
            }
            std::thread::sleep(Duration::from_millis(250));
        }
    }

    fn after_stop(&self) {
        if !self.s.backup_on_stop {
            return;
        }
        let st = state::read_state(&self.s.runtime_dir());
        if st.as_ref().map(|s| s.save_failed).unwrap_or(false) {
            self.say("WARNING not making a stop-backup because the final save failed");
            return;
        }
        self.do_backup(st.as_ref());
    }

    fn do_backup(&self, st: Option<&HostState>) {
        match self.save_dir(st) {
            Some(dir) => match backup::create(
                &dir,
                &self.s.backup_dir,
                self.s.save_slot,
                self.s.backup_keep,
            ) {
                Ok(p) => self.say(format!("BACKUP {}", p.display())),
                Err(e) => self.say(format!("WARNING backup failed: {e}")),
            },
            None => self.say("WARNING backup skipped: the save folder is unknown"),
        }
    }

    fn print_status(&self, st: Option<&HostState>) {
        match st {
            None => self.say("status: starting (no status from the game yet)"),
            Some(s) => {
                self.say(format!(
                    "status: phase={} ready={} players={}/{} server_steam_id={} save_pending={} save_failed={}",
                    s.phase,
                    s.ready,
                    s.players.len(),
                    self.s.max_players,
                    s.server_steam_id,
                    s.save_pending,
                    s.save_failed
                ));
                if !s.mods.is_empty() {
                    self.say(format!("mods on server: {}", s.mods.join(", ")));
                }
            }
        }
    }
}

enum Outcome {
    Stopped,
    StopFailed,
    Crashed(i32),
}

struct LogTail {
    path: PathBuf,
    pos: u64,
}

impl LogTail {
    fn new(path: PathBuf) -> Self {
        LogTail { path, pos: 0 }
    }
    fn read_new(&mut self) -> Vec<String> {
        let Ok(mut f) = std::fs::File::open(&self.path) else {
            return vec![];
        };
        let len = f.metadata().map(|m| m.len()).unwrap_or(0);
        if len < self.pos {
            self.pos = 0;
        }
        if len == self.pos || f.seek(SeekFrom::Start(self.pos)).is_err() {
            return vec![];
        }
        let mut buf = Vec::new();
        let _ = f.by_ref().take(256 * 1024).read_to_end(&mut buf);
        // only whole lines
        let end = buf
            .iter()
            .rposition(|&b| b == b'\n')
            .map(|i| i + 1)
            .unwrap_or(0);
        // A single line can exceed the read budget (for example a serialized
        // exception payload). Always consume a full budget rather than reading
        // the same prefix forever and hiding every later log entry.
        if end == 0 && buf.len() == 256 * 1024 {
            self.pos += buf.len() as u64;
            return vec![format!(
                "{} [long log line continues]",
                String::from_utf8_lossy(&buf)
            )];
        }
        self.pos += end as u64;
        String::from_utf8_lossy(&buf[..end])
            .lines()
            .map(|l| l.trim_end().to_string())
            .filter(|l| !l.is_empty())
            .collect()
    }
}

#[cfg(test)]
mod log_tail_tests {
    use super::LogTail;
    #[test]
    fn oversized_log_line_does_not_hide_later_events() {
        let path = std::env::temp_dir().join(format!(
            "moon-log-tail-{}-{}.log",
            std::process::id(),
            crate::state::now_ms()
        ));
        let mut bytes = vec![b'x'; 300_000];
        bytes.extend_from_slice(b"\nNEXT EVENT\n");
        std::fs::write(&path, bytes).unwrap();
        let mut tail = LogTail::new(path.clone());
        assert!(!tail.read_new().is_empty());
        assert!(tail.read_new().iter().any(|line| line == "NEXT EVENT"));
        assert!(tail.read_new().is_empty());
        std::fs::remove_file(path).unwrap();
    }
}

/// End the game and, on Unix, everything in its process group (Wine starts helper processes).
fn kill_tree(child: &mut Child) {
    #[cfg(unix)]
    {
        let _ = Command::new("kill")
            .args(["-KILL", &format!("-{}", child.id())])
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .status();
    }
    let _ = child.kill();
}

#[derive(Default)]
struct Helper(Option<Child>);
impl Drop for Helper {
    fn drop(&mut self) {
        if let Some(child) = &mut self.0 {
            let _ = child.kill();
            let _ = child.wait();
        }
    }
}

#[cfg(unix)]
fn start_xvfb() -> Result<(Child, String), String> {
    // Xvfb atomically chooses an unused display and publishes it only when ready.
    let (reader, writer) = std::os::unix::net::UnixStream::pair().map_err(|e| e.to_string())?;
    reader
        .set_read_timeout(Some(Duration::from_secs(10)))
        .map_err(|e| e.to_string())?;
    let mut helper = Helper(Some(
        Command::new("Xvfb")
            .args([
                "-displayfd",
                "1",
                "-screen",
                "0",
                "640x480x24",
                "-nolisten",
                "tcp",
            ])
            .stdin(Stdio::null())
            .stdout(Stdio::from(std::os::fd::OwnedFd::from(writer)))
            .stderr(Stdio::null())
            .spawn()
            .map_err(|e| format!("cannot start Xvfb: {e}"))?,
    ));
    let mut line = String::new();
    std::io::BufReader::new(reader)
        .take(32)
        .read_line(&mut line)
        .map_err(|e| format!("Xvfb readiness: {e}"))?;
    let display: u32 = line
        .trim()
        .parse()
        .map_err(|_| "Xvfb did not report a ready display")?;
    if helper
        .0
        .as_mut()
        .unwrap()
        .try_wait()
        .map_err(|e| e.to_string())?
        .is_some()
    {
        return Err("Xvfb exited before game launch".into());
    }
    Ok((helper.0.take().unwrap(), format!(":{display}")))
}

#[cfg(not(unix))]
fn start_xvfb() -> Result<(Child, String), String> {
    Err("Xvfb is only supported on Unix".into())
}

/// Copy the mod next to BepInEx. BepInEx itself must already be installed in the game folder.
pub fn install_payload(s: &Settings, say: &dyn Fn(&str)) -> Result<(), String> {
    let bep = s.game_dir.join("BepInEx");
    if !bep.join("core").is_dir() {
        return Err(format!(
            "BepInEx is not installed in '{}'. Install BepInEx 6 (IL2CPP) and the Moon Sweepers BepInEx patcher into the game folder first (see docs/INSTALL.md).",
            s.game_dir.display()
        ));
    }
    let exe_dir = std::env::current_exe()
        .ok()
        .and_then(|p| p.parent().map(|p| p.to_path_buf()))
        .unwrap_or_default();
    for (filename, folder) in [
        ("MoonDedicated.dll", "plugins"),
        ("MoonLoaderCompatibility.dll", "patchers"),
    ] {
        let candidates = [
            exe_dir.join("mod").join(filename),
            exe_dir.join(filename),
            PathBuf::from("mod").join(filename),
        ];
        let target_dir = bep.join(folder).join("MoonDedicated");
        let target = target_dir.join(filename);
        if let Some(src) = candidates.iter().find(|p| p.exists()) {
            std::fs::create_dir_all(&target_dir).map_err(|e| e.to_string())?;
            let bytes = std::fs::read(src).map_err(|e| format!("cannot read {filename}: {e}"))?;
            if std::fs::read(&target).ok().as_ref() != Some(&bytes) {
                std::fs::write(&target, bytes)
                    .map_err(|e| format!("cannot install {filename}: {e}"))?;
                say(&format!("installed mod into {}", target.display()));
            }
        } else if !target.exists() {
            return Err(format!("{filename} was not found in mod/ beside moon-ds and is not installed in BepInEx/{folder} yet. Extract the complete server release."));
        }
    }
    // SteamInternal_GameServer_Init returns failure when Steam cannot work out the app id (decompiled:
    // steam_api64.dll 0x13b408be0 checks the id from SteamUtils), so give it the standard file as well as the env vars.
    let appid = s.game_dir.join("steam_appid.txt");
    if std::fs::read_to_string(&appid)
        .map(|t| t.trim() != config::APP_ID.to_string())
        .unwrap_or(true)
    {
        let _ = std::fs::write(&appid, config::APP_ID.to_string());
    }
    Ok(())
}

/// Valve's Steamworks redistributable lets the game log in to Steam as a game server without a Steam client.
pub fn install_redist(s: &Settings, say: &dyn Fn(&str)) {
    let need = ["steamclient64.dll", "tier0_s64.dll", "vstdlib_s64.dll"];
    if need.iter().all(|f| s.game_dir.join(f).exists()) {
        return;
    }
    if s.steam_redist_dir.as_os_str().is_empty() {
        if !s.game_dir.join("steamclient64.dll").exists() {
            say("note: steamclient64.dll is not next to the game and MOON_STEAM_REDIST_DIR is not set; a Steam client on this PC is needed, or download Steamworks SDK Redist (app 1007) with SteamCMD");
        }
        return;
    }
    fn find(dir: &Path, name: &str, depth: u32) -> Option<PathBuf> {
        for e in std::fs::read_dir(dir).ok()?.flatten() {
            let p = e.path();
            if p.is_dir() && depth < 6 {
                if let Some(f) = find(&p, name, depth + 1) {
                    return Some(f);
                }
            } else if p
                .file_name()
                .map(|n| n.to_string_lossy().eq_ignore_ascii_case(name))
                .unwrap_or(false)
            {
                return Some(p);
            }
        }
        None
    }
    for f in need {
        if s.game_dir.join(f).exists() {
            continue;
        }
        match find(&s.steam_redist_dir, f, 0) {
            Some(src) => {
                let _ = std::fs::copy(&src, s.game_dir.join(f));
                let plugins = s
                    .game_dir
                    .join("MoonSweepers_Data")
                    .join("Plugins")
                    .join("x86_64");
                if plugins.is_dir() {
                    let _ = std::fs::copy(&src, plugins.join(f));
                }
                say(&format!(
                    "installed {f} from the Steamworks redistributable"
                ));
            }
            None => say(&format!(
                "warning: {f} not found in {}",
                s.steam_redist_dir.display()
            )),
        }
    }
}

/// Standard Unity location of the world: <persistentDataPath>/MoonSessions/slot-N.
pub fn guess_save_dir(s: &Settings) -> Option<PathBuf> {
    Some(
        s.data_dir
            .join("world")
            .join("MoonSessions")
            .join(format!("slot-{}", s.save_slot)),
    )
}

/// Read console lines from stdin on a thread.
pub fn stdin_channel() -> Receiver<String> {
    let (tx, rx) = std::sync::mpsc::channel();
    std::thread::spawn(move || {
        let stdin = std::io::stdin();
        for line in stdin.lock().lines().map_while(Result::ok) {
            if tx.send(line).is_err() {
                break;
            }
        }
    });
    rx
}
