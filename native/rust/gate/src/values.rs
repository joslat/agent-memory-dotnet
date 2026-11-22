//! JSON pointers, the placeholders of FORMAT.md, and raw request bytes.

use std::collections::hash_map::RandomState;
use std::hash::{BuildHasher, Hasher};
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

use serde_json::{Map, Value};

use crate::Stop;

/// The store tier A points at: nobody listens there.
const UNREACHABLE: &str = "bolt://127.0.0.1:9";

/// What a case may name with `{"$var": name}` besides its own variables.
pub struct Environment {
    pub vars: Map<String, Value>,
}

impl Environment {
    pub fn new(tier: &str, stub_url: Option<String>) -> Environment {
        let store = tier != "A";
        let variable = |name: &str| std::env::var(name).ok().filter(|value| !value.is_empty());
        let text = |value: Option<String>| value.map_or(Value::Null, Value::String);
        let mut vars = Map::new();
        vars.insert("tier".into(), Value::String(tier.into()));
        let uri = if store {
            variable("AMP_URI").unwrap_or_else(|| UNREACHABLE.into())
        } else {
            UNREACHABLE.into()
        };
        vars.insert("storeUri".into(), Value::String(uri));
        vars.insert(
            "storeUser".into(),
            Value::String(variable("AMP_USER").unwrap_or_else(|| "neo4j".into())),
        );
        vars.insert(
            "storePassword".into(),
            Value::String(variable("AMP_PASSWORD").unwrap_or_else(|| "gate-tier-a-unused".into())),
        );
        vars.insert("verify".into(), Value::Bool(store));
        vars.insert("tlsUri".into(), text(variable("AMP_TLS_URI")));
        vars.insert("tlsOtherNameUri".into(), text(variable("AMP_TLS_OTHER_NAME_URI")));
        vars.insert("stubUrl".into(), text(stub_url));
        Environment { vars }
    }
}

static COUNTER: AtomicU64 = AtomicU64::new(0);

/// 64 bits that differ between calls and processes; the standard library's per-process random hash keys seed it.
pub fn random_u64() -> u64 {
    let mut hasher = RandomState::new().build_hasher();
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |elapsed| elapsed.as_nanos());
    hasher.write_u128(nanos);
    hasher.write_u64(COUNTER.fetch_add(1, Ordering::Relaxed));
    hasher.write_u32(std::process::id());
    hasher.finish()
}

/// A new, empty folder's `state` path, as `{"$freshState": true}` and the case variable `stateDir` give.
pub fn fresh_state() -> Result<String, Stop> {
    let folder: PathBuf = std::env::temp_dir().join(format!("am-gate-state-{:016x}", random_u64()));
    std::fs::create_dir_all(&folder)
        .map_err(|error| Stop::Failure(format!("cannot create {}: {error}", folder.display())))?;
    Ok(folder.join("state").to_string_lossy().into_owned())
}

pub fn split(pointer: &str) -> Vec<String> {
    if pointer.is_empty() || pointer == "/" {
        return Vec::new();
    }
    pointer
        .strip_prefix('/')
        .unwrap_or(pointer)
        .split('/')
        .map(|part| part.replace("~1", "/").replace("~0", "~"))
        .collect()
}

fn index(part: &str) -> Option<usize> {
    if part.is_empty() || !part.bytes().all(|byte| byte.is_ascii_digit()) {
        return None;
    }
    part.parse().ok()
}

/// The value at a JSON pointer; `None` when it is not there.
pub fn get_pointer<'a>(document: &'a Value, pointer: &str) -> Option<&'a Value> {
    let mut target = document;
    for part in split(pointer) {
        target = match target {
            Value::Array(items) => items.get(index(&part)?)?,
            Value::Object(map) => map.get(&part)?,
            _ => return None,
        };
    }
    Some(target)
}

fn bad_pointer(pointer: &str) -> Stop {
    Stop::Failure(format!("the pointer {pointer} does not fit the document"))
}

/// Sets the value at a JSON pointer, creating objects on the way.
pub fn set_pointer(document: &mut Value, pointer: &str, value: Value) -> Result<(), Stop> {
    let parts = split(pointer);
    let Some((last, parents)) = parts.split_last() else {
        *document = value;
        return Ok(());
    };
    let mut target = document;
    for part in parents {
        target = match target {
            Value::Array(items) => index(part)
                .and_then(|i| items.get_mut(i))
                .ok_or_else(|| bad_pointer(pointer))?,
            Value::Object(map) => map.entry(part.clone()).or_insert_with(|| Value::Object(Map::new())),
            _ => return Err(bad_pointer(pointer)),
        };
    }
    match target {
        Value::Object(map) => {
            map.insert(last.clone(), value);
        }
        Value::Array(items) => {
            let slot = index(last)
                .and_then(|i| items.get_mut(i))
                .ok_or_else(|| bad_pointer(pointer))?;
            *slot = value;
        }
        _ => return Err(bad_pointer(pointer)),
    }
    Ok(())
}

/// Removes the key at a JSON pointer, when it is there.
pub fn remove_pointer(document: &mut Value, pointer: &str) {
    let parts = split(pointer);
    let Some((last, parents)) = parts.split_last() else {
        return;
    };
    let mut target = document;
    for part in parents {
        target = match target.get_mut(part.as_str()) {
            Some(next) => next,
            None => return,
        };
    }
    if let Value::Object(map) = target {
        map.shift_remove(last);
    }
}

/// Replaces the placeholders of FORMAT.md, depth first.
pub fn resolve(value: &Value, env: &Environment, fixtures: &Value, vars: &Map<String, Value>) -> Result<Value, Stop> {
    let object = match value {
        Value::Array(items) => {
            return items
                .iter()
                .map(|item| resolve(item, env, fixtures, vars))
                .collect::<Result<_, _>>()
                .map(Value::Array)
        }
        Value::Object(object) => object,
        other => return Ok(other.clone()),
    };
    if let Some(name) = object.get("$var") {
        let name = name.as_str().ok_or_else(|| Stop::Failure("$var takes a name".into()))?;
        let (name, pointer) = name
            .split_once('/')
            .map_or((name, None), |(name, rest)| (name, Some(rest)));
        if let Some(found) = vars.get(name) {
            return match pointer {
                None => Ok(found.clone()),
                Some(pointer) => get_pointer(found, &format!("/{pointer}"))
                    .cloned()
                    .ok_or_else(|| Stop::Failure(format!("variable {name} has nothing at /{pointer}"))),
            };
        }
        return match env.vars.get(name) {
            None => Err(Stop::Failure(format!("unknown variable {name}"))),
            Some(Value::Null) => Err(Stop::Skip(format!("needs {name}, which this run does not have"))),
            Some(found) => Ok(found.clone()),
        };
    }
    if object.contains_key("$freshState") {
        return fresh_state().map(Value::String);
    }
    if let Some(name) = object.get("$vector") {
        let name = name.as_str().unwrap_or_default();
        return fixtures["vectors"]
            .get(name)
            .cloned()
            .ok_or_else(|| Stop::Failure(format!("no vector {name} in fixtures.json")));
    }
    if let Some(name) = object.get("$config") {
        let name = name.as_str().unwrap_or_default();
        let template = fixtures["configs"]
            .get(name)
            .ok_or_else(|| Stop::Failure(format!("no configuration {name} in fixtures.json")))?;
        let mut document = resolve(template, env, fixtures, vars)?;
        if let Some(Value::Object(with)) = object.get("$with") {
            for (pointer, replacement) in with {
                set_pointer(&mut document, pointer, resolve(replacement, env, fixtures, vars)?)?;
            }
        }
        if let Some(Value::Array(without)) = object.get("$without") {
            for pointer in without.iter().filter_map(Value::as_str) {
                remove_pointer(&mut document, pointer);
            }
        }
        return Ok(document);
    }
    if let Some(Value::Array(parts)) = object.get("$concat") {
        let mut text = String::new();
        for part in parts {
            match resolve(part, env, fixtures, vars)? {
                Value::String(part) => text.push_str(&part),
                other => text.push_str(&other.to_string()),
            }
        }
        return Ok(Value::String(text));
    }
    let mut resolved = Map::new();
    for (key, item) in object {
        resolved.insert(key.clone(), resolve(item, env, fixtures, vars)?);
    }
    Ok(Value::Object(resolved))
}

/// A request or configuration as bytes: a document's JSON, or exact bytes (`$base64`, `$utf8`, `$repeat`,
/// `$jsonReplace`).
pub fn raw_bytes(
    spec: &Value,
    env: &Environment,
    fixtures: &Value,
    vars: &Map<String, Value>,
) -> Result<Vec<u8>, Stop> {
    if let Value::Object(object) = spec {
        if let Some(text) = object.get("$base64") {
            return decode_base64(text.as_str().unwrap_or_default());
        }
        if let Some(text) = object.get("$utf8") {
            return Ok(text.as_str().unwrap_or_default().as_bytes().to_vec());
        }
        if let Some(Value::Array(repeat)) = object.get("$repeat") {
            let text = repeat.first().and_then(Value::as_str).unwrap_or_default();
            let times = repeat.get(1).and_then(Value::as_u64).unwrap_or(0);
            return Ok(text.repeat(times as usize).into_bytes());
        }
        if let Some(Value::Array(replace)) = object.get("$jsonReplace") {
            let [document, old, new] = replace.as_slice() else {
                return Err(Stop::Failure("$jsonReplace takes [document, old, new]".into()));
            };
            let document = python_json(&resolve(document, env, fixtures, vars)?, true);
            let old = match resolve(old, env, fixtures, vars)? {
                Value::String(text) => text,
                other => other.to_string(),
            };
            // As the reference runner: Python's json.dumps spacing, and every occurrence replaced.
            return Ok(document.replace(&old, new.as_str().unwrap_or_default()).into_bytes());
        }
    }
    serde_json::to_vec(&resolve(spec, env, fixtures, vars)?).map_err(|error| Stop::Failure(error.to_string()))
}

/// JSON as Python's `json.dumps` writes it: `", "` and `": "` between items, and with `ascii` every character beyond
/// ASCII escaped (`ensure_ascii`).
pub fn python_json(value: &Value, ascii: bool) -> String {
    let mut out = String::new();
    write_python(&mut out, value, ascii);
    out
}

fn write_python(out: &mut String, value: &Value, ascii: bool) {
    match value {
        Value::Array(items) => {
            out.push('[');
            for (i, item) in items.iter().enumerate() {
                if i > 0 {
                    out.push_str(", ");
                }
                write_python(out, item, ascii);
            }
            out.push(']');
        }
        Value::Object(map) => {
            out.push('{');
            for (i, (key, item)) in map.iter().enumerate() {
                if i > 0 {
                    out.push_str(", ");
                }
                write_string(out, key, ascii);
                out.push_str(": ");
                write_python(out, item, ascii);
            }
            out.push('}');
        }
        Value::String(text) => write_string(out, text, ascii),
        other => out.push_str(&other.to_string()),
    }
}

fn write_string(out: &mut String, text: &str, ascii: bool) {
    out.push('"');
    for c in text.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            '\u{8}' => out.push_str("\\b"),
            '\u{c}' => out.push_str("\\f"),
            c if (c as u32) < 0x20 || (ascii && (c as u32) > 0x7e) => {
                let mut units = [0_u16; 2];
                for unit in c.encode_utf16(&mut units) {
                    out.push_str(&format!("\\u{unit:04x}"));
                }
            }
            c => out.push(c),
        }
    }
    out.push('"');
}

/// Standard base64 with padding, as the cases write it.
pub fn decode_base64(text: &str) -> Result<Vec<u8>, Stop> {
    let mut bytes = Vec::with_capacity(text.len() * 3 / 4);
    let mut buffer = 0_u32;
    let mut bits = 0;
    for c in text.bytes().filter(|byte| !byte.is_ascii_whitespace() && *byte != b'=') {
        let value = match c {
            b'A'..=b'Z' => c - b'A',
            b'a'..=b'z' => c - b'a' + 26,
            b'0'..=b'9' => c - b'0' + 52,
            b'+' => 62,
            b'/' => 63,
            _ => return Err(Stop::Failure(format!("not base64: {text}"))),
        };
        buffer = (buffer << 6) | u32::from(value);
        bits += 6;
        if bits >= 8 {
            bits -= 8;
            bytes.push((buffer >> bits) as u8);
            buffer &= (1 << bits) - 1;
        }
    }
    Ok(bytes)
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn base64_decodes_the_cases_bytes() {
        assert_eq!(
            decode_base64("//57InNjaGVtYVZlcnNpb24iOjF9").unwrap()[..3],
            [0xff, 0xfe, b'{']
        );
        assert_eq!(decode_base64("aGk=").unwrap(), b"hi");
    }

    #[test]
    fn python_spacing_and_ascii_escapes() {
        let value = json!({"a": [1, "é"], "b": {"c": null}});
        let escaped = format!(r#"{{"a": [1, "{}u00e9"], "b": {{"c": null}}}}"#, char::from(92));
        assert_eq!(python_json(&value, true), escaped);
        assert_eq!(python_json(&value, false), r#"{"a": [1, "é"], "b": {"c": null}}"#);
    }

    #[test]
    fn pointers_set_get_and_remove() {
        let mut document = json!({"store": {"uri": "x"}});
        set_pointer(&mut document, "store/surprise", json!(1)).unwrap();
        set_pointer(&mut document, "/owner", json!({"id": "b"})).unwrap();
        assert_eq!(get_pointer(&document, "/store/surprise"), Some(&json!(1)));
        assert_eq!(get_pointer(&document, "/owner/id"), Some(&json!("b")));
        remove_pointer(&mut document, "/store/uri");
        assert_eq!(get_pointer(&document, "/store/uri"), None);
        assert_eq!(get_pointer(&json!([1, 2]), "/1"), Some(&json!(2)));
        assert_eq!(get_pointer(&json!([1, 2]), "/x"), None);
    }
}
