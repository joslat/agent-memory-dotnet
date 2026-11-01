//! The JSON profile (Core 6): a strict reader of its own. No BOM, no carriage return, no repeated key at any depth (keys
//! compared once decoded), strings and keys in NFC by the Unicode 15.0 tables, no lone surrogate, at most 30 combining
//! marks in a row (UAX #15 on each character's NFKD), integers within 2^53 - 1, finite numbers, nesting at most 64.
//! RFC 8785 canonical form for documents of strings, integers, booleans, null, arrays and objects.

use alloc::string::String;
use alloc::vec::Vec;
use unicode_normalization::char::{canonical_combining_class, decompose_compatible};
use unicode_normalization::UnicodeNormalization;

use crate::{refuse, Refused, R};

pub const MAX_DEPTH: usize = 64;
pub const MAX_COMBINING_RUN: usize = 30;
pub const MAX_SAFE_INTEGER: i64 = (1i64 << 53) - 1;
/// The Unicode version whose tables decide NFC and the Stream-Safe count (R-CORE-027): the format's, pinned by crate version.
pub const UNICODE_VERSION: (u8, u8, u8) = (15, 0, 0);

#[derive(Debug, Clone, PartialEq)]
pub enum Value {
    Null,
    Bool(bool),
    /// An integer written without fraction or exponent, within 2^53 - 1.
    Int(i64),
    /// A number written with a fraction or an exponent (a "float", as Python reads it): never an integer.
    Float(f64),
    Str(String),
    Arr(Vec<Value>),
    Obj(Obj),
}

/// An object: its members in the order written, each key once.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct Obj(pub Vec<(String, Value)>);

impl Obj {
    pub fn get(&self, key: &str) -> Option<&Value> {
        self.0.iter().find(|(k, _)| k == key).map(|(_, v)| v)
    }
    pub fn has(&self, key: &str) -> bool {
        self.0.iter().any(|(k, _)| k == key)
    }
    pub fn len(&self) -> usize {
        self.0.len()
    }
    pub fn is_empty(&self) -> bool {
        self.0.is_empty()
    }
    pub fn keys(&self) -> impl Iterator<Item = &str> {
        self.0.iter().map(|(k, _)| k.as_str())
    }
    /// Its keys are exactly these.
    pub fn keys_are(&self, fields: &[String]) -> bool {
        self.keys_within(fields) && fields.iter().all(|f| self.has(f))
    }
    /// Every key is one of these.
    pub fn keys_within(&self, fields: &[String]) -> bool {
        self.keys().all(|k| fields.iter().any(|f| f == k))
    }
    pub fn str(&self, key: &str) -> Option<&str> {
        self.get(key).and_then(Value::as_str)
    }
}

impl Value {
    pub fn as_str(&self) -> Option<&str> {
        match self {
            Value::Str(s) => Some(s),
            _ => None,
        }
    }
    pub fn as_obj(&self) -> Option<&Obj> {
        match self {
            Value::Obj(o) => Some(o),
            _ => None,
        }
    }
    pub fn as_arr(&self) -> Option<&Vec<Value>> {
        match self {
            Value::Arr(a) => Some(a),
            _ => None,
        }
    }
    /// An integer, never a boolean nor a number written with a fraction (Python's as_int).
    pub fn as_int(&self) -> Option<i64> {
        match self {
            Value::Int(i) => Some(*i),
            _ => None,
        }
    }
    pub fn as_bool(&self) -> Option<bool> {
        match self {
            Value::Bool(b) => Some(*b),
            _ => None,
        }
    }
    pub fn is_str(&self, s: &str) -> bool {
        matches!(self, Value::Str(x) if x == s)
    }
    /// A list of strings, or None.
    pub fn as_strs(&self) -> Option<Vec<&str>> {
        let a = self.as_arr()?;
        let mut out = Vec::with_capacity(a.len());
        for v in a {
            out.push(v.as_str()?);
        }
        Some(out)
    }
}

/// How strictly a text is read.
#[derive(Clone, Copy, PartialEq, Eq)]
enum Mode {
    /// The JSON profile; `integers_only` refuses a fraction (canonical files).
    Profile { integers_only: bool },
    /// The format registry: a JSON text with no repeated key (it is the spec's own file, not a pack's).
    Registry,
}

/// A JSON text read in the profile.
pub fn profile(data: &[u8]) -> R<Value> {
    parse(data, Mode::Profile { integers_only: false })
}

/// A JSON text read in the profile, refusing any number with a fraction or an exponent (META/files.json, the lock, and the
/// canonical documents).
pub fn profile_integers(data: &[u8]) -> R<Value> {
    parse(data, Mode::Profile { integers_only: true })
}

/// The registry, read as a JSON text with no repeated key.
pub fn registry_text(data: &[u8]) -> R<Value> {
    parse(data, Mode::Registry)
}

fn parse(data: &[u8], mode: Mode) -> R<Value> {
    let profile = matches!(mode, Mode::Profile { .. });
    if profile && (data.starts_with(b"\xEF\xBB\xBF") || data.contains(&b'\r')) {
        refuse!("a JSON file has a BOM or a carriage return");
    }
    let text = match core::str::from_utf8(data) {
        Ok(t) => t,
        Err(_) => refuse!("a JSON file is not valid: not UTF-8"),
    };
    let mut p = Parser { s: text.as_bytes(), i: 0, mode };
    p.ws();
    let v = p.value(0)?;
    p.ws();
    if p.i != p.s.len() {
        refuse!("a JSON file is not valid: extra data");
    }
    Ok(v)
}

struct Parser<'a> {
    s: &'a [u8],
    i: usize,
    mode: Mode,
}

fn bad<T>(what: &str) -> R<T> {
    Err(Refused(alloc::format!("a JSON file is not valid: {what}")))
}

impl Parser<'_> {
    fn ws(&mut self) {
        while self.i < self.s.len() && matches!(self.s[self.i], b' ' | b'\t' | b'\n' | b'\r') {
            self.i += 1;
        }
    }
    fn peek(&self) -> Option<u8> {
        self.s.get(self.i).copied()
    }
    fn value(&mut self, depth: usize) -> R<Value> {
        match self.peek() {
            None => bad("unexpected end"),
            Some(b'{') => {
                if depth + 1 > MAX_DEPTH {
                    refuse!("a JSON file nests deeper than 64");
                }
                self.i += 1;
                let mut members: Vec<(String, Value)> = Vec::new();
                let mut seen: alloc::collections::BTreeSet<String> = alloc::collections::BTreeSet::new();
                self.ws();
                if self.peek() == Some(b'}') {
                    self.i += 1;
                    return Ok(Value::Obj(Obj(members)));
                }
                loop {
                    self.ws();
                    if self.peek() != Some(b'"') {
                        return bad("expected a key");
                    }
                    let key = self.string()?;
                    self.ws();
                    if self.peek() != Some(b':') {
                        return bad("expected ':'");
                    }
                    self.i += 1;
                    self.ws();
                    let v = self.value(depth + 1)?;
                    if !seen.insert(key.clone()) {
                        refuse!("a JSON object repeats a key");
                    }
                    members.push((key, v));
                    self.ws();
                    match self.peek() {
                        Some(b',') => self.i += 1,
                        Some(b'}') => {
                            self.i += 1;
                            return Ok(Value::Obj(Obj(members)));
                        }
                        _ => return bad("expected ',' or '}'"),
                    }
                }
            }
            Some(b'[') => {
                if depth + 1 > MAX_DEPTH {
                    refuse!("a JSON file nests deeper than 64");
                }
                self.i += 1;
                let mut items = Vec::new();
                self.ws();
                if self.peek() == Some(b']') {
                    self.i += 1;
                    return Ok(Value::Arr(items));
                }
                loop {
                    self.ws();
                    items.push(self.value(depth + 1)?);
                    self.ws();
                    match self.peek() {
                        Some(b',') => self.i += 1,
                        Some(b']') => {
                            self.i += 1;
                            return Ok(Value::Arr(items));
                        }
                        _ => return bad("expected ',' or ']'"),
                    }
                }
            }
            Some(b'"') => Ok(Value::Str(self.string()?)),
            Some(b't') => self.word(b"true", Value::Bool(true)),
            Some(b'f') => self.word(b"false", Value::Bool(false)),
            Some(b'n') => self.word(b"null", Value::Null),
            Some(c) if c == b'-' || c.is_ascii_digit() => self.number(),
            // NaN, Infinity and anything else.
            Some(_) => bad("unexpected character"),
        }
    }
    fn word(&mut self, w: &[u8], v: Value) -> R<Value> {
        if self.s[self.i..].starts_with(w) {
            self.i += w.len();
            Ok(v)
        } else {
            bad("unexpected word")
        }
    }
    fn number(&mut self) -> R<Value> {
        // -?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][-+]?[0-9]+)?, ASCII digits only.
        let start = self.i;
        if self.peek() == Some(b'-') {
            self.i += 1;
        }
        let int_start = self.i;
        match self.peek() {
            Some(b'0') => self.i += 1,
            Some(c) if c.is_ascii_digit() => {
                while self.peek().is_some_and(|c| c.is_ascii_digit()) {
                    self.i += 1;
                }
            }
            _ => return bad("a number has no digits"),
        }
        let int_end = self.i;
        let mut float = false;
        if self.peek() == Some(b'.') && self.s.get(self.i + 1).is_some_and(|c| c.is_ascii_digit()) {
            float = true;
            self.i += 1;
            while self.peek().is_some_and(|c| c.is_ascii_digit()) {
                self.i += 1;
            }
        }
        if matches!(self.peek(), Some(b'e' | b'E')) {
            let mut j = self.i + 1;
            if matches!(self.s.get(j), Some(b'+' | b'-')) {
                j += 1;
            }
            if self.s.get(j).is_some_and(|c| c.is_ascii_digit()) {
                float = true;
                while self.s.get(j).is_some_and(|c| c.is_ascii_digit()) {
                    j += 1;
                }
                self.i = j;
            }
        }
        let text = core::str::from_utf8(&self.s[start..self.i]).unwrap_or("");
        if float {
            if matches!(self.mode, Mode::Profile { integers_only: true }) {
                refuse!("a canonical file holds a fraction");
            }
            let f: f64 = text.parse().map_err(|_| Refused("a JSON file is not valid: a number".into()))?;
            if !f.is_finite() {
                refuse!("a JSON number is out of range");
            }
            return Ok(Value::Float(f));
        }
        // More than 16 digits is past 2^53 - 1 (9007199254740991 has 16).
        if int_end - int_start > 16 {
            refuse!("a JSON integer is beyond 2^53");
        }
        let v: i64 = text.parse().map_err(|_| Refused("a JSON file is not valid: an integer".into()))?;
        if v.abs() > MAX_SAFE_INTEGER {
            refuse!("a JSON integer is beyond 2^53");
        }
        Ok(Value::Int(v))
    }
    fn hex4(&mut self) -> R<u32> {
        if self.i + 4 > self.s.len() {
            return bad("a short \\u escape");
        }
        let mut v = 0u32;
        for k in 0..4 {
            let c = self.s[self.i + k];
            let d = match c {
                b'0'..=b'9' => c - b'0',
                b'a'..=b'f' => c - b'a' + 10,
                b'A'..=b'F' => c - b'A' + 10,
                _ => return bad("a bad \\u escape"),
            };
            v = v * 16 + d as u32;
        }
        self.i += 4;
        Ok(v)
    }
    fn string(&mut self) -> R<String> {
        self.i += 1; // the opening quote
        let mut out = String::new();
        loop {
            let run_start = self.i;
            while self.i < self.s.len() && self.s[self.i] != b'"' && self.s[self.i] != b'\\' && self.s[self.i] >= 0x20 {
                self.i += 1;
            }
            // The run is whole UTF-8 characters: it stops only at ASCII bytes.
            out.push_str(core::str::from_utf8(&self.s[run_start..self.i]).map_err(|_| Refused("a JSON file is not valid: UTF-8".into()))?);
            match self.peek() {
                None => return bad("an unterminated string"),
                Some(b'"') => {
                    self.i += 1;
                    break;
                }
                Some(b'\\') => {
                    self.i += 1;
                    let c = match self.peek() {
                        Some(c) => c,
                        None => return bad("an unterminated escape"),
                    };
                    self.i += 1;
                    match c {
                        b'"' => out.push('"'),
                        b'\\' => out.push('\\'),
                        b'/' => out.push('/'),
                        b'b' => out.push('\u{8}'),
                        b'f' => out.push('\u{c}'),
                        b'n' => out.push('\n'),
                        b'r' => out.push('\r'),
                        b't' => out.push('\t'),
                        b'u' => {
                            let hi = self.hex4()?;
                            if (0xD800..0xDC00).contains(&hi) {
                                // A pair only when a low surrogate escape follows at once.
                                if self.s[self.i..].starts_with(b"\\u") {
                                    let save = self.i;
                                    self.i += 2;
                                    let lo = self.hex4()?;
                                    if (0xDC00..0xE000).contains(&lo) {
                                        let c = 0x10000 + ((hi - 0xD800) << 10) + (lo - 0xDC00);
                                        out.push(char::from_u32(c).unwrap_or('\u{FFFD}'));
                                        continue;
                                    }
                                    self.i = save;
                                }
                                if matches!(self.mode, Mode::Profile { .. }) {
                                    refuse!("a JSON string has an unpaired surrogate");
                                }
                                out.push('\u{FFFD}');
                            } else if (0xDC00..0xE000).contains(&hi) {
                                if matches!(self.mode, Mode::Profile { .. }) {
                                    refuse!("a JSON string has an unpaired surrogate");
                                }
                                out.push('\u{FFFD}');
                            } else {
                                out.push(char::from_u32(hi).unwrap_or('\u{FFFD}'));
                            }
                        }
                        _ => return bad("an invalid escape"),
                    }
                }
                Some(_) => return bad("a control character in a string"),
            }
        }
        if matches!(self.mode, Mode::Profile { .. }) {
            check_text(&out)?;
        }
        Ok(out)
    }
}

/// A character's Stream-Safe profile (UAX #15 D4): the non-starters its NFKD leads and trails with, and whether it is
/// all non-starters.
fn stream_safe(c: char) -> (usize, usize, bool) {
    if (c as u32) < 0xA0 {
        return (0, 0, false);
    }
    let mut classes: [u8; 32] = [0; 32];
    let mut n = 0usize;
    let mut overflow: Vec<u8> = Vec::new();
    decompose_compatible(c, |x| {
        let k = canonical_combining_class(x);
        if n < 32 {
            classes[n] = k;
        } else {
            overflow.push(k);
        }
        n += 1;
    });
    let all: Vec<u8> = classes[..n.min(32)].iter().copied().chain(overflow).collect();
    let lead = all.iter().position(|&k| k == 0).unwrap_or(all.len());
    let trail = all.iter().rev().position(|&k| k == 0).unwrap_or(all.len());
    (lead, trail, lead == all.len())
}

/// The profile's checks on one decoded string or key.
pub fn check_text(s: &str) -> R<()> {
    let mut run = 0usize;
    let mut longest = 0usize;
    let mut ascii = true;
    for c in s.chars() {
        if (c as u32) >= 0xA0 {
            ascii = false;
        }
        let (lead, trail, whole) = stream_safe(c);
        run += lead;
        longest = longest.max(run);
        if !whole {
            run = trail;
        }
    }
    if longest > MAX_COMBINING_RUN {
        refuse!("a JSON string has more than 30 combining marks in a row");
    }
    if !ascii && !s.chars().eq(s.nfc()) {
        refuse!("a JSON string is not NFC");
    }
    Ok(())
}

/// RFC 8785 for strings, integers, booleans, null, arrays and objects (keys sorted by UTF-16 code units).
pub fn canonical(v: &Value) -> String {
    let mut out = String::new();
    write_canonical(v, &mut out);
    out
}

fn utf16_key(s: &str) -> Vec<u16> {
    s.encode_utf16().collect()
}

fn write_canonical(v: &Value, out: &mut String) {
    use core::fmt::Write;
    match v {
        Value::Null => out.push_str("null"),
        Value::Bool(true) => out.push_str("true"),
        Value::Bool(false) => out.push_str("false"),
        Value::Int(i) => {
            let _ = write!(out, "{i}");
        }
        // Never reached for canonical documents (they are read integers-only); written as Python writes a float.
        Value::Float(f) => {
            let _ = write!(out, "{f:?}");
        }
        Value::Str(s) => write_string(s, out),
        Value::Arr(a) => {
            out.push('[');
            for (k, x) in a.iter().enumerate() {
                if k > 0 {
                    out.push(',');
                }
                write_canonical(x, out);
            }
            out.push(']');
        }
        Value::Obj(o) => {
            let mut items: Vec<(&String, &Value)> = o.0.iter().map(|(k, v)| (k, v)).collect();
            items.sort_by(|a, b| utf16_key(a.0).cmp(&utf16_key(b.0)));
            out.push('{');
            for (k, (key, x)) in items.iter().enumerate() {
                if k > 0 {
                    out.push(',');
                }
                write_string(key, out);
                out.push(':');
                write_canonical(x, out);
            }
            out.push('}');
        }
    }
}

fn write_string(s: &str, out: &mut String) {
    use core::fmt::Write;
    out.push('"');
    for c in s.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\u{8}' => out.push_str("\\b"),
            '\u{c}' => out.push_str("\\f"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            c if (c as u32) < 0x20 => {
                let _ = write!(out, "\\u{:04x}", c as u32);
            }
            c => out.push(c),
        }
    }
    out.push('"');
}

/// The bytes are the canonical form of their parse.
pub fn is_canonical(v: &Value, bytes: &[u8]) -> bool {
    canonical(v).as_bytes() == bytes
}

#[cfg(test)]
mod tests {
    use super::*;

    fn ok(s: &str) -> Value {
        profile(s.as_bytes()).expect(s)
    }
    fn no(s: &str) {
        assert!(profile(s.as_bytes()).is_err(), "accepted {s}");
    }

    #[test]
    fn unicode_tables_are_the_formats() {
        assert_eq!(unicode_normalization::UNICODE_VERSION, UNICODE_VERSION);
    }

    #[test]
    fn duplicates_are_refused_once_decoded() {
        no(r#"{"a":1,"a":2}"#);
        no(r#"{"a":1,"a":2}"#);
        no(r#"{"x":{"b":1,"b":1}}"#);
        ok(r#"{"a":1,"b":{"a":2}}"#);
    }

    #[test]
    fn surrogates() {
        no(r#""\ud800""#);
        no(r#""\udc00""#);
        no(r#""\ud800A""#);
        assert_eq!(ok(r#""😀""#), Value::Str("\u{1F600}".into()));
    }

    #[test]
    fn nfc_and_combining_marks() {
        no("\"e\u{0301}\"");
        ok("\"\u{00e9}\"");
        no("{\"e\u{0301}\":1}");
        let thirty: String = core::iter::once('a').chain(core::iter::repeat('\u{0301}').take(30)).collect();
        let _ = profile(alloc::format!("\"{thirty}\"").as_bytes());
        let many: String = core::iter::once('a').chain(core::iter::repeat('\u{0300}').take(31)).collect();
        no(&alloc::format!("\"{many}\""));
    }

    #[test]
    fn integers_and_fractions() {
        ok("9007199254740991");
        ok("-9007199254740991");
        no("9007199254740992");
        no("123456789012345678901234567890");
        assert_eq!(ok("950.0"), Value::Float(950.0));
        assert_eq!(ok("8e5"), Value::Float(800000.0));
        no("1e400");
        no("NaN");
        no("Infinity");
        no("01");
        no("1.");
        no("-");
        assert!(profile_integers(b"950.0").is_err());
        assert!(profile_integers(b"950").is_ok());
    }

    #[test]
    fn bom_cr_and_nesting() {
        no("\u{feff}{}");
        no("{}\r\n");
        let deep64 = alloc::format!("{}{}", "[".repeat(64), "]".repeat(64));
        ok(&deep64);
        let deep65 = alloc::format!("{}{}", "[".repeat(65), "]".repeat(65));
        no(&deep65);
        no("\"a\u{1}\"");
        no("[1,]");
        no("{\"a\":1} x");
    }

    #[test]
    fn canonical_form() {
        let v = ok(r#"{"b":1,"a":[true,null,"\u0001\n"],"é":-0}"#);
        assert_eq!(canonical(&v), "{\"a\":[true,null,\"\\u0001\\n\"],\"b\":1,\"\u{e9}\":0}");
        // Keys by UTF-16 code units: U+FFFF sorts after U+10000 (a surrogate pair).
        let v = ok("{\"\u{ffff}\":1,\"\u{10000}\":2}");
        assert_eq!(canonical(&v), "{\"\u{10000}\":2,\"\u{ffff}\":1}");
    }
}
