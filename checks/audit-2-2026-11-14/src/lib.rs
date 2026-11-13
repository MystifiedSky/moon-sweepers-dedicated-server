//! Desired-behavior regressions for the second audit findings.
#![allow(dead_code)]
#[path = "../../../src/a2s.rs"]
mod a2s;
#[path = "../../../src/backup.rs"]
mod backup;
#[path = "../../../src/client.rs"]
mod client;
#[path = "../../../src/config.rs"]
mod config;
#[path = "../../../src/import.rs"]
mod import;
#[path = "../../../src/state.rs"]
mod state;
#[path = "../../../src/supervisor.rs"]
mod supervisor;

#[path = "../../../src/query_jobs.rs"]
mod query_jobs;

#[cfg(test)]
mod probes {
    use super::*;
    use std::{
        fs,
        path::PathBuf,
        sync::{
            atomic::{AtomicBool, Ordering},
            Arc, Mutex,
        },
        time::{Duration, Instant},
    };
    struct Sandbox(PathBuf);
    impl Sandbox {
        fn new(name: &str) -> Self {
            let dir =
                std::env::temp_dir().join(format!("moon-audit2-{}-{name}", std::process::id()));
            fs::create_dir_all(&dir).unwrap();
            Self(dir)
        }
    }
    impl Drop for Sandbox {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    #[test]
    fn import_replaces_wrong_content_with_equal_metadata() {
        let root = Sandbox::new("import");
        let from = root.0.join("from");
        let to = root.0.join("to");
        fs::create_dir_all(&from).unwrap();
        fs::create_dir_all(&to).unwrap();
        fs::write(from.join("part.dll"), "newer").unwrap();
        fs::write(to.join("part.dll"), "stale").unwrap();
        let times = fs::FileTimes::new()
            .set_modified(std::time::UNIX_EPOCH + Duration::from_secs(1_700_000_000));
        fs::File::options()
            .write(true)
            .open(from.join("part.dll"))
            .unwrap()
            .set_times(times)
            .unwrap();
        fs::File::options()
            .write(true)
            .open(to.join("part.dll"))
            .unwrap()
            .set_times(times)
            .unwrap();
        let mut stats = import::Stats {
            copied: 0,
            skipped: 0,
            bytes: 0,
        };
        import::copy_tree(&from, &to, &mut stats, &|_| {}).unwrap();
        assert_eq!(fs::read_to_string(to.join("part.dll")).unwrap(), "newer");
        assert_eq!(stats.copied, 1);
    }

    #[test]
    fn extra_arguments_preserve_quoted_values() {
        let mut settings = config::Settings::default();
        settings
            .apply(&std::collections::BTreeMap::from([(
                "MOON_EXTRA_ARGS".into(),
                "--custom-path \"two words\"".into(),
            )]))
            .unwrap();
        assert_eq!(settings.extra_args, ["--custom-path", "two words"]);
    }

    #[test]
    fn truncated_rule_value_is_rejected() {
        let mut response = a2s::build_rules(&[("moon_mods".into(), "required.mod@1".into())]);
        response.pop();
        assert!(a2s::parse_rules(&response[4..]).is_none());
    }

    #[test]
    fn shared_world_lock_spans_slots_and_releases() {
        let mut first = config::Settings {
            data_dir: "instance-a".into(),
            ..Default::default()
        };
        let mut second = config::Settings {
            data_dir: "instance-b".into(),
            ..Default::default()
        };
        first.finish().unwrap();
        second.finish().unwrap();
        assert_ne!(first.runtime_dir(), second.runtime_dir());
        assert_ne!(supervisor::guess_save_dir(&first),supervisor::guess_save_dir(&second));
        let root = Sandbox::new("world-lock");
        let first = state::lock_world(&root.0.join("slot-1")).unwrap();
        assert!(state::lock_world(&root.0.join("slot-2")).is_err());
        drop(first);
        assert!(state::lock_world(&root.0.join("slot-2")).is_ok());
    }

    #[test]
    fn corrupted_bookmark_store_is_retained_and_save_refused() {
        let root = Sandbox::new("store");
        let previous = std::env::var_os("MOON_DS_HOME");
        std::env::set_var("MOON_DS_HOME", &root.0);
        fs::write(
            root.0.join("servers.json"),
            "{\"servers\":[{\"name\":\"lost server\"",
        )
        .unwrap();
        let store = client::Store::load();
        let empty = store.servers.is_empty();
        let save_error = store.save().is_err();
        let overwritten = !fs::read_to_string(root.0.join("servers.json"))
            .unwrap()
            .contains("lost server");
        if let Some(value) = previous {
            std::env::set_var("MOON_DS_HOME", value);
        } else {
            std::env::remove_var("MOON_DS_HOME");
        }
        assert!(empty && !overwritten && save_error && store.load_error.is_some());
    }

    fn run_probe(name: &str, command: &str) -> Vec<String> {
        let root = Sandbox::new(name);
        let mut settings = config::Settings {
            data_dir: root.0.clone(),
            backup_on_stop: false,
            autosave_minutes: 0,
            ..Default::default()
        };
        settings.finish().unwrap();
        let logs = Arc::new(Mutex::new(Vec::new()));
        let capture = logs.clone();
        let exe = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join(format!(
            "target/debug/audit-game{}",
            std::env::consts::EXE_SUFFIX
        ));
        assert!(exe.exists(), "First run cargo build --bin audit-game");
        let manager = supervisor::Supervisor {
            s: settings.clone(),
            launch: supervisor::Launch::Fake(exe),
            sink: Arc::new(move |s| capture.lock().unwrap().push(s)),
        };
        let stop = Arc::new(AtomicBool::new(false));
        let stop_child = stop.clone();
        let (tx, rx) = std::sync::mpsc::channel();
        let worker = std::thread::spawn(move || manager.run(rx, stop_child));
        let start = Instant::now();
        while start.elapsed() < Duration::from_secs(5)
            && !logs.lock().unwrap().iter().any(|s| s.contains("READY"))
        {
            std::thread::sleep(Duration::from_millis(20));
        }
        if command == "backup" {
            tx.send("backup".into()).unwrap();
        } else {
            state::send_command(&settings.runtime_dir(), command).unwrap();
        }
        std::thread::sleep(Duration::from_millis(1000));
        stop.store(true, Ordering::SeqCst);
        let code = worker.join().unwrap();
        assert_eq!(code, 0);
        Arc::try_unwrap(logs).unwrap().into_inner().unwrap()
    }

    #[test]
    fn backup_retry_ignores_previous_snapshot_error() {
        let logs = run_probe("snapshot", "backup");
        assert!(!logs.iter().any(|s| s.contains("ERROR snapshot did not finish")));
        assert!(logs.iter().any(|s| s.contains("BACKUP ")));
    }

    #[test]
    fn player_name_cannot_inject_console_events() {
        let logs = run_probe("name", "inject-name");
        assert!(!logs.iter().any(|s| s.contains('\n') || s.contains('\r')));
        assert!(logs.iter().any(|s| s.contains("A\\n[moon-ds] READY")));
    }

    #[test]
    #[ignore = "Creates ten thousand small files to exercise the aggregate manifest limit"]
    fn backup_rejects_an_unrestorable_manifest() {
        let root = Sandbox::new("manifest");
        let save = root.0.join("save");
        fs::create_dir_all(&save).unwrap();
        for n in 0..10000 {
            fs::write(save.join(format!("{}-{n:05}", "界".repeat(150))), []).unwrap();
        }
        let error = backup::create(&save, &root.0.join("backups"), 1, 1).unwrap_err();
        assert!(error.contains("manifest exceeds restore limit"));
        assert!(fs::read_dir(root.0.join("backups")).unwrap().next().is_none());
    }
}

#[path = "../../../src/host_query.rs"]
mod host_query;

#[cfg(test)] mod options {
    use super::config;
    #[test] fn positional_options_consume_values_and_reject_extras() {
        for text in [vec!["--config","settings file.properties","save.zip"],vec!["save.zip","--config","settings file.properties"],vec!["--config=settings file.properties","save.zip"]] {
            let args=text.into_iter().map(String::from).collect::<Vec<_>>();
            let (file,opts)=config::positional_options(&args).unwrap();assert_eq!(file,"save.zip");assert!(opts[0].starts_with("--config"));
        }
        assert!(config::positional_options(&["a".into(),"b".into()]).is_err());
        assert!(config::positional_options(&["--config".into()]).is_err());
    }
    #[test] fn quoted_arguments_preserve_windows_paths_and_empty_arguments() {
        assert_eq!(config::split_extra_args(r#"-folder "C:\My Games\Moon" '' """#).unwrap(),["-folder",r"C:\My Games\Moon","",""]);
        assert_eq!(config::split_extra_args(r#""C:\Moon\\""#).unwrap(),[r"C:\Moon\"]);
        assert!(config::split_extra_args("'unfinished").is_err());
    }
}
