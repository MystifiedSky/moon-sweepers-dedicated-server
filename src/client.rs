//! Player side: the saved server list (favourites, names), checking a server's
//! required mods against what is installed, and handing the join request to the
//! game mod.

use crate::a2s::Query;
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

pub const DEFAULT_QUERY_PORT: u16 = 27026;

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
pub struct Entry {
    pub name: String,
    /// `host` or `host:queryport`
    pub address: String,
    #[serde(default)]
    pub favorite: bool,
}

#[derive(Clone, Debug, Serialize, Deserialize, Default)]
pub struct Store {
    #[serde(skip)]
    pub load_error: Option<String>,
    #[serde(default)]
    pub game_dir: String,
    #[serde(default)]
    pub servers: Vec<Entry>,
}

pub fn home() -> PathBuf {
    if let Some(h) = std::env::var_os("MOON_DS_HOME") {
        return PathBuf::from(h);
    }
    if cfg!(windows) {
        if let Some(a) = std::env::var_os("APPDATA") {
            return PathBuf::from(a).join("MoonDS");
        }
    } else if let Some(x) = std::env::var_os("XDG_CONFIG_HOME") {
        return PathBuf::from(x).join("moon-ds");
    } else if let Some(h) = std::env::var_os("HOME") {
        return PathBuf::from(h).join(".config").join("moon-ds");
    }
    PathBuf::from("moon-ds-data")
}

impl Store {
    pub fn path() -> PathBuf {
        home().join("servers.json")
    }

    pub fn load() -> Store {
        let mut s: Store = match std::fs::read_to_string(Self::path()) {
            Ok(t) => serde_json::from_str(&t).unwrap_or_else(|e| Store { load_error: Some(format!("Cannot read saved servers: {e}. Original servers.json retained; repair or move it and restart.")), ..Default::default() }),
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => Store::default(),
            Err(e) => Store { load_error: Some(format!("Cannot read saved servers: {e}")), ..Default::default() },
        };
        if let Some(path) = std::env::var_os("MOON_DS_CLIENT_GAME_DIR") {
            let path = PathBuf::from(path);
            if path.join("MoonSweepers.exe").is_file() {
                s.game_dir = path.to_string_lossy().into_owned();
            }
        }
        if s.game_dir.is_empty() {
            if let Some(d) = detect_game_dir() {
                s.game_dir = d.to_string_lossy().into_owned();
            }
        }
        s
    }

    pub fn save(&self) -> Result<(), String> {
        if let Some(error) = &self.load_error {
            return Err(error.clone());
        }
        let write = || -> Result<(), String> {
            std::fs::create_dir_all(home()).map_err(|e| e.to_string())?;
            let lock = std::fs::OpenOptions::new()
                .create(true)
                .truncate(false)
                .write(true)
                .open(home().join("servers.lock"))
                .map_err(|e| e.to_string())?;
            lock.try_lock()
                .map_err(|e| format!("Saved servers are in use: {e}"))?;
            let tmp = home().join(format!("servers-{}.tmp", std::process::id()));
            let result = (|| -> Result<(), String> {
                use std::io::Write;
                let mut f = std::fs::File::create(&tmp).map_err(|e| e.to_string())?;
                f.write_all(&serde_json::to_vec_pretty(self).map_err(|e| e.to_string())?)
                    .map_err(|e| e.to_string())?;
                f.sync_all().map_err(|e| e.to_string())?;
                drop(f);
                std::fs::rename(&tmp, Self::path()).map_err(|e| e.to_string())
            })();
            if result.is_err() {
                let _ = std::fs::remove_file(tmp);
            }
            result
        };
        write()
    }

    pub fn sort(&mut self) {
        self.servers.sort_by(|a, b| {
            b.favorite
                .cmp(&a.favorite)
                .then(a.name.to_lowercase().cmp(&b.name.to_lowercase()))
        });
    }
}

/// Accept `host`, `host:port`, `ip:port`; returns the cleaned address or an error.
pub fn clean_address(text: &str) -> Result<String, String> {
    let t = text.trim();
    if t.is_empty() {
        return Err("Enter the server address, for example play.example.com:27026".into());
    }
    if t.contains(' ') || t.contains('/') {
        return Err("The address cannot contain spaces or slashes".into());
    }
    if let Some((h, p)) = t.rsplit_once(':') {
        if h.is_empty() || p.parse::<u16>().map(|n| n == 0).unwrap_or(true) {
            return Err("The port must be a number from 1 to 65535".into());
        }
    }
    Ok(t.to_string())
}

pub fn detect_game_dir() -> Option<PathBuf> {
    let mut libs: Vec<PathBuf> = Vec::new();
    if cfg!(windows) {
        for var in ["ProgramFiles(x86)", "ProgramFiles"] {
            if let Some(p) = std::env::var_os(var) {
                libs.push(PathBuf::from(p).join("Steam"));
            }
        }
    } else if let Some(h) = std::env::var_os("HOME") {
        let h = PathBuf::from(h);
        libs.push(h.join(".steam").join("steam"));
        libs.push(h.join(".local").join("share").join("Steam"));
    }
    let mut extra = Vec::new();
    for l in &libs {
        if let Ok(t) = std::fs::read_to_string(l.join("steamapps").join("libraryfolders.vdf")) {
            for line in t.lines() {
                let line = line.trim();
                if line.starts_with("\"path\"") {
                    if let Some(v) = line.split('"').nth(3) {
                        extra.push(PathBuf::from(v.replace("\\\\", "\\")));
                    }
                }
            }
        }
    }
    libs.extend(extra);
    libs.into_iter()
        .map(|l| l.join("steamapps").join("common").join("Moon Sweepers"))
        .find(|p| p.join("MoonSweepers.exe").exists())
}

#[derive(Clone, Debug, Deserialize)]
pub struct InstalledMod {
    pub guid: String,
    #[serde(default)]
    pub version: String,
}

fn mod_dir(game_dir: &Path) -> PathBuf {
    game_dir.join("BepInEx").join("moon-ds")
}

/// The mods the game mod saw the last time the game started. `None` = the game was never started with the mod.
pub fn installed_mods(game_dir: &Path) -> Option<Vec<InstalledMod>> {
    let t = std::fs::read_to_string(mod_dir(game_dir).join("installed-mods.json")).ok()?;
    serde_json::from_str(&t).ok()
}

pub fn mod_installed(game_dir: &Path) -> bool {
    let plugins = game_dir.join("BepInEx").join("plugins");
    fn find(dir: &Path, depth: u32) -> bool {
        let Ok(rd) = std::fs::read_dir(dir) else {
            return false;
        };
        for e in rd.flatten() {
            let p = e.path();
            if p.is_dir() && depth < 3 {
                if find(&p, depth + 1) {
                    return true;
                }
            } else if p
                .file_name()
                .map(|n| {
                    n.to_string_lossy()
                        .eq_ignore_ascii_case("MoonDedicated.dll")
                })
                .unwrap_or(false)
            {
                return true;
            }
        }
        false
    }
    find(&plugins, 0)
}

#[derive(Clone, Debug, PartialEq)]
pub enum ModStatus {
    Ok,
    Missing,
    WrongVersion {
        have: String,
    },
    /// Cannot tell yet: the game has not been started once with the mod.
    Unknown,
}

pub fn check_mods(
    required: &[(String, Option<String>)],
    installed: Option<&[InstalledMod]>,
) -> Vec<(String, Option<String>, ModStatus)> {
    required
        .iter()
        .map(|(g, v)| {
            let st = match installed {
                None => ModStatus::Unknown,
                Some(list) => match list.iter().find(|m| m.guid.eq_ignore_ascii_case(g)) {
                    None => ModStatus::Missing,
                    Some(m) => match v {
                        Some(want) if !want.is_empty() && &m.version != want => {
                            ModStatus::WrongVersion {
                                have: m.version.clone(),
                            }
                        }
                        _ => ModStatus::Ok,
                    },
                },
            };
            (g.clone(), v.clone(), st)
        })
        .collect()
}

#[derive(Serialize)]
struct ConnectRequest<'a> {
    address: &'a str,
    query_port: u16,
    steam_id: String,
    password: &'a str,
    name: &'a str,
    created_unix_ms: u64,
}

/// Hand the join request to the game mod. The mod deletes the file as soon as it has read it.
pub fn write_connect(
    game_dir: &Path,
    address: &str,
    q: &Query,
    password: &str,
) -> Result<(), String> {
    let id = q.info.steam_id.ok_or(
        "The server did not tell its Steam ID; it may not be a Moon Sweepers dedicated server",
    )?;
    let dir = mod_dir(game_dir);
    std::fs::create_dir_all(&dir)
        .map_err(|e| format!("cannot write into {}: {e}", dir.display()))?;
    let port = crate::a2s::resolve(address, DEFAULT_QUERY_PORT)
        .map(|a| a.port())
        .unwrap_or(DEFAULT_QUERY_PORT);
    let req = ConnectRequest {
        address,
        query_port: port,
        steam_id: id.to_string(),
        password,
        name: &q.info.name,
        created_unix_ms: crate::state::now_ms(),
    };
    let tmp = dir.join("connect.json.tmp");
    std::fs::write(
        &tmp,
        serde_json::to_vec_pretty(&req).map_err(|e| e.to_string())?,
    )
    .map_err(|e| e.to_string())?;
    std::fs::rename(&tmp, dir.join("connect.json")).map_err(|e| e.to_string())
}

#[derive(Clone, Debug, Deserialize)]
pub struct JoinResult {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: String,
}

pub fn read_join_result(game_dir: &Path) -> Option<JoinResult> {
    serde_json::from_str(&std::fs::read_to_string(mod_dir(game_dir).join("last-join.json")).ok()?)
        .ok()
}

pub fn launch_game() -> Result<(), String> {
    let uri = format!("steam://rungameid/{}", crate::config::APP_ID);
    let r = if cfg!(windows) {
        std::process::Command::new("cmd")
            .args(["/C", "start", "", &uri])
            .spawn()
    } else {
        std::process::Command::new("xdg-open").arg(&uri).spawn()
    };
    r.map(|_| ())
        .map_err(|e| format!("could not ask Steam to start the game: {e}"))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn address_cleaning() {
        assert!(clean_address("a.b:27026").is_ok());
        assert!(clean_address("host").is_ok());
        assert!(clean_address("host:0").is_err());
        assert!(clean_address("").is_err());
    }
    #[test]
    fn mod_status() {
        let have = vec![InstalledMod {
            guid: "a.b".into(),
            version: "1.0".into(),
        }];
        let r = check_mods(
            &[
                ("a.b".into(), Some("1.0".into())),
                ("c.d".into(), None),
                ("a.b".into(), Some("2.0".into())),
            ],
            Some(&have),
        );
        assert_eq!(r[0].2, ModStatus::Ok);
        assert_eq!(r[1].2, ModStatus::Missing);
        assert!(matches!(r[2].2, ModStatus::WrongVersion { .. }));
        assert_eq!(
            check_mods(&[("x".into(), None)], None)[0].2,
            ModStatus::Unknown
        );
    }
}
