//! Regression tests for the November 14 audit fixes. Live saves are never used.
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

#[cfg(test)]
mod regressions {
    use super::*;
    use std::{fs, io::Write, path::PathBuf};
    struct Sandbox(PathBuf);
    impl Sandbox {
        fn new(name: &str) -> Self {
            let path =
                std::env::temp_dir().join(format!("moon-audit-{}-{name}", std::process::id()));
            // Unique test names isolate parallel execution. No live game path is used.
            fs::create_dir_all(&path).unwrap();
            Self(path)
        }
    }
    impl Drop for Sandbox {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    #[test]
    fn runtime_directory_is_independent_of_child_working_directory() {
        let mut s = config::Settings::default();
        s.finish().unwrap();
        let runtime = s
            .child_env()
            .into_iter()
            .find(|(k, _)| k == "MOON_DS_RUNTIME")
            .unwrap()
            .1;
        let parent = std::env::current_dir().unwrap();
        let manager = parent.join(s.runtime_dir());
        let child = parent.join(&s.game_dir).join(runtime);
        assert_eq!(manager, child);
        assert!(s.wine_prefix.is_absolute());
    }

    #[test]
    fn pruning_preserves_unrelated_archives_and_other_slots() {
        let root = Sandbox::new("prune");
        let save = root.0.join("save");
        let dest = root.0.join("backups");
        fs::create_dir_all(&save).unwrap();
        fs::create_dir_all(&dest).unwrap();
        fs::write(save.join("session.moon"), "world").unwrap();
        let unrelated = dest.join("aaa-unrelated.zip");
        fs::write(&unrelated, "unrelated user archive").unwrap();
        let other = backup::create(&save, &dest, 2, 1).unwrap();
        backup::create(&save, &dest, 1, 1).unwrap();
        assert!(unrelated.exists() && other.exists());
    }

    #[test]
    fn newest_backup_is_retained_even_in_the_same_second() {
        let root = Sandbox::new("latest-backup");
        let save = root.0.join("save");
        let dest = root.0.join("backups");
        fs::create_dir_all(&save).unwrap();
        fs::write(save.join("session.moon"), "world").unwrap();
        for _ in 0..10 {
            let created = backup::create(&save, &dest, 1, 1).unwrap();
            assert!(created.exists());
            assert_eq!(backup::list(&dest), vec![created]);
        }
    }

    #[test]
    fn corrupt_restore_preserves_the_world_and_previous_recovery() {
        let root = Sandbox::new("restore");
        let save = root.0.join("slot-1");
        let aside = root.0.join("slot-1.before-restore");
        fs::create_dir_all(&save).unwrap();
        fs::create_dir_all(&aside).unwrap();
        fs::write(save.join("session.moon"), "current world").unwrap();
        fs::write(aside.join("previous.moon"), "previous recovery").unwrap();
        let archive = root.0.join("bad.zip");
        let mut zip = zip::ZipWriter::new(fs::File::create(&archive).unwrap());
        zip.start_file(
            "session.moon",
            zip::write::SimpleFileOptions::default()
                .compression_method(zip::CompressionMethod::Stored),
        )
        .unwrap();
        zip.write_all(b"unique-world-payload").unwrap();
        zip.start_file(
            "moon-ds-backup.json",
            zip::write::SimpleFileOptions::default(),
        )
        .unwrap();
        zip.write_all(
            br#"{"schema":1,"slot":1,"created_ns":1,"sequence":1,"files":{"session.moon":20}}"#,
        )
        .unwrap();
        zip.finish().unwrap();
        let mut bytes = fs::read(&archive).unwrap();
        let pos = bytes
            .windows(20)
            .position(|p| p == b"unique-world-payload")
            .unwrap();
        bytes[pos] ^= 1; // Valid ZIP directory, invalid entry CRC: failure during extraction.
        fs::write(&archive, bytes).unwrap();
        assert!(backup::restore(&archive, &save).is_err());
        assert_eq!(
            fs::read_to_string(save.join("session.moon")).unwrap(),
            "current world"
        );
        assert_eq!(
            fs::read_to_string(aside.join("previous.moon")).unwrap(),
            "previous recovery"
        );
    }

    #[test]
    fn explicit_missing_config_and_typos_are_rejected() {
        let root = Sandbox::new("config");
        let args = vec![
            "--config".to_owned(),
            root.0
                .join("missing.properties")
                .to_string_lossy()
                .into_owned(),
        ];
        assert!(config::load(&args).is_err());
        assert!(config::Settings::default()
            .apply(&config::parse_properties("MOON_SERVER_PASSWROD=secret"))
            .is_err());
    }

    #[test]
    fn query_does_not_combine_fragments_from_different_reply_ids() {
        use std::{net::UdpSocket, time::Duration};
        let server = UdpSocket::bind("127.0.0.1:0").unwrap();
        server
            .set_read_timeout(Some(Duration::from_secs(2)))
            .unwrap();
        let address = server.local_addr().unwrap();
        let worker = std::thread::spawn(move || {
            let mut buf = [0; 1500];
            let (_, from) = server.recv_from(&mut buf).unwrap();
            server
                .send_to(&a2s::build_info(&a2s::ServerInfo::default(), 27025), from)
                .unwrap();
            let (_, from) = server.recv_from(&mut buf).unwrap();
            let normal = a2s::build_rules(&[("moon_mods".into(), "safe@1".into())]);
            let other = a2s::build_rules(&[("moon_mods".into(), "evil@1".into())]);
            let split = normal.len() - 7;
            for (id, number, body) in [(100u32, 0u8, &normal[..split]), (200, 1, &other[split..])] {
                let mut packet = vec![0xfe, 0xff, 0xff, 0xff];
                packet.extend_from_slice(&id.to_le_bytes());
                packet.extend_from_slice(&[2, number]);
                packet.extend_from_slice(&1248u16.to_le_bytes());
                packet.extend_from_slice(body);
                server.send_to(&packet, from).unwrap();
            }
        });
        let result = a2s::query(
            &address.to_string(),
            27026,
            Duration::from_millis(200),
            true,
        );
        worker.join().unwrap();
        assert!(result.is_err());
    }

    #[test]
    fn successful_restore_preserves_recovery_and_roundtrips_all_files() {
        let root = Sandbox::new("roundtrip");
        let save = root.0.join("slot-1");
        let dest = root.0.join("backups");
        fs::create_dir_all(save.join("regions")).unwrap();
        fs::write(save.join("slot.json"), "original").unwrap();
        fs::write(save.join("regions/chunk"), [0, 1, 255]).unwrap();
        let archive = backup::create(&save, &dest, 1, 1).unwrap();
        fs::write(save.join("slot.json"), "newer").unwrap();
        backup::restore(&archive, &save).unwrap();
        assert_eq!(
            fs::read_to_string(save.join("slot.json")).unwrap(),
            "original"
        );
        assert_eq!(fs::read(save.join("regions/chunk")).unwrap(), [0, 1, 255]);
        assert!(fs::read_dir(&root.0).unwrap().flatten().any(|e| e
            .file_name()
            .to_string_lossy()
            .starts_with("slot-1.before-restore-")));
    }

    #[test]
    fn exclusive_lock_survives_stale_status_and_releases_immediately() {
        let root = Sandbox::new("lock");
        let lock = state::lock_runtime(&root.0).unwrap();
        state::write_state_atomic(&root.0, &state::HostState::default()).unwrap();
        assert!(state::lock_runtime(&root.0).is_err());
        drop(lock);
        assert!(state::lock_runtime(&root.0).is_ok());
    }

    #[test]
    fn import_rejects_nested_destination_before_creating_it() {
        let root = Sandbox::new("import");
        let source = root.0.join("source");
        fs::create_dir_all(&source).unwrap();
        let target = source.join("server-copy");
        let mut stats = import::Stats {
            copied: 0,
            skipped: 0,
            bytes: 0,
        };
        assert!(import::copy_tree(&source, &target, &mut stats, &|_| {}).is_err());
        assert!(!target.exists());
    }

    #[test]
    fn browser_honors_disabled_mod_policy() {
        let q = a2s::Query {
            rules: vec![
                ("moon_policy".into(), "off".into()),
                ("moon_mods".into(), "missing.mod@1".into()),
            ],
            ..Default::default()
        };
        assert!(client::check_mods(&q.required_mods(), Some(&[])).is_empty());
    }

    #[test]
    fn legacy_backup_restores_but_empty_archive_does_not_replace_the_world() {
        let root = Sandbox::new("legacy");
        let save = root.0.join("slot-1");
        fs::create_dir_all(&save).unwrap();
        fs::write(save.join("slot.json"), "current").unwrap();
        let empty = root.0.join("moon-slot1-empty.zip");
        zip::ZipWriter::new(fs::File::create(&empty).unwrap())
            .finish()
            .unwrap();
        assert!(backup::restore(&empty, &save).is_err());
        assert_eq!(
            fs::read_to_string(save.join("slot.json")).unwrap(),
            "current"
        );
        let legacy = root.0.join("moon-slot1-20260101-000000.zip");
        let mut writer = zip::ZipWriter::new(fs::File::create(&legacy).unwrap());
        writer
            .start_file("slot.json", zip::write::SimpleFileOptions::default())
            .unwrap();
        writer.write_all(b"legacy").unwrap();
        writer.finish().unwrap();
        backup::restore(&legacy, &save).unwrap();
        assert_eq!(
            fs::read_to_string(save.join("slot.json")).unwrap(),
            "legacy"
        );
    }

    #[test]
    fn query_reassembles_out_of_order_fragments_of_one_reply() {
        use std::{net::UdpSocket, time::Duration};
        let server = UdpSocket::bind("127.0.0.1:0").unwrap();
        server
            .set_read_timeout(Some(Duration::from_secs(2)))
            .unwrap();
        let address = server.local_addr().unwrap();
        let worker = std::thread::spawn(move || {
            let mut buf = [0; 1500];
            let (_, from) = server.recv_from(&mut buf).unwrap();
            server
                .send_to(&a2s::build_info(&a2s::ServerInfo::default(), 27025), from)
                .unwrap();
            let (_, from) = server.recv_from(&mut buf).unwrap();
            let normal = a2s::build_rules(&[("moon_mods".into(), "safe@1".into())]);
            let split = normal.len() / 2;
            for (number, body) in [(1u8, &normal[split..]), (0, &normal[..split])] {
                let mut packet = vec![0xfe, 0xff, 0xff, 0xff];
                packet.extend_from_slice(&100u32.to_le_bytes());
                packet.extend_from_slice(&[2, number]);
                packet.extend_from_slice(&1248u16.to_le_bytes());
                packet.extend_from_slice(body);
                server.send_to(&packet, from).unwrap();
            }
        });
        let result = a2s::query(&address.to_string(), 27026, Duration::from_secs(2), true);
        worker.join().unwrap();
        assert_eq!(result.unwrap().rule("moon_mods"), Some("safe@1"));
    }
}

#[path = "../../../src/host_query.rs"]
mod host_query;
