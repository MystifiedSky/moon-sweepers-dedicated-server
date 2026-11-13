//! Valve server-query (A2S) client, plus a tiny responder used by the self-test.
//!
//! The dedicated server logs in to Steam as an anonymous game server with a
//! *query port*; Steam answers A2S_INFO / A2S_RULES on that port itself. The
//! info reply carries the server's SteamID, which is how a player can join with
//! just `ip:port` instead of typing a SteamID.

use std::net::{ToSocketAddrs, UdpSocket};
use std::time::{Duration, Instant};

#[derive(Clone, Debug, Default, PartialEq)]
pub struct ServerInfo {
    pub name: String,
    pub map: String,
    pub game: String,
    pub players: u8,
    pub max_players: u8,
    pub password: bool,
    pub version: String,
    pub steam_id: Option<u64>,
    pub keywords: String,
    pub ping_ms: u32,
}

#[derive(Clone, Debug, Default, PartialEq)]
pub struct Query {
    pub info: ServerInfo,
    pub rules: Vec<(String, String)>,
}

impl Query {
    pub fn rule(&self, key: &str) -> Option<&str> {
        self.rules
            .iter()
            .find(|(k, _)| k == key)
            .map(|(_, v)| v.as_str())
    }
    /// Required mods advertised by the server: `guid` or `guid@version`.
    pub fn required_mods(&self) -> Vec<(String, Option<String>)> {
        if self.rule("moon_policy") == Some("off") {
            return Vec::new();
        }
        self.rule("moon_mods")
            .unwrap_or("")
            .split(',')
            .filter(|s| !s.trim().is_empty())
            .map(|s| match s.split_once('@') {
                Some((g, v)) => (g.trim().to_string(), Some(v.trim().to_string())),
                None => (s.trim().to_string(), None),
            })
            .collect()
    }
}

const HDR: [u8; 4] = [0xFF, 0xFF, 0xFF, 0xFF];

fn info_request(challenge: Option<[u8; 4]>) -> Vec<u8> {
    let mut v = HDR.to_vec();
    v.push(b'T');
    v.extend_from_slice(b"Source Engine Query\0");
    if let Some(c) = challenge {
        v.extend_from_slice(&c);
    }
    v
}

fn rules_request(challenge: [u8; 4]) -> Vec<u8> {
    let mut v = HDR.to_vec();
    v.push(b'V');
    v.extend_from_slice(&challenge);
    v
}

struct Reader<'a> {
    b: &'a [u8],
    p: usize,
}

impl<'a> Reader<'a> {
    fn u8(&mut self) -> Option<u8> {
        let v = *self.b.get(self.p)?;
        self.p += 1;
        Some(v)
    }
    fn u16(&mut self) -> Option<u16> {
        let s = self.b.get(self.p..self.p + 2)?;
        self.p += 2;
        Some(u16::from_le_bytes([s[0], s[1]]))
    }
    fn u64(&mut self) -> Option<u64> {
        let s = self.b.get(self.p..self.p + 8)?;
        self.p += 8;
        Some(u64::from_le_bytes(s.try_into().ok()?))
    }
    fn cstr(&mut self) -> Option<String> {
        let rest = self.b.get(self.p..)?;
        let end = rest.iter().position(|&c| c == 0)?;
        let s = String::from_utf8_lossy(&rest[..end]).into_owned();
        self.p += end + 1;
        Some(s)
    }
}

pub fn parse_info(payload: &[u8]) -> Option<ServerInfo> {
    // payload starts after the FFFFFFFF header, at the 'I' byte.
    let mut r = Reader { b: payload, p: 0 };
    if r.u8()? != 0x49 {
        return None;
    }
    let _protocol = r.u8()?;
    let name = r.cstr()?;
    let map = r.cstr()?;
    let _folder = r.cstr()?;
    let game = r.cstr()?;
    let _app = r.u16()?;
    let players = r.u8()?;
    let max_players = r.u8()?;
    let _bots = r.u8()?;
    let _kind = r.u8()?;
    let _env = r.u8()?;
    let vis = r.u8()?;
    let _vac = r.u8()?;
    let version = r.cstr()?;
    let mut info = ServerInfo {
        name,
        map,
        game,
        players,
        max_players,
        password: vis == 1,
        version,
        ..Default::default()
    };
    if let Some(edf) = r.u8() {
        if edf & 0x80 != 0 {
            r.u16();
        }
        if edf & 0x10 != 0 {
            info.steam_id = r.u64();
        }
        if edf & 0x40 != 0 {
            r.u16();
            r.cstr();
        }
        if edf & 0x20 != 0 {
            info.keywords = r.cstr().unwrap_or_default();
        }
    }
    Some(info)
}

pub fn parse_rules(payload: &[u8]) -> Option<Vec<(String, String)>> {
    let mut r = Reader { b: payload, p: 0 };
    if r.u8()? != 0x45 {
        return None;
    }
    let n = r.u16()?;
    let mut out = Vec::new();
    for _ in 0..n {
        let k = r.cstr()?;
        let v = r.cstr()?;
        out.push((k, v));
    }
    (r.p == payload.len()).then_some(out)
}

/// Receive one logical reply (reassembling Source multi-packet replies) and
/// return the payload after the FFFFFFFF header.
fn recv_reply(sock: &UdpSocket, timeout: Duration) -> Option<Vec<u8>> {
    let end = Instant::now() + timeout;
    let mut buf = [0u8; 65535];
    type Fragments = (u8, u16, Vec<Option<Vec<u8>>>, usize);
    let mut replies: std::collections::HashMap<u32, Fragments> = std::collections::HashMap::new();
    loop {
        let left = end.checked_duration_since(Instant::now())?;
        sock.set_read_timeout(Some(left.max(Duration::from_millis(1))))
            .ok()?;
        let n = sock.recv(&mut buf).ok()?;
        let d = &buf[..n];
        if d.len() >= 5 && d[..4] == HDR {
            return Some(d[4..].to_vec());
        }
        if d.len() > 12 && d[..4] == [0xFE, 0xFF, 0xFF, 0xFF] {
            let id = u32::from_le_bytes([d[4], d[5], d[6], d[7]]);
            if id & 0x8000_0000 != 0 {
                return None; // compressed replies are not used by Steam game servers
            }
            let total = d[8];
            let num = d[9];
            let size = u16::from_le_bytes([d[10], d[11]]);
            if total == 0 || total > 128 || num >= total || size < 12 || d.len() > size as usize {
                return None;
            }
            if !replies.contains_key(&id) && replies.len() >= 8 {
                return None;
            }
            let entry = replies
                .entry(id)
                .or_insert_with(|| (total, size, vec![None; total as usize], 0));
            if entry.0 != total || entry.1 != size {
                return None;
            }
            let data = &d[12..];
            if let Some(previous) = &entry.2[num as usize] {
                if previous != data {
                    return None;
                }
            } else {
                entry.3 += data.len();
                if entry.3 > 1024 * 1024 {
                    return None;
                }
                entry.2[num as usize] = Some(data.to_vec());
            }
            if entry.2.iter().all(Option::is_some) {
                let all: Vec<u8> = entry
                    .2
                    .iter()
                    .flat_map(|p| p.as_ref().unwrap().iter().copied())
                    .collect();
                if all.len() >= 5 && all[..4] == HDR {
                    return Some(all[4..].to_vec());
                }
                return None;
            }
        }
    }
}

pub fn resolve(addr: &str, default_port: u16) -> Option<std::net::SocketAddr> {
    resolve_timeout(addr, default_port, Duration::from_secs(3))
}

fn resolve_timeout(
    addr: &str,
    default_port: u16,
    timeout: Duration,
) -> Option<std::net::SocketAddr> {
    let a = if addr.contains(':') && !addr.starts_with('[') && addr.matches(':').count() == 1 {
        addr.to_string()
    } else {
        format!("{addr}:{default_port}")
    };
    if let Ok(ip) = a.parse() {
        return Some(ip);
    }
    type Job = (
        String,
        std::sync::mpsc::Sender<Option<std::net::SocketAddr>>,
    );
    static RESOLVER: std::sync::OnceLock<std::sync::mpsc::SyncSender<Job>> =
        std::sync::OnceLock::new();
    let resolver = RESOLVER.get_or_init(|| {
        let (tx, rx) = std::sync::mpsc::sync_channel::<Job>(4);
        let rx = std::sync::Arc::new(std::sync::Mutex::new(rx));
        for _ in 0..4 {
            let rx = rx.clone();
            std::thread::spawn(move || loop {
                let job = rx.lock().unwrap().recv();
                let Ok((name, result)) = job else {
                    break;
                };
                let addresses: Vec<_> = name
                    .to_socket_addrs()
                    .map(|a| a.collect())
                    .unwrap_or_default();
                let answer = addresses
                    .iter()
                    .find(|a| a.is_ipv4())
                    .or(addresses.first())
                    .copied();
                let _ = result.send(answer);
            });
        }
        tx
    });
    let (tx, rx) = std::sync::mpsc::channel();
    resolver.try_send((a, tx)).ok()?;
    rx.recv_timeout(timeout).ok().flatten()
}

/// Ask a server for its details. `addr` is `host` or `host:queryport`.
pub fn query(
    addr: &str,
    default_port: u16,
    timeout: Duration,
    want_rules: bool,
) -> Result<Query, String> {
    let deadline = Instant::now() + timeout;
    let remaining = || {
        deadline
            .checked_duration_since(Instant::now())
            .ok_or("query timed out".to_string())
    };
    let target = resolve_timeout(addr, default_port, remaining()?)
        .ok_or_else(|| format!("cannot resolve '{addr}' within query deadline"))?;
    let sock = UdpSocket::bind(if target.is_ipv4() {
        "0.0.0.0:0"
    } else {
        "[::]:0"
    })
    .map_err(|e| e.to_string())?;
    sock.connect(target).map_err(|e| e.to_string())?;
    let t0 = Instant::now();
    sock.send(&info_request(None)).map_err(|e| e.to_string())?;
    let mut reply =
        recv_reply(&sock, remaining()?).ok_or("no reply (is the query UDP port open?)")?;
    if reply.first() == Some(&0x41) && reply.len() >= 5 {
        let c = [reply[1], reply[2], reply[3], reply[4]];
        sock.send(&info_request(Some(c)))
            .map_err(|e| e.to_string())?;
        reply = recv_reply(&sock, remaining()?).ok_or("no reply after challenge")?;
    }
    let ping = t0.elapsed().as_millis() as u32;
    let mut info = parse_info(&reply).ok_or("unexpected reply to the info request")?;
    info.ping_ms = ping;
    let mut rules = Vec::new();
    if want_rules {
        sock.send(&rules_request([0xFF; 4]))
            .map_err(|e| e.to_string())?;
        if let Some(r) = recv_reply(&sock, remaining()?) {
            let mut r = r;
            if r.first() == Some(&0x41) && r.len() >= 5 {
                let c = [r[1], r[2], r[3], r[4]];
                sock.send(&rules_request(c)).map_err(|e| e.to_string())?;
                r = recv_reply(&sock, remaining()?).ok_or("no rules reply after challenge")?;
            }
            rules = parse_rules(&r).ok_or("malformed rules reply")?;
        } else {
            return Err("no rules reply; requirements could not be verified".into());
        }
    }
    Ok(Query { info, rules })
}

// ---------------------------------------------------------------- responder (tests)

fn put_cstr(v: &mut Vec<u8>, s: &str) {
    v.extend_from_slice(s.as_bytes());
    v.push(0);
}

pub fn build_info(info: &ServerInfo, game_port: u16) -> Vec<u8> {
    let mut v = HDR.to_vec();
    v.push(0x49);
    v.push(17);
    put_cstr(&mut v, &info.name);
    put_cstr(&mut v, &info.map);
    put_cstr(&mut v, "moonsweepers");
    put_cstr(&mut v, &info.game);
    v.extend_from_slice(&0u16.to_le_bytes());
    v.push(info.players);
    v.push(info.max_players);
    v.push(0);
    v.push(b'd');
    v.push(b'w');
    v.push(info.password as u8);
    v.push(0);
    put_cstr(&mut v, &info.version);
    let mut edf = 0x80u8;
    if info.steam_id.is_some() {
        edf |= 0x10;
    }
    if !info.keywords.is_empty() {
        edf |= 0x20;
    }
    v.push(edf);
    v.extend_from_slice(&game_port.to_le_bytes());
    if let Some(id) = info.steam_id {
        v.extend_from_slice(&id.to_le_bytes());
    }
    if !info.keywords.is_empty() {
        put_cstr(&mut v, &info.keywords);
    }
    v
}

pub fn build_rules(rules: &[(String, String)]) -> Vec<u8> {
    let mut v = HDR.to_vec();
    v.push(0x45);
    v.extend_from_slice(&(rules.len() as u16).to_le_bytes());
    for (k, val) in rules {
        put_cstr(&mut v, k);
        put_cstr(&mut v, val);
    }
    v
}

/// Answer queries on `sock` until `stop` is set. Test helper that behaves like
/// Steam's built-in responder (challenge for rules, plain info).
pub fn respond(
    sock: UdpSocket,
    q: Query,
    game_port: u16,
    stop: std::sync::Arc<std::sync::atomic::AtomicBool>,
) {
    use std::sync::atomic::Ordering;
    sock.set_read_timeout(Some(Duration::from_millis(100))).ok();
    let mut buf = [0u8; 1500];
    while !stop.load(Ordering::Relaxed) {
        let Ok((n, from)) = sock.recv_from(&mut buf) else {
            continue;
        };
        let d = &buf[..n];
        if d.len() < 5 || d[..4] != HDR {
            continue;
        }
        match d[4] {
            b'T' => {
                let _ = sock.send_to(&build_info(&q.info, game_port), from);
            }
            b'V' => {
                if d.len() >= 9 && d[5..9] == [0xFF; 4] {
                    let mut c = HDR.to_vec();
                    c.push(0x41);
                    c.extend_from_slice(&[1, 2, 3, 4]);
                    let _ = sock.send_to(&c, from);
                } else {
                    let _ = sock.send_to(&build_rules(&q.rules), from);
                }
            }
            _ => {}
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn info_roundtrip() {
        let i = ServerInfo {
            name: "n".into(),
            map: "slot 1".into(),
            game: "Moon Sweepers".into(),
            players: 2,
            max_players: 8,
            password: true,
            version: "1.0.5".into(),
            steam_id: Some(90123456789012345),
            keywords: "pw".into(),
            ping_ms: 0,
        };
        let b = build_info(&i, 27025);
        let p = parse_info(&b[4..]).unwrap();
        assert_eq!(p.steam_id, i.steam_id);
        assert!(p.password);
        assert_eq!(p.keywords, "pw");
    }
    #[test]
    fn rules_roundtrip() {
        let r = vec![("moon_mods".to_string(), "a@1,b".to_string())];
        assert_eq!(parse_rules(&build_rules(&r)[4..]).unwrap(), r);
    }
}
