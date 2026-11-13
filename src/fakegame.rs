//! A stand-in for the game + mod, used by `--self-test` and for trying the
//! manager without the game installed. It speaks the same state/command files
//! as the real mod (`state.json`, `commands/*.cmd`).

use crate::state::{self, HostState, Player};
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};

pub fn fake_save_dir(runtime: &Path, slot: u8) -> PathBuf {
    runtime.join("fake-save").join(format!("slot-{slot}"))
}

pub fn run(runtime: &Path, slot: u8) -> i32 {
    let save = fake_save_dir(runtime, slot);
    let _ = std::fs::create_dir_all(&save);
    let _ = std::fs::write(save.join("session.moon"), b"fake session");
    let mut st = HostState {
        schema: 2,
        phase: "loading".into(),
        server_steam_id: "90271234567890123".into(),
        save_dir: save.to_string_lossy().into_owned(),
        game_port: 27025,
        query_port: 27026,
        mods: vec![format!("moon.dedicated@{}", env!("CARGO_PKG_VERSION"))],
        instance_id: std::env::var("MOON_DS_INSTANCE_ID").unwrap_or_default(),
        ..Default::default()
    };
    let start = Instant::now();
    let mut save_done_at: Option<Instant> = None;
    let mut revision = 0u32;
    let mut snapshot_requested = false;
    loop {
        for cmd in state::take_commands(runtime) {
            let (c, a) = cmd
                .split_once(' ')
                .map(|(c, a)| (c.to_string(), a.to_string()))
                .unwrap_or((cmd.clone(), String::new()));
            match c.as_str() {
                "save" | "snapshot" => {
                    snapshot_requested |= c == "snapshot";
                    if c == "snapshot" {
                        st.snapshot_request_id = a;
                        st.snapshot_dir.clear();
                        st.snapshot_error.clear();
                    }
                    if st.ready {
                        st.save_pending = true;
                        save_done_at = Some(Instant::now() + Duration::from_millis(400));
                    }
                }
                "stop" => {
                    match std::env::var("MOON_FAKE_STOP_MODE")
                        .unwrap_or_default()
                        .as_str()
                    {
                        "hang" => continue,
                        "crash" => return 7,
                        "save-failed" => {
                            st.phase = "stopped-save-failed".into();
                            st.save_failed = true;
                            st.ready = false;
                            let _ = state::write_state_atomic(runtime, &st);
                            return 0;
                        }
                        _ => {}
                    }
                    st.phase = "stopped".into();
                    st.ready = false;
                    revision += 1;
                    let _ = std::fs::write(
                        save.join("session.moon"),
                        format!("fake session r{revision}"),
                    );
                    st.last_save_unix_ms = state::now_ms();
                    st.save_pending = false;
                    let _ = state::write_state_atomic(runtime, &st);
                    return 0;
                }
                "fake-join" => {
                    let (id, name) = a.split_once(' ').unwrap_or((a.as_str(), "Crewmate"));
                    st.players.push(Player {
                        steam_id: id.into(),
                        name: name.into(),
                    });
                }
                "fake-leave" => st.players.retain(|p| p.steam_id != a),
                "kick" => st.players.retain(|p| p.steam_id != a),
                "fake-crash" => return 3,
                "fake-save-error" => {
                    st.save_failed = true;
                    st.error = "Injected write failure".into();
                    st.phase = "error-save".into();
                }
                "fake-save-recovered" => {
                    st.save_failed = false;
                    st.error.clear();
                    st.phase = "world-bound".into();
                }
                _ => {}
            }
        }
        if st.phase == "loading" && start.elapsed() > Duration::from_millis(900) {
            st.phase = "world-bound".into();
            st.ready = true;
        }
        if let Some(t) = save_done_at {
            if Instant::now() >= t {
                revision += 1;
                let _ = std::fs::write(
                    save.join("session.moon"),
                    format!("fake session r{revision}"),
                );
                let _ = std::fs::write(save.join("live.moon"), format!("fake live r{revision}"));
                st.save_pending = false;
                st.last_save_unix_ms = state::now_ms();
                save_done_at = None;
                if snapshot_requested {
                    let dest = runtime.join("snapshots").join(format!("fake-{revision}"));
                    std::fs::create_dir_all(&dest).unwrap();
                    for entry in std::fs::read_dir(&save).unwrap().flatten() {
                        std::fs::copy(entry.path(), dest.join(entry.file_name())).unwrap();
                    }
                    st.snapshot_dir = dest.to_string_lossy().into_owned();
                    st.snapshot_unix_ms = state::now_ms().max(st.snapshot_unix_ms + 1);
                    snapshot_requested = false;
                }
            }
        }
        st.heartbeat_unix_ms = state::now_ms();
        let _ = state::write_state_atomic(runtime, &st);
        std::thread::sleep(Duration::from_millis(100));
    }
}
