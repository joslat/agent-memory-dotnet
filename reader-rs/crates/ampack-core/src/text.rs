//! The format's small grammars (paths, ids, prefixes, versions, WebIDs, digests) and display-safe text, written out by
//! hand as the two other verifiers' patterns read them, ASCII only.

use alloc::string::String;
use alloc::vec::Vec;

use crate::registry::Registry;

pub const MAX_VERSION: usize = 256;

fn alnum(c: u8) -> bool {
    c.is_ascii_alphanumeric()
}

/// A path segment: [A-Za-z0-9][A-Za-z0-9._-]{0,99}, not ending with a dot, its stem no device name.
fn segment(devices: &[String], p: &str) -> bool {
    let b = p.as_bytes();
    !b.is_empty()
        && b.len() <= 100
        && alnum(b[0])
        && b.iter().all(|&c| alnum(c) || c == b'.' || c == b'_' || c == b'-')
        && !p.ends_with('.')
        && {
            let stem = p.split('.').next().unwrap_or("").to_ascii_uppercase();
            !devices.iter().any(|d| *d == stem)
        }
}

/// A pack path (Core 5): 1 to 255 characters, at most 8 segments, each safe.
pub fn safe_path(devices: &[String], p: &str) -> bool {
    let n = p.chars().count();
    n > 0 && n <= 255 && p.split('/').count() <= 8 && p.split('/').all(|s| segment(devices, s))
}

/// One id segment: [a-z][a-z0-9]*(-[a-z0-9]+)*.
fn id_segment(s: &str) -> bool {
    let b = s.as_bytes();
    !b.is_empty()
        && b[0].is_ascii_lowercase()
        && b.iter().all(|&c| c.is_ascii_lowercase() || c.is_ascii_digit() || c == b'-')
        && !s.ends_with('-')
        && !s.contains("--")
}

/// A pack id: three or more id segments joined by dots, at most 64 characters.
pub fn valid_id(s: &str) -> bool {
    s.len() <= 64 && s.split('.').count() >= 3 && s.split('.').all(id_segment)
}

/// The id grammar alone (no length bound), as META/files.json is first read.
pub fn id_grammar(s: &str) -> bool {
    s.split('.').count() >= 3 && s.split('.').all(id_segment)
}

/// A pack id prefix: one or more id segments, optionally ending with a dot.
pub fn valid_prefix(s: &str) -> bool {
    let body = s.strip_suffix('.').unwrap_or(s);
    !body.is_empty() && body.split('.').all(id_segment)
}

fn numeric(s: &str) -> bool {
    !s.is_empty() && s.bytes().all(|c| c.is_ascii_digit()) && (s == "0" || !s.starts_with('0'))
}

fn ident(s: &str) -> bool {
    !s.is_empty() && s.bytes().all(|c| alnum(c) || c == b'-')
}

/// A SemVer 2.0 version (R-CORE-036): ASCII, at most 256 characters, each numeric part within 64 bits, no leading zero in a
/// numeric prerelease identifier.
pub fn valid_version(s: &str) -> bool {
    if s.chars().count() > MAX_VERSION || !s.is_ascii() {
        return false;
    }
    let (rest, build) = match s.split_once('+') {
        Some((a, b)) => (a, Some(b)),
        None => (s, None),
    };
    if let Some(b) = build {
        if !b.split('.').all(ident) {
            return false;
        }
    }
    let (core, pre) = match rest.split_once('-') {
        Some((a, b)) => (a, Some(b)),
        None => (rest, None),
    };
    let parts: Vec<&str> = core.split('.').collect();
    if parts.len() != 3 || !parts.iter().all(|p| numeric(p)) {
        return false;
    }
    if parts.iter().any(|p| p.len() > 19 || p.parse::<u64>().map_or(true, |v| v > i64::MAX as u64)) {
        return false;
    }
    if let Some(pre) = pre {
        if !pre.split('.').all(ident) {
            return false;
        }
        if pre.split('.').any(|p| p.len() > 1 && p.starts_with('0') && p.bytes().all(|c| c.is_ascii_digit())) {
            return false;
        }
    }
    true
}

/// Exactly `n` lowercase hexadecimal characters.
pub fn hex_n(s: &str, n: usize) -> bool {
    s.len() == n && s.bytes().all(|c| c.is_ascii_digit() || (b'a'..=b'f').contains(&c))
}

/// sha256: and 64 lowercase hex.
pub fn is_digest(s: &str) -> bool {
    s.strip_prefix("sha256:").is_some_and(|h| hex_n(h, 64))
}

/// A commit: 7 to 64 lowercase hex.
pub fn is_commit(s: &str) -> bool {
    (7..=64).contains(&s.len()) && s.bytes().all(|c| c.is_ascii_digit() || (b'a'..=b'f').contains(&c))
}

fn dns_host(h: &str) -> bool {
    !h.is_empty()
        && h.split('.').all(|l| {
            let b = l.as_bytes();
            !b.is_empty() && alnum(b[0]) && alnum(b[b.len() - 1]) && b.iter().all(|&c| alnum(c) || c == b'-')
        })
}

fn path_char(c: u8) -> bool {
    alnum(c) || b"-._~:/?#@!$&'()*+,;=%".contains(&c)
}

/// One WebID grammar for every verifier (federated packs 3): https and a DNS host, or http for a loopback host; an optional
/// port; a path of characters that need no escaping; at most one '#'; at most 512 characters.
pub fn valid_webid(s: &str) -> bool {
    if s.is_empty() || s.chars().count() > 512 || s.matches('#').count() > 1 || !s.is_ascii() {
        return false;
    }
    let after_host = if let Some(rest) = s.strip_prefix("https://") {
        let end = rest.find(|c: char| !(c.is_ascii_alphanumeric() || c == '.' || c == '-')).unwrap_or(rest.len());
        if !dns_host(&rest[..end]) {
            return false;
        }
        &rest[end..]
    } else if let Some(rest) = s.strip_prefix("http://") {
        match ["localhost", "127.0.0.1", "[::1]"].iter().find(|h| rest.starts_with(**h)) {
            Some(h) => &rest[h.len()..],
            None => return false,
        }
    } else {
        return false;
    };
    let path = if let Some(p) = after_host.strip_prefix(':') {
        let digits = p.bytes().take_while(|c| c.is_ascii_digit()).count();
        if !(1..=5).contains(&digits) {
            return false;
        }
        &p[digits..]
    } else {
        after_host
    };
    path.starts_with('/') && path.bytes().all(path_char)
}

/// Unicode's White_Space property (stable since 6.3).
fn white_space(c: char) -> bool {
    let u = c as u32;
    (0x09..0x0E).contains(&u)
        || matches!(u, 0x20 | 0x85 | 0xA0 | 0x1680 | 0x2028 | 0x2029 | 0x202F | 0x205F | 0x3000)
        || (0x2000..0x200B).contains(&u)
}

fn line_break(c: char) -> bool {
    matches!(c, '\n' | '\t' | '\u{2028}' | '\u{2029}')
}

impl Registry {
    /// No code point the registry refuses for display (fixed at Unicode 15.0).
    pub fn display_safe(&self, s: &str) -> bool {
        s.chars().all(|c| {
            let u = c as u32;
            let i = self.display_refused.partition_point(|&(a, _)| a <= u);
            !(i > 0 && u <= self.display_refused[i - 1].1)
        })
    }

    /// Display text: 1 to `maximum` characters, display-safe, not blank; for one line no line feed, tab, line or paragraph
    /// separator.
    pub fn is_display_text(&self, s: &str, maximum: usize, line: bool) -> bool {
        let n = s.chars().count();
        (1..=maximum).contains(&n) && self.display_safe(s) && s.chars().any(|c| !white_space(c)) && !(line && s.chars().any(line_break))
    }
    pub fn is_text(&self, v: Option<&crate::json::Value>, maximum: usize) -> bool {
        v.and_then(|v| v.as_str()).is_some_and(|s| self.is_display_text(s, maximum, false))
    }
    pub fn is_line(&self, v: Option<&crate::json::Value>, maximum: usize) -> bool {
        v.and_then(|v| v.as_str()).is_some_and(|s| self.is_display_text(s, maximum, true))
    }
}

/// A value from a pack as it is printed: control and format characters escaped, so pack text cannot forge or hide a line.
pub fn shown(s: &str) -> String {
    use core::fmt::Write;
    let mut out = String::new();
    for c in s.chars() {
        let u = c as u32;
        let control = u < 0x20 || (0x7F..0xA0).contains(&u);
        let format = matches!(u, 0xAD | 0x600..=0x605 | 0x61C | 0x6DD | 0x70F | 0x180E | 0x200B..=0x200F | 0x202A..=0x202E
            | 0x2060..=0x2064 | 0x2066..=0x206F | 0xFEFF | 0xFFF9..=0xFFFB | 0xE0001 | 0xE0020..=0xE007F);
        if control || format {
            let _ = write!(out, "\\u{u:04X}");
        } else {
            out.push(c);
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn paths() {
        let reg = Registry::load().unwrap();
        let safe_path = |p: &str| super::safe_path(&reg.devices, p);
        assert!(safe_path("META/files.json"));
        assert!(safe_path("records/a_b-c.1.jsonl"));
        assert!(!safe_path(""));
        assert!(!safe_path("a//b"));
        assert!(!safe_path(".hidden"));
        assert!(!safe_path("a./b"));
        assert!(!safe_path("aux.json"));
        assert!(!safe_path("x/COM1"));
        assert!(!safe_path("a/b/c/d/e/f/g/h/i"));
        assert!(!safe_path("caf\u{e9}"));
    }

    #[test]
    fn ids_prefixes_versions() {
        assert!(valid_id("io.example.notes"));
        assert!(!valid_id("io.example"));
        assert!(!valid_id("io.Example.notes"));
        assert!(!valid_id("io.ex--ample.notes"));
        assert!(!valid_id("io.example-.notes"));
        assert!(valid_prefix("io.example."));
        assert!(valid_prefix("io"));
        assert!(!valid_prefix("."));
        assert!(valid_version("1.0.0"));
        assert!(valid_version("1.0.0-alpha.1+build.5"));
        assert!(!valid_version("1.0.0-01"));
        assert!(!valid_version("01.0.0"));
        assert!(!valid_version("1.0"));
        assert!(!valid_version("9223372036854775808.0.0"));
        assert!(valid_version("9223372036854775807.0.0"));
        assert!(!valid_version("1.0.0-"));
        assert!(!valid_version("1.0.0+"));
    }

    #[test]
    fn webids() {
        assert!(valid_webid("https://alice.example/profile/card#me"));
        assert!(valid_webid("http://localhost:3000/x"));
        assert!(!valid_webid("http://example.org/x"));
        assert!(!valid_webid("https://-a.example/"));
        assert!(!valid_webid("https://a.example"));
        assert!(!valid_webid("https://a.example/#a#b"));
        assert!(!valid_webid("https://a.example:123456/"));
        assert!(!valid_webid("https://a.example/ x"));
    }
}
