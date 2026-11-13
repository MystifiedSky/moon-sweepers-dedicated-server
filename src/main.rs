// moon-ds: dedicated server manager and server browser for Moon Sweepers.
//
//   moon-ds                      open the server browser (players)
//   moon-ds serve                run the dedicated server (hosts; no GPU needed)
//   moon-ds init | check | status | query <addr> | backup | restore <zip> | send <cmd>

mod a2s;
mod backup;
mod client;
mod config;
mod fakegame;
mod gui;
mod host_query;
mod import;
mod query_jobs;
mod selftest;
mod state;
mod supervisor;

use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::Duration;

const HELP: &str = "moon-ds - Moon Sweepers dedicated server kit

Players:
  moon-ds                         open the server browser (favourites, names, join)
  moon-ds query <host[:port]>     print what a server reports (players, mods, Steam ID)

Hosts:
  moon-ds init [dir]              write a server.properties with every option
  moon-ds check                   validate settings, game folder and BepInEx
  moon-ds serve                   run the server (type help / save / backup / stop in the console)
  moon-ds status                  show the running server's status
  moon-ds send <command>          queue a console command for a running server
  moon-ds import <game folder>    copy an installed game (with BepInEx) into MOON_GAME_DIR
  moon-ds prepare                 install the mod and Steamworks files into the game folder
  moon-ds backup                  zip the current world into the backup folder
  moon-ds restore <zip>           restore a backup (server must be stopped)

Settings come from server.properties, MOON_* environment variables and --options
(e.g. --server-password, --game-port). Add --config <file> to pick the file.
Testing: --screenshot <png>, --self-test <json>, serve --fake (stand-in game).
";

fn main() {
    let mut args: Vec<String> = std::env::args().skip(1).collect();
    let host_entry = std::env::current_exe()
        .ok()
        .and_then(|p| {
            p.file_stem()
                .map(|n| n.to_string_lossy().eq_ignore_ascii_case("moon-server"))
        })
        .unwrap_or(false);
    if host_entry && args.is_empty() {
        match setup_host() {
            Ok(config) => {
                args = vec![
                    "serve".into(),
                    "--config".into(),
                    config.to_string_lossy().into_owned(),
                ]
            }
            Err(e) => {
                eprintln!("[moon-ds] {e}\nPress Enter to close.");
                let _ = std::io::stdin().read_line(&mut String::new());
                std::process::exit(1);
            }
        }
    } else if host_entry
        && args.first().is_some_and(|a| {
            a.starts_with("--") && a != "--help" && a != "--self-test" && a != "--screenshot"
        })
    {
        args.insert(0, "serve".into());
    }

    if let Some(i) = args.iter().position(|a| a == "--screenshot") {
        let path = args
            .get(i + 1)
            .cloned()
            .unwrap_or_else(|| "screenshot.png".into());
        macroquad::Window::from_config(gui::conf(), gui::run(gui::Mode::Screenshot(path)));
        return;
    }
    if let Some(i) = args.iter().position(|a| a == "--self-test") {
        let path = args
            .get(i + 1)
            .cloned()
            .unwrap_or_else(|| "self-test.json".into());
        self_test(path);
        return;
    }
    let cmd = args.first().map(|s| s.as_str()).unwrap_or("gui");
    let rest: Vec<String> = args.iter().skip(1).cloned().collect();
    let code = match cmd {
        "gui" => {
            macroquad::Window::from_config(gui::conf(), gui::run(gui::Mode::Normal));
            0
        }
        "serve" => serve(&rest),
        "init" => init(&rest),
        "check" => check(&rest),
        "status" => status(&rest),
        "send" => send(&rest),
        "backup" => backup_now(&rest),
        "restore" => restore(&rest),
        "import" => import_game(&rest),
        "prepare" => prepare(&rest),
        "query" => query(&rest),
        "fake-game" => fake_game(&rest),
        "help" | "--help" | "-h" => {
            print!("{HELP}");
            0
        }
        other => {
            eprintln!("unknown command '{other}'\n\n{HELP}");
            2
        }
    };
    std::process::exit(code);
}

fn setup_host() -> Result<PathBuf, String> {
    let folder = std::env::current_exe()
        .map_err(|e| e.to_string())?
        .parent()
        .ok_or("invalid executable folder")?
        .join("server");
    let properties = folder.join("server.properties");
    if properties.exists() {
        return Ok(properties);
    }
    println!("Moon Sweepers local server setup.\nThe server gets its own game copy and world; your client saves stay separate.");
    let source = if let Some(path) = client::detect_game_dir() {
        path
    } else {
        println!("Enter your Moon Sweepers game folder (contains MoonSweepers.exe):");
        let mut input = String::new();
        std::io::stdin()
            .read_line(&mut input)
            .map_err(|e| e.to_string())?;
        PathBuf::from(input.trim().trim_matches('"'))
    };
    config::check_game_dir(&source)?;
    if !source.join("BepInEx/core").is_dir() {
        return Err("Install BepInEx and the Moon Sweepers patcher in your client first (see docs/INSTALL.md)".into());
    }
    let game = folder.join("game");
    let mut stats = import::Stats {
        copied: 0,
        skipped: 0,
        bytes: 0,
    };
    println!("Copying game files for the server. First run can take a moment...");
    import::copy_tree(&source, &game, &mut stats, &|s| println!("{s}"))
        .map_err(|e| e.to_string())?;
    std::fs::create_dir_all(&folder).map_err(|e| e.to_string())?;
    let text = format!("MOON_GAME_DIR={}\nMOON_DATA_DIR={}\nMOON_SERVER_NAME=Local Moon Sweepers Server\nMOON_GAME_PORT=27025\nMOON_QUERY_PORT=27026\nMOON_SLOT=1\nMOON_SERVER_PASSWORD=\nMOON_BACKUP_ON_STOP=true\nMOON_AUTOSAVE_MINUTES=5\n",game.display(),folder.join("data").display());
    use std::io::Write;
    let mut file = std::fs::File::create_new(&properties).map_err(|e| e.to_string())?;
    file.write_all(text.as_bytes())
        .and_then(|_| file.sync_all())
        .map_err(|e| e.to_string())?;
    println!("Setup complete. Local client address: 127.0.0.1:27026\nType stop to save and shut down. Settings: {}",properties.display());
    Ok(properties)
}

fn load(rest: &[String]) -> Result<config::Settings, i32> {
    config::load(rest).map_err(|e| {
        eprintln!("[moon-ds] settings error: {e}");
        1
    })
}

fn serve(rest: &[String]) -> i32 {
    let fake = rest.iter().any(|a| a == "--fake");
    let rest: Vec<String> = rest.iter().filter(|a| *a != "--fake").cloned().collect();
    let s = match load(&rest) {
        Ok(s) => s,
        Err(c) => return c,
    };
    println!(
        "[moon-ds] Moon Sweepers dedicated server manager {}",
        env!("CARGO_PKG_VERSION")
    );
    for l in s.summary() {
        println!("[moon-ds]   {l}");
    }
    if cfg!(not(windows)) && !fake {
        println!(
            "[moon-ds]   runs the Windows game through '{}' (prefix {})",
            s.wine_bin,
            s.wine_prefix.display()
        );
    }
    let stop = Arc::new(AtomicBool::new(false));
    let st = stop.clone();
    let _ = ctrlc::set_handler(move || {
        st.store(true, Ordering::SeqCst);
    });
    let launch = if fake {
        supervisor::Launch::Fake(std::env::current_exe().unwrap())
    } else {
        supervisor::Launch::Game
    };
    let sup = supervisor::Supervisor {
        s,
        launch,
        sink: supervisor::stdout_sink(),
    };
    sup.run(supervisor::stdin_channel(), stop)
}

fn init(rest: &[String]) -> i32 {
    let dir = PathBuf::from(rest.first().map(|s| s.as_str()).unwrap_or("."));
    let _ = std::fs::create_dir_all(&dir);
    let p = dir.join("server.properties");
    if p.exists() {
        println!("[moon-ds] {} already exists; leaving it alone", p.display());
        return 0;
    }
    match std::fs::write(&p, config::PROPERTIES_TEMPLATE) {
        Ok(()) => {
            println!("[moon-ds] wrote {}", p.display());
            0
        }
        Err(e) => {
            eprintln!("[moon-ds] cannot write {}: {e}", p.display());
            1
        }
    }
}

fn check(rest: &[String]) -> i32 {
    let s = match load(rest) {
        Ok(s) => s,
        Err(c) => return c,
    };
    for l in s.summary() {
        println!("{l}");
    }
    let mut bad = false;
    match config::check_game_dir(&s.game_dir) {
        Ok(()) => println!("game folder     : OK"),
        Err(e) => {
            println!("game folder     : PROBLEM - {e}");
            bad = true;
        }
    }
    let bep = s.game_dir.join("BepInEx").join("core").is_dir();
    println!(
        "BepInEx         : {}",
        if bep {
            "installed"
        } else {
            "NOT installed (see docs/INSTALL.md)"
        }
    );
    bad |= !bep;
    let mod_ok = client::mod_installed(&s.game_dir);
    println!(
        "MoonDedicated   : {}",
        if mod_ok {
            "installed"
        } else {
            "not installed yet (serve copies mod/MoonDedicated.dll for you)"
        }
    );
    if s.password.is_empty() {
        println!("note            : no password - anyone who finds the server can join");
    }
    if bad {
        1
    } else {
        0
    }
}

fn status(rest: &[String]) -> i32 {
    let s = match load(rest) {
        Ok(s) => s,
        Err(c) => return c,
    };
    match state::read_state(&s.runtime_dir()) {
        Some(st) => {
            println!("{}", serde_json::to_string_pretty(&st).unwrap_or_default());
            0
        }
        None => {
            println!(
                "no status file in {} (is the server running?)",
                s.runtime_dir().display()
            );
            1
        }
    }
}

fn send(rest: &[String]) -> i32 {
    let (cmd_args, cfg_args): (Vec<String>, Vec<String>) = {
        let mut c = Vec::new();
        let mut o = Vec::new();
        let mut i = 0;
        while i < rest.len() {
            if rest[i].starts_with("--") {
                o.push(rest[i].clone());
                if !rest[i].contains('=') && i + 1 < rest.len() {
                    i += 1;
                    o.push(rest[i].clone());
                }
            } else {
                c.push(rest[i].clone());
            }
            i += 1;
        }
        (c, o)
    };
    let s = match load(&cfg_args) {
        Ok(s) => s,
        Err(c) => return c,
    };
    if cmd_args.is_empty() {
        eprintln!("usage: moon-ds send <save|say ...|kick <steamid>|stop>");
        return 2;
    }
    if !state::running(&s.runtime_dir()) {
        eprintln!("server is not running");
        return 1;
    }
    match state::send_admin_command(&s.runtime_dir(), &cmd_args.join(" ")) {
        Ok(()) => 0,
        Err(e) => {
            eprintln!("cannot queue the command: {e}");
            1
        }
    }
}

fn backup_now(rest: &[String]) -> i32 {
    let s = match load(rest) {
        Ok(s) => s,
        Err(c) => return c,
    };
    let _lock = match state::lock_runtime(&s.runtime_dir()) {
        Ok(lock) => lock,
        Err(_) => {
            return match state::send_admin_command(&s.runtime_dir(), "backup") {
                Ok(()) => {
                    println!("[moon-ds] backup queued: the manager will save and snapshot first");
                    0
                }
                Err(e) => {
                    eprintln!("cannot queue backup: {e}");
                    1
                }
            }
        }
    };
    let Some(dir) = supervisor::guess_save_dir(&s) else {
        eprintln!("the save folder is unknown");
        return 1;
    };
    let _world_lock = match state::lock_world(&dir) {
        Ok(lock) => lock,
        Err(e) => {
            eprintln!("{e}");
            return 1;
        }
    };
    match backup::create(&dir, &s.backup_dir, s.save_slot, s.backup_keep) {
        Ok(p) => {
            println!("[moon-ds] BACKUP {}", p.display());
            0
        }
        Err(e) => {
            eprintln!("[moon-ds] backup failed: {e}");
            1
        }
    }
}

fn restore(rest: &[String]) -> i32 {
    let (zip, cfg) = match config::positional_options(rest) {
        Ok(args) => args,
        Err(e) => {
            eprintln!("{e}");
            return 2;
        }
    };
    let s = match load(&cfg) {
        Ok(s) => s,
        Err(c) => return c,
    };
    let _lock = match state::lock_runtime(&s.runtime_dir()) {
        Ok(lock) => lock,
        Err(e) => {
            eprintln!("{e}");
            return 1;
        }
    };
    let Some(dir) = supervisor::guess_save_dir(&s) else {
        eprintln!("the save folder is unknown");
        return 1;
    };
    let _world_lock = match state::lock_world(&dir) {
        Ok(lock) => lock,
        Err(e) => {
            eprintln!("{e}");
            return 1;
        }
    };
    match backup::restore(&PathBuf::from(zip), &dir) {
        Ok(()) => {
            println!("[moon-ds] restored into {} (the old world was retained in a unique .before-restore directory)", dir.display());
            0
        }
        Err(e) => {
            eprintln!("[moon-ds] restore failed: {e}");
            1
        }
    }
}

fn query(rest: &[String]) -> i32 {
    let Some(addr) = rest.first() else {
        eprintln!("usage: moon-ds query <host[:queryport]>");
        return 2;
    };
    match a2s::query(
        addr,
        client::DEFAULT_QUERY_PORT,
        Duration::from_secs(3),
        true,
    ) {
        Ok(q) => {
            println!("name       : {}", q.info.name);
            println!("players    : {}/{}", q.info.players, q.info.max_players);
            println!(
                "password   : {}",
                if q.info.password { "yes" } else { "no" }
            );
            println!("game       : {}", q.info.version);
            println!(
                "steam id   : {}",
                q.info
                    .steam_id
                    .map(|i| i.to_string())
                    .unwrap_or_else(|| "not reported".into())
            );
            println!("ping       : {} ms", q.info.ping_ms);
            let mods = q.required_mods();
            println!(
                "required mods: {}",
                if mods.is_empty() {
                    "none".to_string()
                } else {
                    mods.iter()
                        .map(|(g, v)| v.as_ref().map(|v| format!("{g}@{v}")).unwrap_or(g.clone()))
                        .collect::<Vec<_>>()
                        .join(", ")
                }
            );
            0
        }
        Err(e) => {
            eprintln!("no answer from {addr}: {e}");
            1
        }
    }
}

fn import_game(rest: &[String]) -> i32 {
    let (from, cfg) = match config::positional_options(rest) {
        Ok(args) => args,
        Err(e) => {
            eprintln!("{e}");
            return 2;
        }
    };
    let s = match load(&cfg) {
        Ok(s) => s,
        Err(c) => return c,
    };
    let from = PathBuf::from(from);
    if config::check_game_dir(&from).is_err() {
        eprintln!("[moon-ds] '{}' does not look like a Moon Sweepers folder (MoonSweepers.exe, GameAssembly.dll)", from.display());
        return 1;
    }
    let mut st = import::Stats {
        copied: 0,
        skipped: 0,
        bytes: 0,
    };
    println!(
        "[moon-ds] copying the game from {} to {} ...",
        from.display(),
        s.game_dir.display()
    );
    match import::copy_tree(&from, &s.game_dir, &mut st, &|m| println!("[moon-ds] {m}")) {
        Ok(()) => {
            println!(
                "[moon-ds] done: {} files copied ({} MB), {} already up to date",
                st.copied,
                st.bytes / 1_000_000,
                st.skipped
            );
            0
        }
        Err(e) => {
            eprintln!("[moon-ds] copy failed: {e}");
            1
        }
    }
}

fn prepare(rest: &[String]) -> i32 {
    let s = match load(rest) {
        Ok(s) => s,
        Err(c) => return c,
    };
    if let Err(e) = config::check_game_dir(&s.game_dir) {
        eprintln!("[moon-ds] {e}");
        return 1;
    }
    let say = |m: &str| println!("[moon-ds] {m}");
    if let Err(e) = supervisor::install_payload(&s, &say) {
        eprintln!("[moon-ds] {e}");
        return 1;
    }
    supervisor::install_redist(&s, &say);
    println!("[moon-ds] game folder is ready");
    0
}

fn fake_game(rest: &[String]) -> i32 {
    let mut rt = PathBuf::from("runtime");
    let mut slot = 1u8;
    let mut i = 0;
    while i < rest.len() {
        match rest[i].as_str() {
            "--runtime" if i + 1 < rest.len() => {
                i += 1;
                rt = PathBuf::from(&rest[i]);
            }
            "--slot" if i + 1 < rest.len() => {
                i += 1;
                slot = rest[i].parse().unwrap_or(1);
            }
            _ => {}
        }
        i += 1;
    }
    fakegame::run(&rt, slot)
}

fn self_test(path: String) {
    let mut steps = selftest::run_server_steps();
    let (tx, rx) = std::sync::mpsc::channel::<Vec<String>>();
    // Opening the window ends this process once the frames are drawn.
    let p2 = path.clone();
    let steps_arc = Arc::new(std::sync::Mutex::new(std::mem::take(&mut steps)));
    let s2 = steps_arc.clone();
    std::thread::spawn(move || {
        if let Ok(seen) = rx.recv() {
            let mut v = s2.lock().unwrap();
            v.push(selftest::hud_step(&seen));
            selftest::write(&p2, &v);
            let ok = v.iter().all(|s| s.passed);
            std::process::exit(if ok { 0 } else { 1 });
        }
    });
    macroquad::Window::from_config(gui::conf(), gui::run(gui::Mode::SelfTest(tx)));
    // The window closed without reporting: record what we have.
    let mut v = steps_arc.lock().unwrap();
    v.push(selftest::Step {
        step: "hud",
        passed: false,
        detail: "the window closed before it was drawn".into(),
    });
    selftest::write(&path, &v);
    std::process::exit(1);
}
