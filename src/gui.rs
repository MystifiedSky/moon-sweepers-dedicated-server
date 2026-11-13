//! The player's server browser: a saved list with favourites and names,
//! live details from each server (players, ping, password, required mods) and a
//! Join button that hands the request to the game mod.

use crate::a2s::{Query, ServerInfo};
use crate::client::{self, Entry, ModStatus, Store};
use macroquad::prelude::*;
use std::collections::{HashMap, HashSet};
use std::path::PathBuf;
use std::time::{Duration, Instant};

const W: i32 = 1100;
const H: i32 = 680;

const BG: Color = Color::new(0.06, 0.07, 0.10, 1.0);
const PANEL: Color = Color::new(0.10, 0.12, 0.17, 1.0);
const PANEL2: Color = Color::new(0.14, 0.17, 0.23, 1.0);
const ACCENT: Color = Color::new(0.30, 0.60, 0.98, 1.0);
const TEXT: Color = Color::new(0.90, 0.92, 0.96, 1.0);
const DIM: Color = Color::new(0.58, 0.63, 0.72, 1.0);
const GOLD: Color = Color::new(1.0, 0.80, 0.25, 1.0);
const GOOD: Color = Color::new(0.35, 0.85, 0.50, 1.0);
const BAD: Color = Color::new(1.0, 0.40, 0.40, 1.0);
const WARN: Color = Color::new(1.0, 0.72, 0.25, 1.0);

pub enum Mode {
    Normal,
    Screenshot(String),
    /// Draw a few frames, report which parts of the screen were drawn, quit.
    SelfTest(std::sync::mpsc::Sender<Vec<String>>),
}

#[derive(Clone)]
enum QState {
    Pending,
    Done(Query),
    Failed(String),
}

enum Modal {
    None,
    Edit {
        index: Option<usize>,
        name: String,
        addr: String,
        field: u8,
        error: String,
    },
    Password {
        index: usize,
        text: String,
    },
    Folder {
        text: String,
    },
    Notice {
        title: String,
        lines: Vec<(String, Color)>,
    },
}

struct Ui {
    mouse: (f32, f32),
    click: bool,
    seen: HashSet<String>,
}

impl Ui {
    fn mark(&mut self, what: &str) {
        self.seen.insert(what.to_string());
    }
    fn text(&mut self, id: &str, s: &str, x: f32, y: f32, size: f32, c: Color) {
        self.mark(id);
        draw_text(s, x, y, size, c);
    }
    fn over(&self, x: f32, y: f32, w: f32, h: f32) -> bool {
        self.mouse.0 >= x && self.mouse.0 <= x + w && self.mouse.1 >= y && self.mouse.1 <= y + h
    }
    #[allow(clippy::too_many_arguments)] // Drawing coordinates and widget state form the immediate-mode UI API.
    fn button(
        &mut self,
        id: &str,
        label: &str,
        x: f32,
        y: f32,
        w: f32,
        h: f32,
        enabled: bool,
        primary: bool,
    ) -> bool {
        self.mark(id);
        let hot = enabled && self.over(x, y, w, h);
        let base = if !enabled {
            Color::new(0.16, 0.18, 0.23, 1.0)
        } else if primary {
            ACCENT
        } else {
            PANEL2
        };
        let col = if hot {
            Color::new(
                (base.r + 0.1).min(1.0),
                (base.g + 0.1).min(1.0),
                (base.b + 0.1).min(1.0),
                1.0,
            )
        } else {
            base
        };
        draw_rectangle(x, y, w, h, col);
        let tc = if !enabled {
            Color::new(0.45, 0.5, 0.58, 1.0)
        } else if primary {
            Color::new(0.02, 0.05, 0.10, 1.0)
        } else {
            TEXT
        };
        let d = measure_text(label, None, 20, 1.0);
        draw_text(
            label,
            x + (w - d.width) / 2.0,
            y + h / 2.0 + d.height / 2.0 - 1.0,
            20.0,
            tc,
        );
        hot && self.click
    }
    #[allow(clippy::too_many_arguments)]
    fn field(
        &mut self,
        id: &str,
        x: f32,
        y: f32,
        w: f32,
        text: &str,
        focused: bool,
        mask: bool,
        placeholder: &str,
    ) {
        self.mark(id);
        draw_rectangle(x, y, w, 34.0, Color::new(0.05, 0.06, 0.09, 1.0));
        draw_rectangle_lines(
            x,
            y,
            w,
            34.0,
            2.0,
            if focused {
                ACCENT
            } else {
                Color::new(0.25, 0.29, 0.37, 1.0)
            },
        );
        let shown: String = if mask {
            "*".repeat(text.chars().count())
        } else {
            text.to_string()
        };
        if shown.is_empty() {
            draw_text(
                placeholder,
                x + 10.0,
                y + 23.0,
                20.0,
                Color::new(0.4, 0.45, 0.52, 1.0),
            );
        } else {
            draw_text(&shown, x + 10.0, y + 23.0, 22.0, TEXT);
        }
        if focused && (get_time() * 2.0) as i64 % 2 == 0 {
            let d = measure_text(&shown, None, 22, 1.0);
            draw_rectangle(x + 11.0 + d.width, y + 7.0, 2.0, 20.0, TEXT);
        }
    }
}

fn fit(s: &str, max_w: f32, size: u16) -> String {
    if measure_text(s, None, size, 1.0).width <= max_w {
        return s.to_string();
    }
    let mut out: String = s.chars().collect();
    while !out.is_empty() && measure_text(format!("{out}..."), None, size, 1.0).width > max_w {
        out.pop();
    }
    format!("{out}...")
}

struct App {
    store: Store,
    results: HashMap<String, QState>,
    jobs: crate::query_jobs::Jobs,
    selected: Option<usize>,
    modal: Modal,
    scroll: usize,
    status: String,
    status_color: Color,
    next_refresh: Instant,
    demo: bool,
    last_click: (Instant, Option<usize>),
    disk: DiskCache,
}

/// Things read from the game folder. Refreshed every couple of seconds instead of every frame.
struct DiskCache {
    at: Option<Instant>,
    installed: Option<Vec<client::InstalledMod>>,
    mod_ok: bool,
    last_join: Option<client::JoinResult>,
}

fn demo_results() -> (Vec<Entry>, HashMap<String, QState>) {
    let entries = vec![
        Entry {
            name: "Crater Crew".into(),
            address: "crater.example.net:27026".into(),
            favorite: true,
        },
        Entry {
            name: "Weekend Sweepers".into(),
            address: "203.0.113.7:27026".into(),
            favorite: false,
        },
        Entry {
            name: "Modded Moon".into(),
            address: "mods.example.org:27026".into(),
            favorite: false,
        },
        Entry {
            name: "Offline example".into(),
            address: "198.51.100.9:27026".into(),
            favorite: false,
        },
    ];
    let mk = |name: &str, players: u8, max: u8, pw: bool, ping: u32, mods: &str| {
        let mut rules = vec![
            ("moon_ds".to_string(), "1".to_string()),
            (
                "moon_motd".to_string(),
                "Welcome aboard, crew! Keep the depot tidy.".to_string(),
            ),
        ];
        if !mods.is_empty() {
            rules.push(("moon_mods".to_string(), mods.to_string()));
        }
        QState::Done(Query {
            info: ServerInfo {
                name: name.into(),
                map: "slot 1".into(),
                game: "Moon Sweepers".into(),
                players,
                max_players: max,
                password: pw,
                version: "1.0.5".into(),
                steam_id: Some(90271234567890123),
                keywords: String::new(),
                ping_ms: ping,
            },
            rules,
        })
    };
    let mut r = HashMap::new();
    r.insert(
        entries[0].address.clone(),
        mk("Crater Crew", 3, 8, true, 41, ""),
    );
    r.insert(
        entries[1].address.clone(),
        mk("Weekend Sweepers", 1, 4, false, 87, ""),
    );
    r.insert(
        entries[2].address.clone(),
        mk(
            "Modded Moon",
            5,
            10,
            false,
            63,
            "author.bigger.bags@1.2.0,author.more.vehicles",
        ),
    );
    r.insert(
        entries[3].address.clone(),
        QState::Failed("no reply (is the query UDP port open?)".into()),
    );
    (entries, r)
}

impl App {
    fn new(demo_allowed: bool) -> App {
        let mut store = Store::load();
        let mut results = HashMap::new();
        let mut demo = false;
        if demo_allowed && store.servers.is_empty() {
            let (e, r) = demo_results();
            store.servers = e;
            results = r;
            demo = true;
            if store.game_dir.is_empty() {
                store.game_dir =
                    "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Moon Sweepers".into();
            }
        }
        store.sort();
        let selected = if demo {
            store
                .servers
                .iter()
                .position(|e| e.address.starts_with("mods."))
        } else if store.servers.is_empty() {
            None
        } else {
            Some(0)
        };
        let mut app = App {
            store,
            results,
            jobs: crate::query_jobs::Jobs::new(),
            selected,
            modal: Modal::None,
            scroll: 0,
            status: if demo {
                "Showing example servers. Add your own with Add Server.".into()
            } else {
                String::new()
            },
            status_color: DIM,
            next_refresh: Instant::now() + Duration::from_secs(30),
            demo,
            last_click: (Instant::now(), None),
            disk: DiskCache {
                at: None,
                installed: None,
                mod_ok: false,
                last_join: None,
            },
        };
        if !demo {
            app.refresh_all();
            if let Some(error) = app.store.load_error.clone() {
                app.modal = Modal::Notice {
                    title: "Saved servers could not be loaded".into(),
                    lines: vec![(error, BAD)],
                };
            }
        }
        app
    }

    fn persist(&mut self) {
        if let Err(e) = self.store.save() {
            self.set_status(format!("Could not save servers: {e}"), BAD);
            self.modal = Modal::Notice {
                title: "Saved servers need attention".into(),
                lines: vec![(e, BAD)],
            };
        }
    }

    fn refresh_all(&mut self) {
        let addrs: Vec<String> = self
            .store
            .servers
            .iter()
            .map(|e| e.address.clone())
            .collect();
        for a in addrs {
            self.refresh_one(&a);
        }
        self.next_refresh = Instant::now() + Duration::from_secs(30);
    }

    fn refresh_one(&mut self, addr: &str) {
        if self.demo {
            return;
        }
        self.results.insert(addr.to_string(), QState::Pending);
        if let Err(e) = self.jobs.enqueue(addr) {
            self.results.insert(addr.into(), QState::Failed(e));
        }
    }

    fn pump(&mut self) {
        for (a, r) in self.jobs.pump() {
            if !self.store.servers.iter().any(|e| e.address == a) {
                continue;
            }
            self.results.insert(
                a,
                match r {
                    Ok(q) => QState::Done(q),
                    Err(e) => QState::Failed(e),
                },
            );
        }
        if !self.demo && self.next_refresh <= Instant::now() && matches!(self.modal, Modal::None) {
            self.refresh_all();
        }
        if self
            .disk
            .at
            .map(|t| t.elapsed() > Duration::from_secs(2))
            .unwrap_or(true)
        {
            let gd = self.game_dir();
            self.disk = DiskCache {
                at: Some(Instant::now()),
                installed: client::installed_mods(&gd),
                mod_ok: client::mod_installed(&gd),
                last_join: client::read_join_result(&gd),
            };
        }
    }

    fn game_dir(&self) -> PathBuf {
        PathBuf::from(&self.store.game_dir)
    }

    fn set_status(&mut self, s: impl Into<String>, c: Color) {
        self.status = s.into();
        self.status_color = c;
    }

    fn mods_for(&self, q: &Query) -> Vec<(String, Option<String>, ModStatus)> {
        let installed = if self.demo {
            Some(vec![client::InstalledMod {
                guid: "author.bigger.bags".into(),
                version: "1.1.0".into(),
            }])
        } else {
            self.disk.installed.clone()
        };
        client::check_mods(&q.required_mods(), installed.as_deref())
    }

    fn try_join(&mut self, idx: usize) {
        let Some(entry) = self.store.servers.get(idx).cloned() else {
            return;
        };
        let Some(QState::Done(q)) = self.results.get(&entry.address).cloned() else {
            self.set_status(
                "That server did not answer, so it cannot be joined yet. Try Refresh.",
                BAD,
            );
            return;
        };
        if q.info.steam_id.is_none() {
            self.set_status("That server did not give a Steam ID. It is probably not a Moon Sweepers dedicated server.", BAD);
            return;
        }
        let issues: Vec<_> = self
            .mods_for(&q)
            .into_iter()
            .filter(|(_, _, s)| matches!(s, ModStatus::Missing | ModStatus::WrongVersion { .. }))
            .collect();
        if !issues.is_empty() {
            let mut lines = vec![(
                "This server needs mods you do not have, so it would refuse you:".to_string(),
                TEXT,
            )];
            for (g, v, s) in &issues {
                let want = v
                    .clone()
                    .map(|v| format!(" (version {v})"))
                    .unwrap_or_default();
                match s {
                    ModStatus::WrongVersion { have } => {
                        lines.push((format!("* {g}{want} - you have version {have}"), WARN))
                    }
                    _ => lines.push((format!("* {g}{want} - not installed"), BAD)),
                }
            }
            lines.push(("Install these in the game's BepInEx plugins folder, start the game once, then try again.".into(), DIM));
            self.modal = Modal::Notice {
                title: "Required mods".into(),
                lines,
            };
            return;
        }
        if q.info.password {
            self.modal = Modal::Password {
                index: idx,
                text: String::new(),
            };
            return;
        }
        self.start_join(idx, "");
    }

    fn start_join(&mut self, idx: usize, password: &str) {
        let Some(entry) = self.store.servers.get(idx).cloned() else {
            return;
        };
        let Some(QState::Done(q)) = self.results.get(&entry.address).cloned() else {
            return;
        };
        let gd = self.game_dir();
        if !gd.join("MoonSweepers.exe").exists() {
            self.modal = Modal::Folder {
                text: self.store.game_dir.clone(),
            };
            self.set_status("Tell me where Moon Sweepers is installed first.", WARN);
            return;
        }
        if !client::mod_installed(&gd) && !self.demo {
            self.modal = Modal::Notice {
                title: "Game mod not installed".into(),
                lines: vec![
                    ("The Moon Sweepers dedicated-server mod is not in this game folder.".into(), TEXT),
                    ("Copy MoonDedicated.dll into <game>/BepInEx/plugins/MoonDedicated/ (BepInEx must be installed).".into(), DIM),
                ],
            };
            return;
        }
        if self.demo {
            self.set_status(
                "Demo mode: nothing was sent. With real servers this starts the game and joins.",
                WARN,
            );
            return;
        }
        match client::write_connect(&gd, &entry.address, &q, password) {
            Ok(()) => match client::launch_game() {
                Ok(()) => self.set_status(
                    format!("Joining {} - the game is starting...", q.info.name),
                    GOOD,
                ),
                Err(e) => self.set_status(
                    format!("Join request saved, but {e}. Start the game from Steam yourself."),
                    WARN,
                ),
            },
            Err(e) => self.set_status(e, BAD),
        }
    }
}

fn pump_text(buf: &mut String, max: usize) {
    while let Some(c) = get_char_pressed() {
        if !c.is_control() && buf.chars().count() < max {
            buf.push(c);
        }
    }
    if is_key_pressed(KeyCode::Backspace) {
        buf.pop();
    }
}

#[cfg(windows)]
fn send_window_to_back(title: &str) {
    use windows_sys::Win32::UI::WindowsAndMessaging::*;
    let wide: Vec<u16> = title.encode_utf16().chain(std::iter::once(0)).collect();
    unsafe {
        let hwnd = FindWindowW(std::ptr::null(), wide.as_ptr());
        if !hwnd.is_null() {
            SetWindowPos(
                hwnd,
                HWND_BOTTOM,
                0,
                0,
                0,
                0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE,
            );
        }
    }
}
#[cfg(not(windows))]
fn send_window_to_back(_title: &str) {}

pub const TITLE: &str = "Moon Sweepers Server Browser";

pub fn conf() -> Conf {
    Conf {
        window_title: TITLE.into(),
        window_width: W,
        window_height: H,
        window_resizable: false,
        ..Default::default()
    }
}

pub async fn run(mode: Mode) {
    let background = !matches!(mode, Mode::Normal);
    let mut app = App::new(background);
    let mut frame = 0u32;
    let mut ui = Ui {
        mouse: (0.0, 0.0),
        click: false,
        seen: HashSet::new(),
    };
    loop {
        frame += 1;
        if background && frame <= 3 {
            send_window_to_back(TITLE);
        }
        ui.mouse = mouse_position();
        ui.click = is_mouse_button_pressed(MouseButton::Left);
        app.pump();
        draw(&mut app, &mut ui);

        match &mode {
            Mode::Screenshot(path) if frame >= 120 => {
                get_screen_data().export_png(path);
                return;
            }
            Mode::SelfTest(tx) if frame >= 40 => {
                let mut v: Vec<String> = ui.seen.iter().cloned().collect();
                v.sort();
                let _ = tx.send(v);
                return;
            }
            _ => {}
        }
        next_frame().await;
    }
}

fn draw(app: &mut App, ui: &mut Ui) {
    clear_background(BG);
    let w = W as f32;
    // header
    draw_rectangle(0.0, 0.0, w, 74.0, PANEL);
    draw_rectangle(0.0, 72.0, w, 2.0, ACCENT);
    ui.text("title", "Moon Sweepers", 24.0, 40.0, 36.0, TEXT);
    ui.text(
        "subtitle",
        "Server browser - join dedicated servers by address",
        24.0,
        62.0,
        20.0,
        DIM,
    );

    let modal_open = !matches!(app.modal, Modal::None);
    let click_ok = !modal_open;
    let saved_click = ui.click;
    if !click_ok {
        ui.click = false;
    }

    // toolbar
    let ty = 88.0;
    let mut x = 24.0;
    if ui.button("btn_add", "Add Server", x, ty, 130.0, 36.0, true, false) {
        app.modal = Modal::Edit {
            index: None,
            name: String::new(),
            addr: String::new(),
            field: 0,
            error: String::new(),
        };
    }
    x += 140.0;
    let has_sel = app
        .selected
        .map(|i| i < app.store.servers.len())
        .unwrap_or(false);
    if ui.button("btn_edit", "Edit", x, ty, 90.0, 36.0, has_sel, false) {
        if let Some(i) = app.selected {
            let e = app.store.servers[i].clone();
            app.modal = Modal::Edit {
                index: Some(i),
                name: e.name,
                addr: e.address,
                field: 0,
                error: String::new(),
            };
        }
    }
    x += 100.0;
    if ui.button("btn_delete", "Delete", x, ty, 90.0, 36.0, has_sel, false) {
        if let Some(i) = app.selected {
            let removed = app.store.servers.remove(i);
            app.results.remove(&removed.address);
            app.persist();
            app.selected = if app.store.servers.is_empty() {
                None
            } else {
                Some(i.min(app.store.servers.len() - 1))
            };
        }
    }
    x += 100.0;
    let fav_label = if has_sel && app.store.servers[app.selected.unwrap()].favorite {
        "★ Unfavourite"
    } else {
        "☆ Favourite"
    };
    let fav_label = fav_label.replace(['★', '☆'], "*");
    if ui.button(
        "btn_favourite",
        &fav_label,
        x,
        ty,
        140.0,
        36.0,
        has_sel,
        false,
    ) {
        if let Some(i) = app.selected {
            let key = app.store.servers[i].address.clone();
            app.store.servers[i].favorite = !app.store.servers[i].favorite;
            app.store.sort();
            app.selected = app.store.servers.iter().position(|e| e.address == key);
            app.persist();
        }
    }
    x += 150.0;
    if ui.button("btn_refresh", "Refresh", x, ty, 100.0, 36.0, true, false) {
        app.refresh_all();
        app.set_status("Asking servers...", DIM);
    }
    x += 110.0;
    if ui.button(
        "btn_settings",
        "Game folder",
        x,
        ty,
        130.0,
        36.0,
        true,
        false,
    ) {
        app.modal = Modal::Folder {
            text: app.store.game_dir.clone(),
        };
    }
    let join_ready = has_sel;
    if ui.button(
        "btn_join",
        "Join Server",
        w - 190.0,
        ty,
        166.0,
        36.0,
        join_ready,
        true,
    ) {
        if let Some(i) = app.selected {
            app.try_join(i);
        }
    }

    // list
    let ly = 138.0;
    draw_rectangle(24.0, ly, w - 48.0, 26.0, PANEL2);
    for (id, label, cx) in [
        ("col_fav", "Fav", 34.0),
        ("col_name", "Name", 80.0),
        ("col_addr", "Address", 430.0),
        ("col_players", "Players", 650.0),
        ("col_ping", "Ping", 740.0),
        ("col_flags", "Lock / Mods", 810.0),
        ("col_status", "Status", 940.0),
    ] {
        ui.text(id, label, cx, ly + 19.0, 20.0, DIM);
    }
    let row_h = 34.0;
    let rows_top = ly + 28.0;
    let visible = 8usize;
    let n = app.store.servers.len();
    if n == 0 {
        ui.text(
            "empty",
            "No servers yet. Click Add Server and enter an address like play.example.com:27026",
            40.0,
            rows_top + 40.0,
            22.0,
            DIM,
        );
    }
    let (_, wheel) = mouse_wheel();
    if wheel != 0.0 && !modal_open {
        if wheel < 0.0 && app.scroll + visible < n {
            app.scroll += 1;
        } else if wheel > 0.0 && app.scroll > 0 {
            app.scroll -= 1;
        }
    }
    for (vi, i) in (app.scroll..n.min(app.scroll + visible)).enumerate() {
        let y = rows_top + vi as f32 * row_h;
        let e = app.store.servers[i].clone();
        let hover = ui.over(24.0, y, w - 48.0, row_h - 2.0);
        let sel = app.selected == Some(i);
        draw_rectangle(
            24.0,
            y,
            w - 48.0,
            row_h - 2.0,
            if sel {
                Color::new(0.16, 0.26, 0.42, 1.0)
            } else if hover {
                PANEL2
            } else {
                PANEL
            },
        );
        if hover && ui.click {
            let dbl = app.last_click.1 == Some(i)
                && app.last_click.0.elapsed() < Duration::from_millis(450);
            app.selected = Some(i);
            app.last_click = (Instant::now(), Some(i));
            if dbl {
                app.try_join(i);
            }
        }
        ui.mark("row");
        draw_text(
            if e.favorite { "*" } else { "." },
            40.0,
            y + 25.0,
            30.0,
            if e.favorite {
                GOLD
            } else {
                Color::new(0.3, 0.34, 0.42, 1.0)
            },
        );
        let st = app
            .results
            .get(&e.address)
            .cloned()
            .unwrap_or(QState::Pending);
        match &st {
            QState::Done(q) => {
                draw_text(fit(&e.name, 340.0, 22), 80.0, y + 23.0, 22.0, TEXT);
                draw_text(fit(&e.address, 200.0, 20), 430.0, y + 23.0, 20.0, DIM);
                let pc = if q.info.players >= q.info.max_players {
                    WARN
                } else {
                    TEXT
                };
                draw_text(
                    format!("{}/{}", q.info.players, q.info.max_players),
                    650.0,
                    y + 23.0,
                    22.0,
                    pc,
                );
                let pc = if q.info.ping_ms < 80 {
                    GOOD
                } else if q.info.ping_ms < 160 {
                    WARN
                } else {
                    BAD
                };
                draw_text(format!("{} ms", q.info.ping_ms), 740.0, y + 23.0, 20.0, pc);
                if q.info.password {
                    draw_rectangle(810.0, y + 9.0, 16.0, 14.0, WARN);
                    draw_rectangle_lines(813.0, y + 3.0, 10.0, 10.0, 2.0, WARN);
                    ui.mark("icon_lock");
                }
                let mods = q.required_mods();
                if !mods.is_empty() {
                    let issues = app
                        .mods_for(q)
                        .iter()
                        .filter(|(_, _, s)| {
                            matches!(s, ModStatus::Missing | ModStatus::WrongVersion { .. })
                        })
                        .count();
                    draw_text(
                        format!(
                            "{} mod{}",
                            mods.len(),
                            if mods.len() == 1 { "" } else { "s" }
                        ),
                        836.0,
                        y + 23.0,
                        20.0,
                        if issues > 0 { BAD } else { ACCENT },
                    );
                    ui.mark("icon_mods");
                }
                if q.info.version != crate::config::SUPPORTED_GAME && !q.info.version.is_empty() {
                    draw_text(format!("v{}", q.info.version), 940.0, y + 23.0, 20.0, WARN);
                } else {
                    draw_text("Online", 940.0, y + 23.0, 20.0, GOOD);
                }
            }
            QState::Pending => {
                draw_text(fit(&e.name, 340.0, 22), 80.0, y + 23.0, 22.0, TEXT);
                draw_text(fit(&e.address, 200.0, 20), 430.0, y + 23.0, 20.0, DIM);
                draw_text("Pinging...", 940.0, y + 23.0, 20.0, DIM);
            }
            QState::Failed(_) => {
                draw_text(fit(&e.name, 340.0, 22), 80.0, y + 23.0, 22.0, DIM);
                draw_text(fit(&e.address, 200.0, 20), 430.0, y + 23.0, 20.0, DIM);
                draw_text("Offline", 940.0, y + 23.0, 20.0, BAD);
            }
        }
    }

    // details
    let dy = rows_top + visible as f32 * row_h + 8.0;
    let dh = H as f32 - dy - 40.0;
    draw_rectangle(24.0, dy, w - 48.0, dh, PANEL);
    if let Some(i) = app.selected.filter(|i| *i < n) {
        let e = app.store.servers[i].clone();
        match app
            .results
            .get(&e.address)
            .cloned()
            .unwrap_or(QState::Pending)
        {
            QState::Done(q) => {
                ui.text("detail_name", &q.info.name, 40.0, dy + 30.0, 28.0, TEXT);
                let sid = q
                    .info
                    .steam_id
                    .map(|s| format!("Steam ID {s}"))
                    .unwrap_or_else(|| "no Steam ID".into());
                ui.text(
                    "detail_meta",
                    &format!(
                        "{}   |   game {}   |   {}   |   {}",
                        e.address,
                        q.info.version,
                        sid,
                        if q.info.password {
                            "password required"
                        } else {
                            "no password"
                        }
                    ),
                    40.0,
                    dy + 54.0,
                    19.0,
                    DIM,
                );
                if let Some(m) = q.rule("moon_motd") {
                    if !m.is_empty() {
                        ui.text(
                            "detail_motd",
                            &fit(m, 1000.0, 20),
                            40.0,
                            dy + 80.0,
                            20.0,
                            TEXT,
                        );
                    }
                }
                let mods = app.mods_for(&q);
                if mods.is_empty() {
                    ui.text(
                        "detail_nomods",
                        "No mods required.",
                        40.0,
                        dy + 108.0,
                        20.0,
                        GOOD,
                    );
                } else {
                    ui.text(
                        "detail_mods_title",
                        "Required mods:",
                        40.0,
                        dy + 108.0,
                        20.0,
                        TEXT,
                    );
                    let mut mx = 190.0;
                    let mut my = dy + 108.0;
                    for (g, v, s) in &mods {
                        let (txt, col) = match s {
                            ModStatus::Ok => (format!("{g} ✓"), GOOD),
                            ModStatus::Missing => (format!("{g} - missing"), BAD),
                            ModStatus::WrongVersion { have } => {
                                (format!("{g} - you have {have}"), WARN)
                            }
                            ModStatus::Unknown => (format!("{g} - not verified yet"), WARN),
                        };
                        let txt = txt.replace('✓', "(ok)");
                        let txt = match v {
                            Some(v) => format!("{txt} [{v}]"),
                            None => txt,
                        };
                        let d = measure_text(&txt, None, 19, 1.0);
                        if mx + d.width > w - 60.0 {
                            mx = 190.0;
                            my += 24.0;
                        }
                        draw_text(&txt, mx, my, 19.0, col);
                        mx += d.width + 24.0;
                    }
                    ui.mark("detail_mods");
                    if mods.iter().any(|(_, _, s)| {
                        matches!(s, ModStatus::Missing | ModStatus::WrongVersion { .. })
                    }) {
                        draw_rectangle(
                            40.0,
                            dy + dh - 40.0,
                            w - 96.0,
                            30.0,
                            Color::new(0.35, 0.10, 0.10, 1.0),
                        );
                        ui.text("mods_notice", "You are missing mods this server requires. Install them before joining.", 52.0, dy + dh - 19.0, 20.0, Color::new(1.0, 0.85, 0.85, 1.0));
                    }
                }
            }
            QState::Failed(err) => {
                ui.text("detail_name", &e.name, 40.0, dy + 30.0, 28.0, TEXT);
                ui.text(
                    "detail_error",
                    &format!("Could not reach {}: {}", e.address, err),
                    40.0,
                    dy + 60.0,
                    20.0,
                    BAD,
                );
            }
            QState::Pending => ui.text(
                "detail_pending",
                "Asking the server...",
                40.0,
                dy + 30.0,
                24.0,
                DIM,
            ),
        }
    }

    // footer
    let fy = H as f32 - 30.0;
    draw_rectangle(0.0, fy - 6.0, w, 40.0, PANEL);
    let (mod_txt, mod_col) = if app.demo {
        ("example data".to_string(), WARN)
    } else if app.disk.mod_ok {
        ("game mod installed".to_string(), GOOD)
    } else {
        ("game mod NOT found".to_string(), BAD)
    };
    ui.text("footer_mod", &mod_txt, 24.0, fy + 16.0, 19.0, mod_col);
    if !app.status.is_empty() {
        let s = fit(&app.status, 760.0, 19);
        let sc = app.status_color;
        ui.text("footer_status", &s, 240.0, fy + 16.0, 19.0, sc);
    }
    if let Some(r) = app.disk.last_join.clone().filter(|_| !app.demo) {
        let c = if r.ok { GOOD } else { BAD };
        let s = fit(&format!("Last join: {}", r.message), 400.0, 19);
        draw_text(&s, w - 420.0, fy + 16.0, 19.0, c);
    }

    ui.click = saved_click;
    draw_modal(app, ui);
}

fn draw_modal(app: &mut App, ui: &mut Ui) {
    if matches!(app.modal, Modal::None) {
        return;
    }
    let w = W as f32;
    draw_rectangle(0.0, 0.0, w, H as f32, Color::new(0.0, 0.0, 0.0, 0.62));
    let (mw, mh) = (620.0, 300.0);
    let (mx, my) = ((w - mw) / 2.0, (H as f32 - mh) / 2.0 - 20.0);
    draw_rectangle(mx, my, mw, mh, PANEL);
    draw_rectangle_lines(mx, my, mw, mh, 2.0, ACCENT);
    let modal = std::mem::replace(&mut app.modal, Modal::None);
    match modal {
        Modal::None => {}
        Modal::Edit {
            index,
            mut name,
            mut addr,
            mut field,
            mut error,
        } => {
            ui.text(
                "modal_edit_title",
                if index.is_some() {
                    "Edit server"
                } else {
                    "Add server"
                },
                mx + 24.0,
                my + 40.0,
                30.0,
                TEXT,
            );
            ui.text(
                "lbl_name",
                "Name (your own label)",
                mx + 24.0,
                my + 78.0,
                19.0,
                DIM,
            );
            ui.field(
                "fld_name",
                mx + 24.0,
                my + 86.0,
                mw - 48.0,
                &name,
                field == 0,
                false,
                "Crater Crew",
            );
            ui.text(
                "lbl_addr",
                "Address (host or host:query-port)",
                mx + 24.0,
                my + 142.0,
                19.0,
                DIM,
            );
            ui.field(
                "fld_addr",
                mx + 24.0,
                my + 150.0,
                mw - 48.0,
                &addr,
                field == 1,
                false,
                "play.example.com:27026",
            );
            if ui.click {
                if ui.over(mx + 24.0, my + 86.0, mw - 48.0, 34.0) {
                    field = 0;
                } else if ui.over(mx + 24.0, my + 150.0, mw - 48.0, 34.0) {
                    field = 1;
                }
            }
            if field == 0 {
                pump_text(&mut name, 40)
            } else {
                pump_text(&mut addr, 80)
            }
            if is_key_pressed(KeyCode::Tab) {
                field = 1 - field;
            }
            if !error.is_empty() {
                ui.text("edit_error", &error, mx + 24.0, my + 208.0, 19.0, BAD);
            }
            let ok = ui.button(
                "btn_save",
                "Save",
                mx + mw - 270.0,
                my + mh - 56.0,
                120.0,
                38.0,
                true,
                true,
            ) || is_key_pressed(KeyCode::Enter);
            let cancel = ui.button(
                "btn_cancel",
                "Cancel",
                mx + mw - 140.0,
                my + mh - 56.0,
                116.0,
                38.0,
                true,
                false,
            ) || is_key_pressed(KeyCode::Escape);
            if cancel {
                return;
            }
            if ok {
                match client::clean_address(&addr) {
                    Err(e) => error = e,
                    Ok(a) => {
                        let label = if name.trim().is_empty() {
                            a.clone()
                        } else {
                            name.trim().to_string()
                        };
                        match index {
                            Some(i) if i < app.store.servers.len() => {
                                let old = app.store.servers[i].address.clone();
                                app.store.servers[i].name = label.clone();
                                app.store.servers[i].address = a.clone();
                                if old != a {
                                    app.results.remove(&old);
                                }
                            }
                            _ => {
                                if app.store.servers.iter().any(|e| e.address == a) {
                                    error = "That server is already in the list".into();
                                } else {
                                    app.store.servers.push(Entry {
                                        name: label.clone(),
                                        address: a.clone(),
                                        favorite: false,
                                    });
                                }
                            }
                        }
                        if error.is_empty() {
                            app.store.sort();
                            app.selected = app.store.servers.iter().position(|e| e.address == a);
                            app.persist();
                            app.refresh_one(&a);
                            return;
                        }
                    }
                }
            }
            app.modal = Modal::Edit {
                index,
                name,
                addr,
                field,
                error,
            };
        }
        Modal::Password { index, mut text } => {
            ui.text(
                "modal_pw_title",
                "This server needs a password",
                mx + 24.0,
                my + 40.0,
                30.0,
                TEXT,
            );
            ui.text(
                "lbl_pw",
                "Password (not saved)",
                mx + 24.0,
                my + 90.0,
                19.0,
                DIM,
            );
            ui.field(
                "fld_pw",
                mx + 24.0,
                my + 98.0,
                mw - 48.0,
                &text,
                true,
                true,
                "",
            );
            pump_text(&mut text, 128);
            let ok = ui.button(
                "btn_pw_join",
                "Join",
                mx + mw - 270.0,
                my + mh - 56.0,
                120.0,
                38.0,
                !text.is_empty(),
                true,
            ) || (is_key_pressed(KeyCode::Enter) && !text.is_empty());
            let cancel = ui.button(
                "btn_pw_cancel",
                "Cancel",
                mx + mw - 140.0,
                my + mh - 56.0,
                116.0,
                38.0,
                true,
                false,
            ) || is_key_pressed(KeyCode::Escape);
            if cancel {
                return;
            }
            if ok {
                app.start_join(index, &text);
                return;
            }
            app.modal = Modal::Password { index, text };
        }
        Modal::Folder { mut text } => {
            ui.text(
                "modal_folder_title",
                "Where is Moon Sweepers installed?",
                mx + 24.0,
                my + 40.0,
                30.0,
                TEXT,
            );
            ui.text(
                "lbl_folder",
                "Game folder (contains MoonSweepers.exe)",
                mx + 24.0,
                my + 90.0,
                19.0,
                DIM,
            );
            ui.field(
                "fld_folder",
                mx + 24.0,
                my + 98.0,
                mw - 48.0,
                &text,
                true,
                false,
                "C:\\...\\steamapps\\common\\Moon Sweepers",
            );
            pump_text(&mut text, 260);
            let good = std::path::Path::new(&text)
                .join("MoonSweepers.exe")
                .exists();
            ui.text(
                "folder_check",
                if good {
                    "Found the game here."
                } else {
                    "MoonSweepers.exe not found in this folder."
                },
                mx + 24.0,
                my + 160.0,
                19.0,
                if good { GOOD } else { WARN },
            );
            if ui.button(
                "btn_detect",
                "Auto-detect",
                mx + 24.0,
                my + mh - 56.0,
                140.0,
                38.0,
                true,
                false,
            ) {
                if let Some(d) = client::detect_game_dir() {
                    text = d.to_string_lossy().into_owned();
                }
            }
            let ok = ui.button(
                "btn_folder_save",
                "Save",
                mx + mw - 270.0,
                my + mh - 56.0,
                120.0,
                38.0,
                true,
                true,
            ) || is_key_pressed(KeyCode::Enter);
            let cancel = ui.button(
                "btn_folder_cancel",
                "Cancel",
                mx + mw - 140.0,
                my + mh - 56.0,
                116.0,
                38.0,
                true,
                false,
            ) || is_key_pressed(KeyCode::Escape);
            if cancel {
                return;
            }
            if ok {
                app.store.game_dir = text.trim().to_string();
                app.persist();
                app.disk.at = None;
                return;
            }
            app.modal = Modal::Folder { text };
        }
        Modal::Notice { title, lines } => {
            ui.text(
                "modal_notice_title",
                &title,
                mx + 24.0,
                my + 40.0,
                30.0,
                TEXT,
            );
            for (i, (l, c)) in lines.iter().enumerate() {
                ui.text(
                    "notice_line",
                    &fit(l, mw - 48.0, 20),
                    mx + 24.0,
                    my + 84.0 + i as f32 * 26.0,
                    20.0,
                    *c,
                );
            }
            let ok = ui.button(
                "btn_notice_ok",
                "OK",
                mx + mw - 140.0,
                my + mh - 56.0,
                116.0,
                38.0,
                true,
                true,
            ) || is_key_pressed(KeyCode::Enter)
                || is_key_pressed(KeyCode::Escape);
            if ok {
                return;
            }
            app.modal = Modal::Notice { title, lines };
        }
    }
}

/// Elements the self-test expects on the main screen.
pub const EXPECTED_HUD: &[&str] = &[
    "title",
    "btn_add",
    "btn_join",
    "btn_favourite",
    "col_name",
    "col_players",
    "row",
    "detail_name",
    "footer_mod",
    "icon_lock",
    "icon_mods",
    "mods_notice",
];
