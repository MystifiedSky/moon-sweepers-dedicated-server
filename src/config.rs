//! Server settings: defaults < server.properties < MOON_* environment < --command-line.
//!
//! Every setting has one name (`MOON_SERVER_PASSWORD`, ...) that is used in the
//! properties file, in the environment and (lower-cased, without `MOON_`) on the
//! command line, so AMP, Docker, systemd and a plain shell all behave the same.

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

pub const APP_ID: u32 = 5335510;
pub const SUPPORTED_GAME: &str = "1.0.5";

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ModReq {
    pub guid: String,
    pub version: Option<String>,
}

impl ModReq {
    pub fn parse(text: &str) -> Option<ModReq> {
        let t = text.trim();
        if t.is_empty() {
            return None;
        }
        let (g, v) = match t.split_once('@') {
            Some((g, v)) => (
                g.trim(),
                Some(v.trim().to_string()).filter(|s| !s.is_empty()),
            ),
            None => (t, None),
        };
        if g.is_empty() {
            return None;
        }
        Some(ModReq {
            guid: g.to_string(),
            version: v,
        })
    }
    pub fn render(&self) -> String {
        match &self.version {
            Some(v) => format!("{}@{}", self.guid, v),
            None => self.guid.clone(),
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ModPolicy {
    /// Do not check mods at all.
    Off,
    /// Players must have every required mod (extra mods are fine).
    Required,
    /// Players must have exactly the required mods, nothing more.
    Strict,
}

impl ModPolicy {
    pub fn name(self) -> &'static str {
        match self {
            ModPolicy::Off => "off",
            ModPolicy::Required => "required",
            ModPolicy::Strict => "strict",
        }
    }
}

#[derive(Clone, Debug)]
pub struct Settings {
    pub server_name: String,
    pub motd: String,
    /// Empty = no password (disabled, the default).
    pub password: String,
    /// Empty = no allow-list (disabled, the default).
    pub allowed_steam_ids: Vec<u64>,
    pub game_port: u16,
    pub query_port: u16,
    pub max_players: u8,
    pub save_slot: u8,
    pub autosave_minutes: u32,
    pub backup_keep: u32,
    pub backup_on_stop: bool,
    pub game_dir: PathBuf,
    pub data_dir: PathBuf,
    pub backup_dir: PathBuf,
    pub required_mods: Vec<ModReq>,
    pub mod_policy: ModPolicy,
    pub wine_bin: String,
    pub wine_prefix: PathBuf,
    pub stop_timeout_secs: u32,
    pub auto_restart: u32,
    pub extra_args: Vec<String>,
    /// Folder holding Valve's Steamworks redistributable (steamclient64.dll ...), optional.
    pub steam_redist_dir: PathBuf,
    /// List the server in Steam's public server browser (default off: players join by address).
    pub steam_listing: bool,
    /// Frame cap of the headless game (steady simulation pacing; 30 like the reference setup).
    pub server_fps: u32,
}

impl Default for Settings {
    fn default() -> Self {
        Settings {
            server_name: "Moon Sweepers Dedicated Server".into(),
            motd: String::new(),
            password: String::new(),
            allowed_steam_ids: Vec::new(),
            game_port: 27025,
            query_port: 27026,
            max_players: 8,
            save_slot: 1,
            autosave_minutes: 10,
            backup_keep: 10,
            backup_on_stop: true,
            game_dir: PathBuf::from("game"),
            data_dir: PathBuf::from("data"),
            backup_dir: PathBuf::new(),
            required_mods: Vec::new(),
            mod_policy: ModPolicy::Required,
            wine_bin: "wine".into(),
            wine_prefix: PathBuf::new(),
            stop_timeout_secs: 120,
            auto_restart: 0,
            extra_args: Vec::new(),
            steam_redist_dir: PathBuf::new(),
            steam_listing: false,
            server_fps: 30,
        }
    }
}

/// Parse `KEY=value` lines. `#`/`;` comments, blank lines, optional single or
/// double quotes around the value (the AMP / env-file style the examples use).
pub fn parse_properties(text: &str) -> BTreeMap<String, String> {
    let mut out = BTreeMap::new();
    for raw in text.lines() {
        let line = raw.trim_start_matches('\u{feff}').trim();
        if line.is_empty() || line.starts_with('#') || line.starts_with(';') {
            continue;
        }
        let Some((k, v)) = line.split_once('=') else {
            continue;
        };
        let key = k
            .trim()
            .trim_start_matches("export ")
            .trim()
            .to_ascii_uppercase();
        out.insert(key, unquote(v.trim()));
    }
    out
}

fn unquote(v: &str) -> String {
    let b = v.as_bytes();
    if b.len() >= 2
        && ((b[0] == b'\'' && b[b.len() - 1] == b'\'') || (b[0] == b'"' && b[b.len() - 1] == b'"'))
    {
        v[1..v.len() - 1].to_string()
    } else {
        v.to_string()
    }
}

/// Quotes group an argument; backslashes remain literal (including Windows paths).
/// A backslash immediately before a quote escapes that quote.
pub fn split_extra_args(text: &str) -> Result<Vec<String>, String> {
    let mut words = Vec::new();
    let mut word = String::new();
    let mut quote = None;
    let mut started = false;
    let mut chars = text.chars().peekable();
    while let Some(c) = chars.next() {
        if c == '\\' {
            let mut slashes = 1;
            while chars.peek() == Some(&'\\') {
                chars.next();
                slashes += 1;
            }
            if chars.peek().is_some_and(|c| *c == '"' || *c == '\'') {
                word.extend(std::iter::repeat_n('\\', slashes / 2));
                if slashes % 2 == 1 {
                    word.push(chars.next().unwrap());
                }
                // An even run leaves the quote for the next iteration.
            } else {
                word.extend(std::iter::repeat_n('\\', slashes));
            }
            started = true;
        } else if quote == Some(c) {
            quote = None;
        } else if quote.is_none() && (c == '"' || c == '\'') {
            quote = Some(c);
            started = true;
        } else if quote.is_none() && c.is_whitespace() {
            if started {
                words.push(std::mem::take(&mut word));
                started = false;
            }
        } else {
            word.push(c);
            started = true;
        }
    }
    if quote.is_some() {
        return Err("MOON_EXTRA_ARGS has an unterminated quote".into());
    }
    if started {
        words.push(word);
    }
    Ok(words)
}

/// All settings options consume one value, including --config. Exactly one positional.
pub fn positional_options(args: &[String]) -> Result<(String, Vec<String>), String> {
    let mut positional = None;
    let mut options = Vec::new();
    let mut i = 0;
    let mut literal = false;
    while i < args.len() {
        let arg = &args[i];
        if !literal && arg == "--" {
            literal = true;
        } else if !literal && arg.starts_with("--") {
            options.push(arg.clone());
            if !arg.contains('=') {
                i += 1;
                let value = args
                    .get(i)
                    .filter(|s| !s.starts_with("--"))
                    .ok_or_else(|| format!("{arg} needs a value"))?;
                options.push(value.clone());
            }
        } else if positional.replace(arg.clone()).is_some() {
            return Err("expected exactly one filename/folder".into());
        }
        i += 1;
    }
    Ok((positional.ok_or("missing filename/folder")?, options))
}

/// A SteamID64 for an ordinary public player account.
pub fn valid_player_steam_id(id: u64) -> bool {
    (id >> 56) == 1
        && ((id >> 52) & 0xF) == 1
        && ((id >> 32) & 0xFFFFF) == 1
        && (id & 0xFFFF_FFFF) != 0
}

pub fn parse_steam_ids(text: &str) -> Result<Vec<u64>, String> {
    let mut ids = Vec::new();
    for part in text.split(|c: char| c == ',' || c == ';' || c.is_whitespace()) {
        let p = part.trim();
        if p.is_empty() {
            continue;
        }
        let id: u64 = p.parse().map_err(|_| format!("'{p}' is not a number"))?;
        if !valid_player_steam_id(id) {
            return Err(format!(
                "'{p}' is not a valid player SteamID64 (they look like 76561198012345678)"
            ));
        }
        if !ids.contains(&id) {
            ids.push(id);
        }
    }
    if ids.len() > 15 {
        return Err("at most 15 SteamIDs can be listed".into());
    }
    Ok(ids)
}

fn parse_bool(key: &str, v: &str) -> Result<bool, String> {
    match v.trim().to_ascii_lowercase().as_str() {
        "1" | "true" | "yes" | "on" => Ok(true),
        "0" | "false" | "no" | "off" | "" => Ok(false),
        _ => Err(format!("{key} must be true or false")),
    }
}

fn parse_num<T: std::str::FromStr + PartialOrd + std::fmt::Display>(
    key: &str,
    v: &str,
    lo: T,
    hi: T,
) -> Result<T, String> {
    let n: T = v
        .trim()
        .parse()
        .map_err(|_| format!("{key} must be a whole number"))?;
    if n < lo || n > hi {
        return Err(format!("{key} must be between {lo} and {hi}"));
    }
    Ok(n)
}

pub const KEYS: &[&str] = &[
    "MOON_SERVER_NAME",
    "MOON_MOTD",
    "MOON_SERVER_PASSWORD",
    "MOON_ALLOWED_STEAM_IDS",
    "MOON_GAME_PORT",
    "MOON_QUERY_PORT",
    "MOON_MAX_PLAYERS",
    "MOON_SLOT",
    "MOON_AUTOSAVE_MINUTES",
    "MOON_BACKUP_KEEP",
    "MOON_BACKUP_ON_STOP",
    "MOON_GAME_DIR",
    "MOON_DATA_DIR",
    "MOON_BACKUP_DIR",
    "MOON_REQUIRED_MODS",
    "MOON_MOD_POLICY",
    "MOON_WINE",
    "MOON_WINE_PREFIX",
    "MOON_STOP_TIMEOUT",
    "MOON_AUTO_RESTART",
    "MOON_EXTRA_ARGS",
    "MOON_STEAM_REDIST_DIR",
    "MOON_STEAM_LISTING",
    "MOON_SERVER_FPS",
];

impl Settings {
    /// Merge a key/value map on top of the current settings.
    pub fn apply(&mut self, map: &BTreeMap<String, String>) -> Result<(), String> {
        for (k, v) in map {
            // AMP installation metadata shares the generated properties file.
            if ["GAMESOURCE", "GAMESOURCEPATH", "RELEASEREPO"].contains(&k.as_str()) {
                continue;
            }
            if !KEYS.contains(&k.as_str()) {
                return Err(format!("unknown setting '{k}'"));
            }
            match k.as_str() {
                "MOON_SERVER_NAME" => {
                    if !v.trim().is_empty() {
                        self.server_name = v.chars().take(63).collect();
                    }
                }
                "MOON_MOTD" => self.motd = v.chars().take(200).collect(),
                "MOON_SERVER_PASSWORD" => {
                    if v.len() > 128 || v.bytes().any(|b| !(32..=126).contains(&b)) {
                        return Err("MOON_SERVER_PASSWORD must be at most 128 printable ASCII characters (leave it empty for no password)".into());
                    }
                    self.password = v.clone();
                }
                "MOON_ALLOWED_STEAM_IDS" => {
                    self.allowed_steam_ids =
                        parse_steam_ids(v).map_err(|e| format!("MOON_ALLOWED_STEAM_IDS: {e}"))?
                }
                "MOON_GAME_PORT" if !v.trim().is_empty() => {
                    self.game_port = parse_num(k, v, 1024u16, 65535)?
                }
                "MOON_QUERY_PORT" if !v.trim().is_empty() => {
                    self.query_port = parse_num(k, v, 1024u16, 65535)?
                }
                "MOON_MAX_PLAYERS" if !v.trim().is_empty() => {
                    self.max_players = parse_num(k, v, 1u8, 15)?
                }
                "MOON_SLOT" if !v.trim().is_empty() => self.save_slot = parse_num(k, v, 1u8, 5)?,
                "MOON_AUTOSAVE_MINUTES" if !v.trim().is_empty() => {
                    self.autosave_minutes = parse_num(k, v, 0u32, 1440)?
                }
                "MOON_BACKUP_KEEP" if !v.trim().is_empty() => {
                    self.backup_keep = parse_num(k, v, 0u32, 1000)?
                }
                "MOON_BACKUP_ON_STOP" => self.backup_on_stop = parse_bool(k, v)?,
                "MOON_GAME_DIR" if !v.trim().is_empty() => self.game_dir = PathBuf::from(v),
                "MOON_DATA_DIR" if !v.trim().is_empty() => self.data_dir = PathBuf::from(v),
                "MOON_BACKUP_DIR" if !v.trim().is_empty() => self.backup_dir = PathBuf::from(v),
                "MOON_REQUIRED_MODS" => {
                    self.required_mods = v
                        .split([',', ';', '\n'])
                        .filter_map(ModReq::parse)
                        .collect()
                }
                "MOON_MOD_POLICY" => {
                    self.mod_policy = match v.trim().to_ascii_lowercase().as_str() {
                        "" | "required" => ModPolicy::Required,
                        "off" | "none" => ModPolicy::Off,
                        "strict" | "exact" => ModPolicy::Strict,
                        _ => return Err("MOON_MOD_POLICY must be off, required or strict".into()),
                    }
                }
                "MOON_WINE" if !v.trim().is_empty() => self.wine_bin = v.clone(),
                "MOON_WINE_PREFIX" if !v.trim().is_empty() => self.wine_prefix = PathBuf::from(v),
                "MOON_STOP_TIMEOUT" if !v.trim().is_empty() => {
                    self.stop_timeout_secs = parse_num(k, v, 10u32, 3600)?
                }
                "MOON_AUTO_RESTART" if !v.trim().is_empty() => {
                    self.auto_restart = parse_num(k, v, 0u32, 100)?
                }
                "MOON_STEAM_REDIST_DIR" if !v.trim().is_empty() => {
                    self.steam_redist_dir = PathBuf::from(v)
                }
                "MOON_SERVER_FPS" if !v.trim().is_empty() => {
                    self.server_fps = parse_num(k, v, 10u32, 120)?
                }
                "MOON_STEAM_LISTING" => self.steam_listing = parse_bool(k, v)?,
                "MOON_EXTRA_ARGS" => self.extra_args = split_extra_args(v)?,
                _ => {}
            }
        }
        Ok(())
    }

    pub fn finish(&mut self) -> Result<(), String> {
        if self.game_port == self.query_port {
            return Err("MOON_GAME_PORT and MOON_QUERY_PORT must be different".into());
        }
        if self.backup_dir.as_os_str().is_empty() {
            self.backup_dir = self.data_dir.join("backups");
        }
        if self.wine_prefix.as_os_str().is_empty() {
            self.wine_prefix = self.data_dir.join("wine");
        }
        let base = std::env::current_dir().map_err(|e| e.to_string())?;
        for path in [
            &mut self.game_dir,
            &mut self.data_dir,
            &mut self.backup_dir,
            &mut self.wine_prefix,
            &mut self.steam_redist_dir,
        ] {
            if !path.as_os_str().is_empty() && path.is_relative() {
                *path = base.join(&*path);
            }
        }
        Ok(())
    }

    pub fn runtime_dir(&self) -> PathBuf {
        self.data_dir.join("runtime")
    }

    pub fn password_enabled(&self) -> bool {
        !self.password.is_empty()
    }

    pub fn allowlist_enabled(&self) -> bool {
        !self.allowed_steam_ids.is_empty()
    }

    /// Environment handed to the game process (the mod reads exactly these).
    pub fn child_env(&self) -> Vec<(String, String)> {
        let ids = self
            .allowed_steam_ids
            .iter()
            .map(|i| i.to_string())
            .collect::<Vec<_>>()
            .join(",");
        let mods = self
            .required_mods
            .iter()
            .map(|m| m.render())
            .collect::<Vec<_>>()
            .join(",");
        vec![
            ("MOON_DEDICATED".into(), "1".into()),
            (
                "MOON_DS_SAVE_ROOT".into(),
                self.data_dir.join("world").to_string_lossy().into_owned(),
            ),
            ("MOON_SERVER_NAME".into(), self.server_name.clone()),
            ("MOON_MOTD".into(), self.motd.clone()),
            ("MOON_SERVER_PASSWORD".into(), self.password.clone()),
            ("MOON_ALLOWED_STEAM_IDS".into(), ids),
            ("MOON_GAME_PORT".into(), self.game_port.to_string()),
            ("MOON_QUERY_PORT".into(), self.query_port.to_string()),
            ("MOON_MAX_PLAYERS".into(), self.max_players.to_string()),
            ("MOON_SLOT".into(), self.save_slot.to_string()),
            ("MOON_REQUIRED_MODS".into(), mods),
            ("MOON_MOD_POLICY".into(), self.mod_policy.name().into()),
            (
                "MOON_DS_RUNTIME".into(),
                self.runtime_dir().to_string_lossy().into_owned(),
            ),
            (
                "MOON_AUTOSAVE_MINUTES".into(),
                self.autosave_minutes.to_string(),
            ),
            ("MOON_SERVER_FPS".into(), self.server_fps.to_string()),
            (
                "MOON_STEAM_LISTING".into(),
                if self.steam_listing {
                    "1".into()
                } else {
                    "0".into()
                },
            ),
        ]
    }

    /// One-screen summary for the console. The password is never printed.
    pub fn summary(&self) -> Vec<String> {
        vec![
            format!("name            : {}", self.server_name),
            format!(
                "game / query    : UDP {} / UDP {}",
                self.game_port, self.query_port
            ),
            format!("max players     : {}", self.max_players),
            format!("save slot       : {}", self.save_slot),
            format!(
                "password        : {}",
                if self.password_enabled() {
                    "enabled"
                } else {
                    "disabled"
                }
            ),
            format!(
                "steam allow-list: {}",
                if self.allowlist_enabled() {
                    format!("{} id(s)", self.allowed_steam_ids.len())
                } else {
                    "disabled".into()
                }
            ),
            format!(
                "required mods   : {} (policy: {})",
                if self.required_mods.is_empty() {
                    "none".to_string()
                } else {
                    self.required_mods
                        .iter()
                        .map(|m| m.render())
                        .collect::<Vec<_>>()
                        .join(", ")
                },
                self.mod_policy.name()
            ),
            format!(
                "autosave        : {}",
                if self.autosave_minutes == 0 {
                    "game default".to_string()
                } else {
                    format!("every {} min", self.autosave_minutes)
                }
            ),
            format!(
                "backups         : keep {} in {}",
                self.backup_keep,
                self.backup_dir.display()
            ),
            format!("game folder     : {}", self.game_dir.display()),
        ]
    }
}

/// Load settings. `args` are the arguments after the sub-command (`--key value` or `--key=value`).
pub fn load(args: &[String]) -> Result<Settings, String> {
    let mut s = Settings::default();
    let mut cli: BTreeMap<String, String> = BTreeMap::new();
    let mut file: Option<PathBuf> = None;
    let mut i = 0;
    while i < args.len() {
        let a = &args[i];
        if let Some(rest) = a.strip_prefix("--") {
            let (k, v) = match rest.split_once('=') {
                Some((k, v)) => (k.to_string(), v.to_string()),
                None => {
                    if i + 1 < args.len() && !args[i + 1].starts_with("--") {
                        i += 1;
                        (rest.to_string(), args[i].clone())
                    } else {
                        return Err(format!("--{rest} needs a value"));
                    }
                }
            };
            if k == "config" {
                file = Some(PathBuf::from(v));
            } else {
                let key = format!("MOON_{}", k.to_ascii_uppercase().replace('-', "_"));
                cli.insert(key, v);
            }
        }
        i += 1;
    }
    let selected = file.or_else(|| std::env::var("MOON_CONFIG").ok().map(PathBuf::from));
    let explicit = selected.is_some();
    let file = selected.unwrap_or_else(|| PathBuf::from("server.properties"));
    if explicit || file.exists() {
        let text = std::fs::read_to_string(&file)
            .map_err(|e| format!("cannot read {}: {e}", file.display()))?;
        s.apply(&parse_properties(&text))?;
    }
    let mut env = BTreeMap::new();
    for k in KEYS {
        if let Ok(v) = std::env::var(k) {
            env.insert((*k).to_string(), v);
        }
    }
    s.apply(&env)?;
    s.apply(&cli)?;
    s.finish()?;
    Ok(s)
}

pub fn exe_name() -> &'static str {
    "MoonSweepers.exe"
}

/// Validate the game folder is an installed Moon Sweepers copy.
pub fn check_game_dir(dir: &Path) -> Result<(), String> {
    if !dir.join(exe_name()).exists() {
        return Err(format!(
            "{} not found in '{}'. Set MOON_GAME_DIR to a folder holding a copy of the game (MoonSweepers.exe and GameAssembly.dll).",
            exe_name(),
            dir.display()
        ));
    }
    if !dir.join("GameAssembly.dll").exists() {
        return Err(format!("GameAssembly.dll not found in '{}'", dir.display()));
    }
    Ok(())
}

pub const PROPERTIES_TEMPLATE: &str = r#"# Moon Sweepers dedicated server settings.
# The same names work as environment variables and (lower-case, without MOON_) as --options.
# Quotes around values are optional. An empty value means "disabled / default".

MOON_SERVER_NAME=Moon Sweepers Dedicated Server
MOON_MOTD=

# Optional join password. Empty = no password (the default).
MOON_SERVER_PASSWORD=
# Optional comma-separated list of player SteamID64s allowed to join. Empty = everyone (the default).
MOON_ALLOWED_STEAM_IDS=

# UDP ports. Players join with  <your address>:<query port>  (the query port also answers server-browser pings).
MOON_GAME_PORT=27025
MOON_QUERY_PORT=27026

MOON_MAX_PLAYERS=8
MOON_SLOT=1
MOON_AUTOSAVE_MINUTES=10

# Mods players must have to join: BepInEx plugin GUIDs, optionally GUID@version, comma separated.
MOON_REQUIRED_MODS=
# off | required | strict (strict = players may not run any other mods)
MOON_MOD_POLICY=required

MOON_BACKUP_KEEP=10
MOON_BACKUP_ON_STOP=true

# Folder holding a copy of the game (MoonSweepers.exe) with BepInEx + the Moon Sweepers patcher installed.
MOON_GAME_DIR=game
MOON_DATA_DIR=data
# Optional: folder with Valve's Steamworks redistributable (steamclient64.dll, tier0_s64.dll, vstdlib_s64.dll).
MOON_STEAM_REDIST_DIR=
# List the server in Steam's public server browser (off by default).
MOON_STEAM_LISTING=false
# Frame cap of the headless game (10-120). Steady pacing keeps the simulation in step with players.
MOON_SERVER_FPS=30
# Linux only: wine binary used to run the Windows game, and the seconds allowed for a clean stop.
MOON_WINE=wine
MOON_STOP_TIMEOUT=120
MOON_AUTO_RESTART=0
"#;

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn blank_password_and_ids_are_disabled_by_default() {
        let s = load(&[]).unwrap();
        assert!(!s.password_enabled());
        assert!(!s.allowlist_enabled());
        assert_eq!((s.game_port, s.query_port), (27025, 27026));
    }

    #[test]
    fn quoted_empty_values_stay_disabled() {
        let m = parse_properties(
            "MOON_SERVER_PASSWORD=''\nMOON_ALLOWED_STEAM_IDS=\"\"\nMOON_GAME_PORT=27025\n",
        );
        let mut s = Settings::default();
        s.apply(&m).unwrap();
        assert!(!s.password_enabled() && !s.allowlist_enabled());
    }

    #[test]
    fn steam_id_validation() {
        assert!(parse_steam_ids("76561198000000001, 76561198000000002").is_ok());
        assert!(parse_steam_ids("123").is_err());
    }
}
