//! World backups: zip the save folder, keep the newest N, restore on request.

use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::fs::File;
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

/// `YYYYMMDD-HHMMSS` in UTC without any date library.
pub fn utc_stamp(unix_secs: u64) -> String {
    let days = (unix_secs / 86400) as i64;
    let rem = unix_secs % 86400;
    // civil-from-days (Howard Hinnant)
    let z = days + 719468;
    let era = z.div_euclid(146097);
    let doe = z.rem_euclid(146097);
    let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    let y = if m <= 2 { y + 1 } else { y };
    format!(
        "{:04}{:02}{:02}-{:02}{:02}{:02}",
        y,
        m,
        d,
        rem / 3600,
        (rem % 3600) / 60,
        rem % 60
    )
}

fn collect(dir: &Path, base: &Path, out: &mut Vec<PathBuf>) -> std::io::Result<()> {
    for e in std::fs::read_dir(dir)? {
        let p = e?.path();
        if std::fs::symlink_metadata(&p)?.file_type().is_symlink() {
            return Err(std::io::Error::other(
                "save directories must not contain links",
            ));
        } else if p.is_dir() {
            collect(&p, base, out)?;
        } else {
            out.push(p.strip_prefix(base).unwrap_or(&p).to_path_buf());
        }
    }
    Ok(())
}

const MANIFEST: &str = "moon-ds-backup.json";
const MAX_RESTORE_BYTES: u64 = 64 * 1024 * 1024 * 1024;
const MAX_MANIFEST_BYTES: usize = 4 * 1024 * 1024;
const MAX_FILES: usize = 100_000;
static SEQUENCE: AtomicU64 = AtomicU64::new(0);

#[derive(Serialize, Deserialize)]
struct Manifest {
    schema: u32,
    slot: u8,
    created_ns: u128,
    sequence: u64,
    files: BTreeMap<String, u64>,
}

fn identity() -> (u128, u64) {
    (
        SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default()
            .as_nanos(),
        SEQUENCE.fetch_add(1, Ordering::SeqCst),
    )
}

fn manifest(path: &Path) -> Option<Manifest> {
    let mut archive = zip::ZipArchive::new(File::open(path).ok()?).ok()?;
    let entry = archive.by_name(MANIFEST).ok()?;
    if entry.size() > MAX_MANIFEST_BYTES as u64 {
        return None;
    }
    let m: Manifest = serde_json::from_reader(entry).ok()?;
    (m.schema == 1 && (1..=5).contains(&m.slot) && !m.files.is_empty()).then_some(m)
}

pub fn create(save_dir: &Path, backup_dir: &Path, slot: u8, keep: u32) -> Result<PathBuf, String> {
    if !(1..=5).contains(&slot) {
        return Err("invalid backup slot".into());
    }
    if !save_dir.is_dir() {
        return Err(format!(
            "save folder '{}' does not exist yet",
            save_dir.display()
        ));
    }
    std::fs::create_dir_all(backup_dir).map_err(|e| e.to_string())?;
    let (created_ns, sequence) = identity();
    let stem = format!(
        "moon-slot{slot}-{}-{created_ns}-{}-{sequence}",
        utc_stamp((created_ns / 1_000_000_000) as u64),
        std::process::id()
    );
    let target = backup_dir.join(format!("{stem}.zip"));
    let tmp = backup_dir.join(format!("{stem}.zip.part"));
    let mut files = Vec::new();
    collect(save_dir, save_dir, &mut files).map_err(|e| e.to_string())?;
    if files.is_empty() {
        return Err("refusing to back up an empty save".into());
    }
    if files.len() > MAX_FILES {
        return Err("backup has too many entries".into());
    }
    let result = (|| -> Result<(), String> {
        let f = File::create_new(&tmp).map_err(|e| e.to_string())?;
        let mut z = zip::ZipWriter::new(f);
        let opts = zip::write::SimpleFileOptions::default()
            .compression_method(zip::CompressionMethod::Deflated);
        let mut sizes = BTreeMap::new();
        let mut total = 0u64;
        for rel in &files {
            let raw_name = rel.to_str().ok_or("save filenames must be valid UTF-8")?;
            if cfg!(unix) && raw_name.contains('\\') {
                return Err("save filename contains a nonportable backslash".into());
            }
            let name = raw_name.replace('\\', "/");
            if name == MANIFEST {
                return Err("save contains reserved backup manifest name".into());
            }
            let mut src =
                File::open(save_dir.join(rel)).map_err(|e| format!("cannot read {name}: {e}"))?;
            let before = src.metadata().map_err(|e| e.to_string())?;
            total = total
                .checked_add(before.len())
                .ok_or("backup size overflow")?;
            if total > MAX_RESTORE_BYTES {
                return Err("backup exceeds restore size limit".into());
            }
            z.start_file(&name, opts).map_err(|e| e.to_string())?;
            // Stream it: world chunks can be large and the server should not hold them in memory.
            let size = std::io::copy(
                &mut std::io::Read::by_ref(&mut src).take(before.len() + 1),
                &mut z,
            )
            .map_err(|e| e.to_string())?;
            let after = src.metadata().map_err(|e| e.to_string())?;
            if size != before.len()
                || before.len() != after.len()
                || before.modified().ok() != after.modified().ok()
            {
                return Err(format!("save changed while reading {name}; backup aborted"));
            }
            sizes.insert(name, size);
        }
        z.start_file(MANIFEST, opts).map_err(|e| e.to_string())?;
        let m = Manifest {
            schema: 1,
            slot,
            created_ns,
            sequence,
            files: sizes,
        };
        let manifest_bytes = serde_json::to_vec(&m).map_err(|e| e.to_string())?;
        if manifest_bytes.len() > MAX_MANIFEST_BYTES {
            return Err("backup manifest exceeds restore limit".into());
        }
        z.write_all(&manifest_bytes).map_err(|e| e.to_string())?;
        z.finish()
            .map_err(|e| e.to_string())?
            .sync_all()
            .map_err(|e| e.to_string())?;
        Ok(())
    })();
    if let Err(e) = result {
        let _ = std::fs::remove_file(&tmp);
        return Err(e);
    }
    std::fs::rename(&tmp, &target).map_err(|e| e.to_string())?;
    prune_slot(backup_dir, slot, keep);
    Ok(target)
}

pub fn list(backup_dir: &Path) -> Vec<PathBuf> {
    let mut v: Vec<PathBuf> = std::fs::read_dir(backup_dir)
        .map(|rd| {
            rd.filter_map(|e| e.ok())
                .map(|e| e.path())
                .filter(|p| p.extension().map(|x| x == "zip").unwrap_or(false))
                .collect()
        })
        .unwrap_or_default();
    v.sort();
    v
}

fn prune_slot(backup_dir: &Path, slot: u8, keep: u32) {
    if keep == 0 {
        return;
    }
    let prefix = format!("moon-slot{slot}-");
    let mut all: Vec<_> = list(backup_dir)
        .into_iter()
        .filter_map(|p| {
            if !p.file_name()?.to_str()?.starts_with(&prefix) {
                return None;
            }
            let m = manifest(&p)?;
            (m.slot == slot).then_some(((m.created_ns, m.sequence), p))
        })
        .collect();
    all.sort_by(|a, b| a.0.cmp(&b.0).then(a.1.cmp(&b.1)));
    if all.len() > keep as usize {
        for (_, old) in &all[..all.len() - keep as usize] {
            let _ = std::fs::remove_file(old);
        }
    }
}

/// Restore a backup into `save_dir` (the server must be stopped). The current
/// Validate and extract before modifying the live world. Retain every recovery copy.
pub fn restore(zip_path: &Path, save_dir: &Path) -> Result<(), String> {
    let f = File::open(zip_path).map_err(|e| format!("cannot open {}: {e}", zip_path.display()))?;
    let mut z = zip::ZipArchive::new(f).map_err(|e| e.to_string())?;
    let m = if let Some(m) = manifest(zip_path) {
        m
    } else {
        // Earlier kit releases did not write a manifest. Accept their named
        // archives only when they contain the known root save marker, and build
        // an expected-entry inventory before extracting anything.
        if z.by_name(MANIFEST).is_ok() {
            return Err("invalid backup manifest".into());
        }
        let name = zip_path.file_name().and_then(|n| n.to_str()).unwrap_or("");
        let suffix = name
            .strip_prefix("moon-slot")
            .ok_or("not a Moon DS backup")?;
        let slot = suffix
            .as_bytes()
            .first()
            .copied()
            .filter(|b| (b'1'..=b'5').contains(b))
            .ok_or("invalid legacy backup slot")?
            - b'0';
        if suffix.as_bytes().get(1) != Some(&b'-')
            || (z.by_name("slot.json").is_err() && z.by_name("session.moon").is_err())
        {
            return Err("legacy archive has no recognized world save".into());
        }
        let mut files = BTreeMap::new();
        for i in 0..z.len() {
            let e = z.by_index(i).map_err(|e| e.to_string())?;
            if !e.is_dir() {
                let path = e.enclosed_name().ok_or("unsafe archive path")?;
                if files
                    .insert(path.to_string_lossy().replace('\\', "/"), e.size())
                    .is_some()
                {
                    return Err("duplicate archive entry".into());
                }
            }
        }
        Manifest {
            schema: 1,
            slot,
            created_ns: 0,
            sequence: 0,
            files,
        }
    };
    if z.len() > MAX_FILES + 1 {
        return Err("backup has too many entries".into());
    }
    let (stamp, seq) = identity();
    let stage = save_dir.with_file_name(format!(
        ".moon-restore-{stamp}-{}-{seq}",
        std::process::id()
    ));
    let aside = save_dir.with_file_name(format!(
        "{}.before-restore-{stamp}-{seq}",
        save_dir
            .file_name()
            .ok_or("invalid save directory")?
            .to_string_lossy()
    ));
    std::fs::create_dir_all(save_dir.parent().ok_or("invalid save parent")?)
        .map_err(|e| e.to_string())?;
    std::fs::create_dir(&stage).map_err(|e| e.to_string())?;
    let extracted = (|| -> Result<(), String> {
        let mut found = BTreeMap::new();
        let mut total = 0u64;
        for i in 0..z.len() {
            let mut e = z.by_index(i).map_err(|e| e.to_string())?;
            let rel = e.enclosed_name().ok_or("unsafe archive path")?;
            let name = rel.to_string_lossy().replace('\\', "/");
            if e.unix_mode()
                .map(|v| v & 0o170000 == 0o120000)
                .unwrap_or(false)
            {
                return Err("archive contains links".into());
            }
            let out = stage.join(&rel);
            if name == MANIFEST {
                continue;
            }
            if e.is_dir() {
                std::fs::create_dir_all(&out).map_err(|e| e.to_string())?;
                continue;
            }
            if !m.files.contains_key(&name) || found.contains_key(&name) {
                return Err("unexpected or duplicate backup entry".into());
            }
            total = total.checked_add(e.size()).ok_or("backup size overflow")?;
            if total > MAX_RESTORE_BYTES {
                return Err("backup exceeds restore size limit".into());
            }
            if let Some(p) = out.parent() {
                std::fs::create_dir_all(p).map_err(|e| e.to_string())?;
            }
            let mut target = File::create_new(&out).map_err(|e| e.to_string())?;
            let expected_size = e.size();
            let size = std::io::copy(&mut e.by_ref().take(expected_size + 1), &mut target)
                .map_err(|e| e.to_string())?;
            if size != e.size() {
                return Err("invalid backup entry size".into());
            }
            found.insert(name, size);
        }
        if found != m.files {
            return Err("backup is incomplete".into());
        }
        Ok(())
    })();
    if let Err(e) = extracted {
        let _ = std::fs::remove_dir_all(&stage);
        return Err(e);
    }
    let had_save = save_dir.exists();
    if had_save {
        std::fs::rename(save_dir, &aside)
            .map_err(|e| format!("cannot preserve current save: {e}"))?;
    }
    if let Err(e) = std::fs::rename(&stage, save_dir) {
        if had_save {
            std::fs::rename(&aside, save_dir).map_err(|rollback| {
                format!(
                    "restore swap failed: {e}; rollback failed: {rollback}; original is {}",
                    aside.display()
                )
            })?;
        }
        let _ = std::fs::remove_dir_all(&stage);
        return Err(e.to_string());
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn stamp() {
        assert_eq!(utc_stamp(0), "19700101-000000");
        assert_eq!(utc_stamp(1_700_000_000), "20231114-221320");
    }
}
