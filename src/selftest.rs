//! `--self-test <file.json>`: drives the manager the way an operator and a
//! player would (against the built-in stand-in game), then draws the server
//! browser, and reports each step.

use crate::a2s::{self, Query, ServerInfo};
use crate::config::{self, Settings};
use crate::state;
use crate::supervisor::{Launch, Sink, Supervisor};
use serde_json::json;
use std::path::Path;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::channel;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

pub struct Step {
    pub step: &'static str,
    pub passed: bool,
    pub detail: String,
}

fn step(name: &'static str, r: Result<String, String>) -> Step {
    match r {
        Ok(d) => Step {
            step: name,
            passed: true,
            detail: d,
        },
        Err(d) => Step {
            step: name,
            passed: false,
            detail: d,
        },
    }
}

fn wait_for(
    lines: &Arc<Mutex<Vec<String>>>,
    needle: &str,
    from: usize,
    secs: u64,
) -> Option<usize> {
    let end = Instant::now() + Duration::from_secs(secs);
    while Instant::now() < end {
        {
            let l = lines.lock().unwrap();
            if let Some(i) = l.iter().skip(from).position(|x| x.contains(needle)) {
                return Some(from + i);
            }
        }
        std::thread::sleep(Duration::from_millis(50));
    }
    None
}

fn config_step() -> Result<String, String> {
    let s = Settings::default();
    if s.password_enabled() || s.allowlist_enabled() {
        return Err("password or allow-list is enabled by default".into());
    }
    if (s.game_port, s.query_port) != (27025, 27026) {
        return Err("default ports are wrong".into());
    }
    let mut t = Settings::default();
    t.apply(&config::parse_properties(
        "MOON_SERVER_PASSWORD=''\nMOON_ALLOWED_STEAM_IDS=''\n",
    ))
    .map_err(|e| e.to_string())?;
    if t.password_enabled() || t.allowlist_enabled() {
        return Err("blank values did not stay disabled".into());
    }
    let mut u = Settings::default();
    if u.apply(&config::parse_properties("MOON_ALLOWED_STEAM_IDS=123\n"))
        .is_ok()
    {
        return Err("a bad SteamID was accepted".into());
    }
    let mut v = Settings::default();
    v.apply(&config::parse_properties(
        "MOON_REQUIRED_MODS='a.b@1.0, c.d'\nMOON_ALLOWED_STEAM_IDS='76561198000000001'\n",
    ))
    .map_err(|e| e.to_string())?;
    if v.required_mods.len() != 2 || v.allowed_steam_ids.len() != 1 {
        return Err("mods or steam ids were not parsed".into());
    }
    Ok("password and allow-list are off by default, blank values stay off, ports 27025/27026, bad SteamIDs refused".into())
}

fn query_step() -> Result<String, String> {
    let sock = std::net::UdpSocket::bind("127.0.0.1:0").map_err(|e| e.to_string())?;
    let port = sock.local_addr().map_err(|e| e.to_string())?.port();
    let q = Query {
        info: ServerInfo {
            name: "Self-test".into(),
            map: "slot 1".into(),
            game: "Moon Sweepers".into(),
            players: 2,
            max_players: 8,
            password: true,
            version: "1.0.5".into(),
            steam_id: Some(90271234567890123),
            keywords: "moonds".into(),
            ping_ms: 0,
        },
        rules: vec![("moon_mods".into(), "x.y@1.0,z.w".into())],
    };
    let stop = Arc::new(AtomicBool::new(false));
    let s2 = stop.clone();
    let q2 = q.clone();
    let h = std::thread::spawn(move || a2s::respond(sock, q2, 27025, s2));
    let got = a2s::query(
        &format!("127.0.0.1:{port}"),
        0,
        Duration::from_millis(2000),
        true,
    );
    stop.store(true, Ordering::Relaxed);
    let _ = h.join();
    let got = got?;
    if got.info.steam_id != q.info.steam_id || !got.info.password || got.required_mods().len() != 2
    {
        return Err(format!("unexpected answer: {:?}", got.info));
    }
    Ok(format!("asked a server by address: got its Steam ID, {}/{} players, password flag, {} required mods", got.info.players, got.info.max_players, got.required_mods().len()))
}

pub struct Server {
    pub lines: Arc<Mutex<Vec<String>>>,
    pub console: std::sync::mpsc::Sender<String>,
    pub stop: Arc<AtomicBool>,
    pub handle: std::thread::JoinHandle<i32>,
    pub settings: Settings,
}

pub fn start_fake(dir: &Path) -> Server {
    let mut s = Settings {
        data_dir: dir.to_path_buf(),
        backup_keep: 2,
        autosave_minutes: 0,
        password: "hunter2".into(),
        ..Default::default()
    };
    s.finish().unwrap();
    let lines = Arc::new(Mutex::new(Vec::new()));
    let l2 = lines.clone();
    let sink: Sink = Arc::new(move |l: String| l2.lock().unwrap().push(l));
    let sup = Supervisor {
        s: s.clone(),
        launch: Launch::Fake(std::env::current_exe().unwrap()),
        sink,
    };
    let (tx, rx) = channel();
    let stop = Arc::new(AtomicBool::new(false));
    let st = stop.clone();
    let handle = std::thread::spawn(move || sup.run(rx, st));
    Server {
        lines,
        console: tx,
        stop,
        handle,
        settings: s,
    }
}

pub fn run_server_steps() -> Vec<Step> {
    let mut steps = Vec::new();
    steps.push(step("config", config_step()));
    steps.push(step("query", query_step()));

    let dir = std::env::temp_dir().join(format!("moon-ds-selftest-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&dir);
    let srv = start_fake(&dir);
    let rt = srv.settings.runtime_dir();

    // move: the server starts, becomes ready, and players come and go
    let t0 = Instant::now();
    let mv = (|| -> Result<String, String> {
        wait_for(&srv.lines, "READY", 0, 20).ok_or("the server never became ready")?;
        let ready_ms = t0.elapsed().as_millis();
        state::send_command(&rt, "fake-join 76561198000000001 Ada").map_err(|e| e.to_string())?;
        state::send_command(&rt, "fake-join 76561198000000002 Bo").map_err(|e| e.to_string())?;
        wait_for(&srv.lines, "JOIN Ada", 0, 10)
            .ok_or("join of the first player was not reported")?;
        wait_for(&srv.lines, "JOIN Bo", 0, 10)
            .ok_or("join of the second player was not reported")?;
        state::send_command(&rt, "fake-leave 76561198000000001").map_err(|e| e.to_string())?;
        wait_for(&srv.lines, "LEAVE Ada", 0, 10).ok_or("leave was not reported")?;
        Ok(format!(
            "server ready after {ready_ms} ms; 2 players joined and 1 left, all reported"
        ))
    })();
    steps.push(step("move", mv));

    // act: save and back up the world, old backups are pruned
    let act = (|| -> Result<String, String> {
        let mut made = 0;
        for _ in 0..3 {
            let from = srv.lines.lock().unwrap().len();
            srv.console
                .send("backup".into())
                .map_err(|e| e.to_string())?;
            wait_for(&srv.lines, "BACKUP ", from, 20).ok_or("backup did not finish")?;
            made += 1;
        }
        let kept = crate::backup::list(&srv.settings.backup_dir);
        if kept.len() != 2 {
            return Err(format!("expected 2 kept backups, found {}", kept.len()));
        }
        let f = std::fs::File::open(kept.last().unwrap()).map_err(|e| e.to_string())?;
        let z = zip::ZipArchive::new(f).map_err(|e| e.to_string())?;
        if !z.file_names().any(|n| n == "session.moon") {
            return Err("backup does not contain the world file".into());
        }
        Ok(format!(
            "saved and backed up {made} times, kept the newest {}, world file inside",
            kept.len()
        ))
    })();
    steps.push(step("act", act));

    // stop: graceful stop saves, backs up and exits cleanly
    let stop = (|| -> Result<String, String> {
        let from = srv.lines.lock().unwrap().len();
        srv.console.send("stop".into()).map_err(|e| e.to_string())?;
        wait_for(&srv.lines, "STOPPED", from, 20).ok_or("the server did not stop")?;
        let code = srv
            .handle
            .join()
            .map_err(|_| "supervisor panicked".to_string())?;
        if code != 0 {
            return Err(format!("exit code {code}"));
        }
        let l = srv.lines.lock().unwrap();
        if !l.iter().skip(from).any(|x| x.contains("BACKUP ")) {
            return Err("no backup was made on stop".into());
        }
        Ok("stop saved the world, made a final backup and exited with code 0".into())
    })();
    steps.push(step("stop", stop));
    srv.stop.store(true, Ordering::SeqCst);
    let _ = std::fs::remove_dir_all(&dir);
    steps
}

pub fn hud_step(seen: &[String]) -> Step {
    let missing: Vec<&&str> = crate::gui::EXPECTED_HUD
        .iter()
        .filter(|e| !seen.iter().any(|s| s == **e))
        .collect();
    if missing.is_empty() {
        Step { step: "hud", passed: true, detail: "server list, favourite, join button, player counts, lock and required-mods notice are drawn".into() }
    } else {
        Step {
            step: "hud",
            passed: false,
            detail: format!("not drawn: {missing:?}"),
        }
    }
}

pub fn write(path: &str, steps: &[Step]) {
    let v = json!({
        "version": 1,
        "steps": steps.iter().map(|s| json!({"step": s.step, "passed": s.passed, "detail": s.detail})).collect::<Vec<_>>()
    });
    if let Some(p) = std::path::Path::new(path).parent() {
        let _ = std::fs::create_dir_all(p);
    }
    let _ = std::fs::write(path, serde_json::to_vec_pretty(&v).unwrap());
}
