//! Address queries for private servers, independent of Steam master-list advertising.
use crate::{a2s, config::Settings, state};
use std::hash::{BuildHasher, Hash, Hasher};
use std::net::UdpSocket;
use std::sync::{
    atomic::{AtomicBool, Ordering},
    Arc,
};
use std::time::{Duration, Instant};

pub struct Responder {
    stop: Arc<AtomicBool>,
    worker: Option<std::thread::JoinHandle<()>>,
}
impl Drop for Responder {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Relaxed);
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}
pub fn start(settings: &Settings) -> Result<Responder, String> {
    let sock = UdpSocket::bind(("0.0.0.0", settings.query_port))
        .map_err(|e| format!("cannot bind query port {}: {e}", settings.query_port))?;
    sock.set_read_timeout(Some(Duration::from_millis(100)))
        .map_err(|e| e.to_string())?;
    let s = settings.clone();
    let stop = Arc::new(AtomicBool::new(false));
    let flag = stop.clone();
    let worker = std::thread::spawn(move || {
        let secret = std::collections::hash_map::RandomState::new();
        let mut buffer = [0; 1500];
        let mut window = Instant::now();
        let mut count = 0;
        let mut seq = 0u32;
        while !flag.load(Ordering::Relaxed) {
            let Ok((n, peer)) = sock.recv_from(&mut buffer) else {
                continue;
            };
            if window.elapsed() >= Duration::from_secs(1) {
                window = Instant::now();
                count = 0;
            }
            count += 1;
            if count > 100 || n < 5 || buffer[..4] != [255; 4] {
                continue;
            }
            let kind = buffer[4];
            if kind != b'T' && kind != b'V' {
                continue;
            }
            let bucket = state::now_ms() / 30000;
            let cookie = |bucket: u64| {
                let mut h = secret.build_hasher();
                peer.hash(&mut h);
                bucket.hash(&mut h);
                (h.finish() as u32).to_le_bytes()
            };
            let offset = if kind == b'V' { 5 } else { 25 };
            if kind == b'T' && (n < 25 || &buffer[5..25] != b"Source Engine Query\0") {
                continue;
            }
            let valid = buffer
                .get(offset..offset + 4)
                .is_some_and(|c| c == cookie(bucket) || c == cookie(bucket.saturating_sub(1)));
            if !valid {
                let mut challenge = vec![255, 255, 255, 255, 0x41];
                challenge.extend(cookie(bucket));
                let _ = sock.send_to(&challenge, peer);
                continue;
            }
            let Some(st) = state::read_state(&s.runtime_dir())
                .filter(|st| state::now_ms().saturating_sub(st.heartbeat_unix_ms) < 10000)
            else {
                continue;
            };
            let reply = if kind == b'T' {
                a2s::build_info(
                    &a2s::ServerInfo {
                        name: s.server_name.clone(),
                        map: format!("slot {}", s.save_slot),
                        game: "Moon Sweepers".into(),
                        players: st.players.len().min(255) as u8,
                        max_players: s.max_players,
                        password: s.password_enabled(),
                        version: crate::config::SUPPORTED_GAME.into(),
                        steam_id: st.server_steam_id.parse().ok(),
                        keywords: if st.ready {
                            "ready".into()
                        } else {
                            "starting".into()
                        },
                        ping_ms: 0,
                    },
                    s.game_port,
                )
            } else {
                a2s::build_rules(&[
                    ("moon_ds".into(), "3".into()),
                    ("moon_ver".into(), crate::config::SUPPORTED_GAME.into()),
                    ("moon_policy".into(), s.mod_policy.name().into()),
                    (
                        "moon_mods".into(),
                        s.required_mods
                            .iter()
                            .map(|m| m.render())
                            .collect::<Vec<_>>()
                            .join(","),
                    ),
                    ("moon_motd".into(), s.motd.clone()),
                    ("moon_ready".into(), st.ready.to_string()),
                ])
            };
            if reply.len() > 128 * 1188 {
                continue;
            }
            if reply.len() <= 1200 {
                let _ = sock.send_to(&reply, peer);
            } else {
                seq = seq.wrapping_add(1) & 0x7fffffff;
                let total = reply.len().div_ceil(1188);
                for (index, part) in reply.chunks(1188).enumerate() {
                    let mut packet = vec![254, 255, 255, 255];
                    packet.extend(seq.to_le_bytes());
                    packet.extend([total as u8, index as u8]);
                    packet.extend(1200u16.to_le_bytes());
                    packet.extend(part);
                    let _ = sock.send_to(&packet, peer);
                }
            }
        }
    });
    Ok(Responder {
        stop,
        worker: Some(worker),
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn private_query_roundtrips_state_and_releases_port() {
        let root = std::env::temp_dir().join(format!(
            "moon-query-{}-{}",
            std::process::id(),
            state::now_ms()
        ));
        let probe = UdpSocket::bind("127.0.0.1:0").unwrap();
        let port = probe.local_addr().unwrap().port();
        drop(probe);
        let mut s = Settings {
            data_dir: root.clone(),
            query_port: port,
            ..Default::default()
        };
        s.finish().unwrap();
        state::write_state_atomic(
            &s.runtime_dir(),
            &state::HostState {
                heartbeat_unix_ms: state::now_ms(),
                ready: true,
                server_steam_id: "90271234567890123".into(),
                ..Default::default()
            },
        )
        .unwrap();
        let responder = start(&s).unwrap();
        let answer = a2s::query(
            &format!("127.0.0.1:{port}"),
            port,
            Duration::from_secs(2),
            true,
        )
        .unwrap();
        assert_eq!(answer.info.steam_id, Some(90271234567890123));
        assert_eq!(answer.rule("moon_ready"), Some("true"));
        assert_eq!(answer.rule("moon_policy"), Some("required"));
        drop(responder);
        assert!(UdpSocket::bind(("127.0.0.1", port)).is_ok());
        std::fs::remove_dir_all(root).unwrap();
    }
}
