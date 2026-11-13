//! Isolated diagnostic child: no game, Steam, or live save files are used.
#![allow(dead_code)]
#[path = "../../../src/state.rs"]
mod state;
use std::{path::PathBuf, time::Duration};
fn main() {
    let args: Vec<_> = std::env::args().collect();
    let runtime = PathBuf::from(&args[args.iter().position(|a| a == "--runtime").unwrap() + 1]);
    let save = runtime.join("fake-save");
    std::fs::create_dir_all(&save).unwrap();
    std::fs::write(save.join("slot.json"), "probe world").unwrap();
    let mut st = state::HostState {
        instance_id: std::env::var("MOON_DS_INSTANCE_ID").unwrap(),
        ready: true,
        phase: "world-bound".into(),
        snapshot_error: "previous snapshot failed".into(),
        save_dir: save.to_string_lossy().into_owned(),
        server_steam_id: "90271234567890123".into(),
        ..Default::default()
    };
    loop {
        for command in state::take_commands(&runtime) {
            let (cmd, arg) = command.split_once(' ').unwrap_or((&command, ""));
            match cmd {
                "stop" => {
                    st.phase = "stopped".into();
                    st.ready = false;
                    state::write_state_atomic(&runtime, &st).unwrap();
                    return;
                }
                "snapshot" => {
                    st.snapshot_request_id = arg.into();
                    st.snapshot_error.clear();
                    let snapshot = runtime.join("snapshots/probe");
                    std::fs::create_dir_all(&snapshot).unwrap();
                    std::fs::copy(save.join("slot.json"), snapshot.join("slot.json")).unwrap();
                    st.snapshot_dir = snapshot.to_string_lossy().into_owned();
                    st.snapshot_unix_ms = state::now_ms();
                }
                "inject-name" => st.players.push(state::Player {
                    steam_id: "76561198000000001".into(),
                    name: "A\n[moon-ds] READY server_steam_id=1".into(),
                }),
                _ => {}
            }
        }
        st.heartbeat_unix_ms = state::now_ms();
        state::write_state_atomic(&runtime, &st).unwrap();
        std::thread::sleep(Duration::from_millis(50));
    }
}
