//! `moon-ds import <from>`: copy an installed game folder (with BepInEx) to the server's game folder.
//! Plain file copy; skips files that are already up to date and the game's own logs, saves and crash reports.

use std::path::Path;

pub struct Stats {
    pub copied: u64,
    pub skipped: u64,
    pub bytes: u64,
}

fn same(a: &Path, b: &Path) -> bool {
    use std::io::Read;
    let compare = || -> std::io::Result<bool> {
        let mut left = std::io::BufReader::new(std::fs::File::open(a)?);
        let mut right = std::io::BufReader::new(std::fs::File::open(b)?);
        if left.get_ref().metadata()?.len() != right.get_ref().metadata()?.len() {
            return Ok(false);
        }
        let mut x = [0; 65536];
        let mut y = [0; 65536];
        loop {
            let n = left.read(&mut x)?;
            right.read_exact(&mut y[..n])?;
            if x[..n] != y[..n] {
                return Ok(false);
            }
            if n == 0 {
                return Ok(true);
            }
        }
    };
    compare().unwrap_or(false)
}

fn skip(name: &str) -> bool {
    let n = name.to_ascii_lowercase();
    n == "crash reports" || n == "moonsessions" || n == "backups" || n.ends_with(".log")
}

pub fn copy_tree(
    from: &Path,
    to: &Path,
    st: &mut Stats,
    say: &dyn Fn(&str),
) -> std::io::Result<()> {
    let source = from.canonicalize()?;
    // Resolve existing ancestors, including directory links, before creating anything.
    fn resolved(path: &Path) -> std::io::Result<std::path::PathBuf> {
        if path.exists() {
            return path.canonicalize();
        }
        let absolute = std::path::absolute(path)?;
        let parent = absolute
            .parent()
            .ok_or_else(|| std::io::Error::other("invalid import target"))?;
        Ok(resolved(parent)?.join(
            absolute
                .file_name()
                .ok_or_else(|| std::io::Error::other("invalid import target"))?,
        ))
    }
    let target = resolved(to)?;
    if target.starts_with(&source) || source.starts_with(&target) {
        return Err(std::io::Error::other(
            "import source and destination must be separate directory trees",
        ));
    }
    copy_contents(&source, &target, st, say)
}

fn copy_contents(
    from: &Path,
    to: &Path,
    st: &mut Stats,
    say: &dyn Fn(&str),
) -> std::io::Result<()> {
    std::fs::create_dir_all(to)?;
    for e in std::fs::read_dir(from)? {
        let e = e?;
        let name = e.file_name().to_string_lossy().into_owned();
        if skip(&name) {
            continue;
        }
        let src = e.path();
        let dst = to.join(&name);
        if e.file_type()?.is_symlink() && src.is_dir() {
            return Err(std::io::Error::other(
                "import does not follow directory links",
            ));
        }
        if name.eq_ignore_ascii_case("moon-ds")
            && from
                .file_name()
                .map(|n| n.to_string_lossy().eq_ignore_ascii_case("BepInEx"))
                .unwrap_or(false)
        {
            continue;
        }
        if src.is_dir() {
            copy_contents(&src, &dst, st, say)?;
        } else if same(&src, &dst) {
            st.skipped += 1;
        } else {
            let n = std::fs::copy(&src, &dst)?;
            st.copied += 1;
            st.bytes += n;
            if st.copied.is_multiple_of(200) {
                say(&format!(
                    "copied {} files ({} MB)...",
                    st.copied,
                    st.bytes / 1_000_000
                ));
            }
        }
    }
    Ok(())
}
