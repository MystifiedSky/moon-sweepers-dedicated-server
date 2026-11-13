//! The small files the manager and the game mod use to talk to each other.
//!
//! * `<runtime>/state.json`     written by the mod about once a second (atomic rename)
//! * `<runtime>/commands/*.cmd` written by the manager, one command per file, consumed by the mod

use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq)]
pub struct Player {
    #[serde(default)]
    pub steam_id: String,
    #[serde(default)]
    pub name: String,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(default)]
pub struct HostState {
    pub schema: u32,
    pub heartbeat_unix_ms: u64,
    pub phase: String,
    pub ready: bool,
    pub save_pending: bool,
    pub save_failed: bool,
    pub last_save_unix_ms: u64,
    pub server_steam_id: String,
    pub players: Vec<Player>,
    pub save_dir: String,
    pub error: String,
    pub game_port: u16,
    pub query_port: u16,
    pub mods: Vec<String>,
    pub vehicles_saved: u32,
    pub instance_id: String,
    pub snapshot_dir: String,
    pub snapshot_unix_ms: u64,
    pub snapshot_error: String,
    pub snapshot_request_id: String,
}

/// The open file owns the OS lock. Never delete the lock file: replacing its
/// inode would let two processes hold different locks for the same server.
pub fn lock_runtime(runtime: &Path) -> Result<std::fs::File, String> {
    std::fs::create_dir_all(runtime).map_err(|e| e.to_string())?;
    let file = std::fs::OpenOptions::new()
        .create(true)
        .truncate(false)
        .read(true)
        .write(true)
        .open(runtime.join("server.lock"))
        .map_err(|e| e.to_string())?;
    file.try_lock()
        .map_err(|e| format!("server data is in use; stop the server first: {e}"))?;
    Ok(file)
}

pub fn running(runtime: &Path) -> bool {
    lock_runtime(runtime).is_err()
}

/// Keep the lock outside slot directories so restore cannot replace it.
/// Clients and all server data directories sharing MoonSessions use this lock.
pub fn lock_world(save: &Path) -> Result<std::fs::File, String> {
    let root = save.parent().ok_or("invalid world directory")?;
    std::fs::create_dir_all(root).map_err(|e| e.to_string())?;
    let file = std::fs::OpenOptions::new()
        .create(true)
        .truncate(false)
        .read(true)
        .write(true)
        .open(root.join(".moon-ds-world.lock"))
        .map_err(|e| format!("world storage is in use or unavailable: {e}"))?;
    file.try_lock()
        .map_err(|e| format!("world storage is in use; stop other clients/servers first: {e}"))?;
    Ok(file)
}

pub fn console_field(value: &str) -> String {
    value
        .chars()
        .flat_map(|c| {
            if c.is_control() || c == '\u{2028}' || c == '\u{2029}' {
                c.escape_default().collect::<Vec<_>>()
            } else {
                vec![c]
            }
        })
        .collect()
}

pub fn now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0)
}

pub fn state_path(runtime: &Path) -> PathBuf {
    runtime.join("state.json")
}

pub fn read_state(runtime: &Path) -> Option<HostState> {
    let text = std::fs::read_to_string(state_path(runtime)).ok()?;
    serde_json::from_str(&text).ok()
}

pub fn write_state_atomic(runtime: &Path, s: &HostState) -> std::io::Result<()> {
    std::fs::create_dir_all(runtime)?;
    let tmp = runtime.join("state.json.tmp");
    std::fs::write(&tmp, serde_json::to_vec_pretty(s).unwrap_or_default())?;
    std::fs::rename(&tmp, state_path(runtime))
}

static SEQ: AtomicU64 = AtomicU64::new(0);

/// Queue a command for the mod (`save`, `stop`, `say hello`, `kick 7656...`).
pub fn send_command(runtime: &Path, text: &str) -> std::io::Result<()> {
    let dir = runtime.join("commands");
    std::fs::create_dir_all(&dir)?;
    let n = SEQ.fetch_add(1, Ordering::SeqCst);
    let name = format!("{:016}-{}-{:04}", now_ms(), std::process::id(), n);
    let tmp = dir.join(format!("{name}.tmp"));
    std::fs::write(&tmp, text.as_bytes())?;
    std::fs::rename(&tmp, dir.join(format!("{name}.cmd")))
}

/// Administrative commands always pass through the manager, including stop and backup.
pub fn send_admin_command(runtime: &Path, text: &str) -> std::io::Result<()> {
    send_command(&runtime.join("admin"), text)
}

pub fn take_admin_commands(runtime: &Path) -> Vec<String> {
    take_commands(&runtime.join("admin"))
}

/// Mod side of the same channel, used by the built-in fake game.
pub fn take_commands(runtime: &Path) -> Vec<String> {
    let dir = runtime.join("commands");
    let mut files: Vec<PathBuf> = match std::fs::read_dir(&dir) {
        Ok(rd) => rd
            .filter_map(|e| e.ok())
            .map(|e| e.path())
            .filter(|p| p.extension().map(|x| x == "cmd").unwrap_or(false))
            .collect(),
        Err(_) => return Vec::new(),
    };
    files.sort();
    let mut out = Vec::new();
    for f in files {
        if let Ok(t) = std::fs::read_to_string(&f) {
            out.push(t.trim().to_string());
        }
        let _ = std::fs::remove_file(&f);
    }
    out
}
