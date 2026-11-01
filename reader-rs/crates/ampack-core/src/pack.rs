//! Core verify (Core 7 to 11, 14 to 17c): the files list and digest, the signatures against the trust policy, pack.json and
//! its kind, a composite's lock, the card, the derivation, the usage policy's read door, the records' JSON profile, the
//! embeddings; a pack set with each member on its own; and inspect.

use alloc::collections::{BTreeMap, BTreeSet};
use alloc::string::{String, ToString};
use alloc::vec::Vec;

use crate::json::{canonical, profile, profile_integers, Obj, Value};
use crate::registry::{holds, Registry};
use crate::text::{hex_n, is_commit, is_digest, safe_path, valid_id, valid_version, valid_webid};
use crate::time::{instant, utc_second, Ticks};
use crate::trust::{entry_shaped, holding, Policy, MAX_ENVELOPE, MAX_SIGNATURES};
use crate::zip::{read_zip, Files};
use crate::{b64, crypto, refuse, refused, Refused, R};

pub const FILES: &str = "META/files.json";
pub const SIGNATURES: &str = "META/signatures/";
pub const ATTESTATIONS: &str = "META/attestations/";
pub const LOCK: &str = "META/lock.json";
pub const CARD: &str = "card.json";
pub const FILES_FORMAT: &str = "agentmemory.pack.files/1.1";
pub const PACK_FORMAT: &str = "agentmemory.pack/1.2";
pub const LOCK_FORMAT: &str = "agentmemory.pack.lock/1.1";
pub const CARD_FORMAT: &str = "agentmemory.pack.card/1";
pub const FEDERATION_FORMAT: &str = "agentmemory.pack.federation/1";
pub const STATEMENT: &str = "https://in-toto.io/Statement/v1";
pub const PACK_MINOR: u32 = 2;
pub const MAX_CRITICAL: usize = 64;
pub const MAX_ENVELOPES: usize = 16;
pub const MAX_FILES_LIST: usize = 2 << 20;
pub const MAX_JSON: usize = 1 << 20;
pub const TIMESTAMP_SUFFIX: &str = ".tsr";
pub const MAX_CERTIFICATES: usize = 8;
pub const MAX_RUN_FILES: usize = 256;
pub const MAX_RUN_BYTES: usize = 8 << 20;

/// What verify needs: the registry, the host's policy (its revocations applied) and the time.
pub struct Ctx<'a> {
    pub reg: &'a Registry,
    pub policy: &'a Policy,
    pub now: Ticks,
}

/// A verified pack.
#[derive(Clone)]
pub struct Verified {
    pub id: String,
    pub version: String,
    /// sha256: and the hex of META/files.json.
    pub digest: String,
    pub manifest: Obj,
}

/// The federation extension: its publisher and its audience (None for public).
pub struct Federation {
    pub publisher: String,
    pub audience: Option<Vec<String>>,
}

/// A certificate file name: META/attestations/<stem>.certificate.dsse.json.
pub fn cert_stem(path: &str) -> Option<&str> {
    let stem = path.strip_prefix(ATTESTATIONS)?.strip_suffix(".certificate.dsse.json")?;
    stem_shaped(stem).then_some(stem)
}

/// An erasure file name: META/attestations/<stem>.erasure.dsse.json.
pub fn erasure_stem(path: &str) -> Option<&str> {
    let stem = path.strip_prefix(ATTESTATIONS)?.strip_suffix(".erasure.dsse.json")?;
    stem_shaped(stem).then_some(stem)
}

/// [a-z0-9][a-z0-9-]{0,63}
pub fn stem_shaped(s: &str) -> bool {
    let b = s.as_bytes();
    (1..=64).contains(&b.len())
        && (b[0].is_ascii_lowercase() || b[0].is_ascii_digit())
        && b.iter().all(|&c| c.is_ascii_lowercase() || c.is_ascii_digit() || c == b'-')
}

/// A file of an AEF run: META/attestations/<stem>.aef/<something>; its stem.
pub fn run_stem(path: &str) -> Option<&str> {
    let rest = path.strip_prefix(ATTESTATIONS)?;
    let (stem, inner) = rest.split_once(".aef/")?;
    (stem_shaped(stem) && !inner.is_empty() && !inner.contains('\n')).then_some(stem)
}

/// What travels in META/attestations/: certificates, erasures and the AEF runs beside certificates, shaped and bounded.
fn attestation_checks(files: &Files) -> R<()> {
    let mut stems = BTreeSet::new();
    let mut erasures = 0usize;
    let mut runs: BTreeMap<&str, (usize, usize)> = BTreeMap::new();
    for (path, data) in files.range::<String, _>(String::from(ATTESTATIONS)..) {
        if !path.starts_with(ATTESTATIONS) {
            break;
        }
        if let Some(stem) = cert_stem(path) {
            stems.insert(stem);
            if data.len() > MAX_ENVELOPE {
                refuse!("'{path}' is larger than 64 KiB");
            }
            continue;
        }
        if erasure_stem(path).is_some() {
            erasures += 1;
            if data.len() > MAX_ENVELOPE {
                refuse!("'{path}' is larger than 64 KiB");
            }
            continue;
        }
        match run_stem(path) {
            Some(stem) => {
                let e = runs.entry(stem).or_insert((0, 0));
                e.0 += 1;
                e.1 += data.len();
            }
            None => refuse!("'{path}' is not an attestation"),
        }
    }
    if stems.len() > MAX_CERTIFICATES {
        refuse!("the pack carries {} certificates; at most {MAX_CERTIFICATES}", stems.len());
    }
    if erasures > MAX_CERTIFICATES {
        refuse!("the pack carries {erasures} erasures; at most {MAX_CERTIFICATES}");
    }
    for (stem, (count, bytes)) in runs {
        if !stems.contains(stem) {
            refuse!("the AEF run {stem}.aef/ travels without its certificate");
        }
        if count > MAX_RUN_FILES || bytes > MAX_RUN_BYTES {
            refuse!("the AEF run {stem}.aef/ is too large");
        }
    }
    Ok(())
}

/// The minor of a pack format name, or None when this verifier does not read it: 1.2 up to 1.999, no leading zeros.
fn format_minor(v: Option<&Value>) -> Option<u32> {
    let s = v?.as_str()?;
    let m = s.strip_prefix("agentmemory.pack/1.")?;
    let b = m.as_bytes();
    if b.is_empty() || b.len() > 3 || !b.iter().all(|c| c.is_ascii_digit()) || b[0] == b'0' {
        return None;
    }
    let n: u32 = m.parse().ok()?;
    (n >= PACK_MINOR).then_some(n)
}

fn critical_fields(reg: &Registry, manifest: &Obj) -> R<()> {
    let names = match manifest.get("critical") {
        None => return Ok(()),
        Some(v) => v,
    };
    let names = match names.as_strs() {
        Some(n) if (1..=MAX_CRITICAL).contains(&n.len()) => n,
        _ => refuse!("pack.json has a malformed 'critical': a list of 1 to {MAX_CRITICAL} field names"),
    };
    if names.iter().collect::<BTreeSet<_>>().len() != names.len() {
        refuse!("pack.json lists a critical field twice");
    }
    for name in names {
        if name == "critical" || !manifest.has(name) {
            refuse!("pack.json marks '{name}' critical, but holds no such field");
        }
        if !holds(&reg.manifest_fields, name) {
            refuse!("pack.json marks the field '{name}' critical, and this verifier of {PACK_FORMAT} does not understand it");
        }
    }
    Ok(())
}

/// "public" (None), or 1 to 64 distinct WebIDs.
pub fn read_audience(v: &Value, what: &str) -> R<Option<Vec<String>>> {
    if v.is_str("public") {
        return Ok(None);
    }
    let list = match v.as_strs() {
        Some(l) if (1..=64).contains(&l.len()) => l,
        _ => refuse!("{what} is \"public\" or a list of 1 to 64 WebIDs"),
    };
    if !list.iter().all(|w| valid_webid(w)) {
        refuse!("{what} names something that is not a WebID");
    }
    if list.iter().collect::<BTreeSet<_>>().len() != list.len() {
        refuse!("{what} names a WebID twice");
    }
    Ok(Some(list.iter().map(|s| s.to_string()).collect()))
}

/// The federation extension of pack.json, or None. It must be critical.
pub fn read_federation(reg: &Registry, extensions: &Obj) -> R<Option<Federation>> {
    let f = match extensions.get("federation") {
        None => return Ok(None),
        Some(f) => f,
    };
    let critical = extensions.get("critical").and_then(Value::as_arr).is_some_and(|c| c.iter().any(|x| x.is_str("federation")));
    if !critical {
        refuse!("pack.json carries the 'federation' extension without marking it critical");
    }
    let f = f.as_obj().ok_or_else(|| refused!("pack.json has a malformed 'federation' extension"))?;
    if !f.keys_within(reg.shape("federation")) {
        refuse!("the 'federation' extension has an unknown field");
    }
    if !f.get("format").is_some_and(|x| x.is_str(FEDERATION_FORMAT)) {
        refuse!("the 'federation' extension must name format '{FEDERATION_FORMAT}'");
    }
    let publisher = match f.str("publisher") {
        Some(p) if valid_webid(p) => p.to_string(),
        _ => refuse!("the federation publisher is not a WebID"),
    };
    let audience = match f.get("audience") {
        None => refuse!("the 'federation' extension names no audience"),
        Some(a) => read_audience(a, "the federation audience")?,
    };
    if f.has("purpose") && !reg.is_text(f.get("purpose"), 300) {
        refuse!("the federation purpose is display-safe text of 1 to 300 characters");
    }
    Ok(Some(Federation { publisher, audience }))
}

/// A composite member's values typed as the loader reads them.
fn member_typed(m: &Obj) -> bool {
    let text = |v: Option<&Value>| v.and_then(Value::as_str).is_some_and(|s| !s.is_empty());
    let priority_ok = match m.get("priority") {
        None => true,
        Some(Value::Int(p)) => (-(1i64 << 31)..(1i64 << 31)).contains(p),
        _ => false,
    };
    let enabled_ok = m.get("enabled").map_or(true, |v| v.as_bool().is_some());
    let contains_ok = match m.get("contains") {
        None => true,
        Some(Value::Arr(a)) => a.iter().all(|c| text(Some(c))),
        _ => false,
    };
    text(m.get("id")) && text(m.get("version")) && priority_ok && enabled_ok && contains_ok
}

fn composite_checks(reg: &Registry, manifest: &Obj, content: &BTreeMap<&str, &Vec<u8>>) -> R<()> {
    let lock_bytes = content[LOCK];
    let lock = profile_integers(lock_bytes)?;
    if canonical(&lock).as_bytes() != lock_bytes.as_slice() {
        refuse!("META/lock.json is not canonical");
    }
    let l = match lock.as_obj() {
        Some(l) if l.keys_are(reg.shape("familyFile")) && l.get("format").is_some_and(|f| f.is_str(LOCK_FORMAT)) && l.get("members").and_then(Value::as_arr).is_some() => l,
        _ => refuse!("META/lock.json is not a lock: format and members, nothing else"),
    };
    let mut pinned: Vec<&str> = Vec::new();
    for m in l.get("members").and_then(Value::as_arr).unwrap() {
        let ok = m.as_obj().filter(|m| {
            m.keys_are(reg.shape("familyMember"))
                && m.str("id").is_some_and(valid_id)
                && m.str("version").is_some_and(valid_version)
                && m.str("digest").is_some_and(|d| d.starts_with("sha256:") && d.chars().count() == 71)
        });
        match ok {
            Some(m) => pinned.push(m.str("id").unwrap()),
            None => refuse!("META/lock.json has a malformed member"),
        }
    }
    if pinned.iter().collect::<BTreeSet<_>>().len() != pinned.len() {
        refuse!("META/lock.json pins one id twice");
    }
    let members_bytes = match content.get("members.json") {
        Some(b) => *b,
        None => refuse!("a composite pack carries members.json"),
    };
    let malformed = || refused!("members.json is malformed");
    let doc = profile(members_bytes).map_err(|_| malformed())?;
    let d = doc.as_obj().filter(|d| d.len() == 1 && d.has("members")).ok_or_else(malformed)?;
    let list = d.get("members").and_then(Value::as_arr).ok_or_else(malformed)?;
    let mut named: Vec<&str> = Vec::new();
    for m in list {
        let m = m.as_obj().filter(|m| m.keys_within(&reg.member_fields) && member_typed(m)).ok_or_else(malformed)?;
        named.push(m.str("id").unwrap());
    }
    let mut contains: Vec<&str> = manifest.get("contains").and_then(Value::as_strs).unwrap_or_default();
    contains.sort();
    named.sort();
    pinned.sort();
    if named != contains {
        refuse!("members.json and pack.json name different members");
    }
    if pinned != contains {
        refuse!("META/lock.json pins other members than the composite contains");
    }
    Ok(())
}

fn manifest_checks(ctx: &Ctx, manifest: &Obj, content: &BTreeMap<&str, &Vec<u8>>) -> R<()> {
    let reg = ctx.reg;
    for field in &reg.string_fields {
        if manifest.get(field).is_some_and(|v| v.as_str().is_none()) {
            refuse!("pack.json has a '{field}' that is not a string");
        }
    }
    for field in ["requires", "display"] {
        if manifest.get(field).is_some_and(|v| v.as_obj().is_none()) {
            refuse!("pack.json has a '{field}' that is not an object");
        }
    }
    if let Some(display) = manifest.get("display").and_then(Value::as_obj) {
        if display.get("icon").is_some_and(|v| v.as_str().is_none()) {
            refuse!("pack.json has a display.icon that is not a string");
        }
        for (name, maximum, line) in [("title", 60, true), ("description", 300, false)] {
            if let Some(t) = display.get(name) {
                if !t.as_str().is_some_and(|t| reg.is_display_text(t, maximum, line)) {
                    refuse!("pack.json has a display.{name} that is not display-safe text of 1 to {maximum} characters");
                }
            }
        }
    }
    let kind = manifest.str("kind").filter(|k| holds(&reg.kinds, k)).ok_or_else(|| refused!("pack.json needs a known kind"))?;
    for (field, allowed) in [("tier", &reg.tiers), ("protection", &reg.protections), ("code", &reg.codes)] {
        if let Some(v) = manifest.str(field) {
            if !holds(allowed, v) {
                refuse!("pack.json has an unknown {field}");
            }
        }
    }
    for field in ["contains", "containableBy"] {
        let ok = match manifest.get(field) {
            None => true,
            Some(v) => v.as_strs().is_some_and(|l| l.iter().all(|x| valid_id(x))),
        };
        if !ok {
            refuse!("pack.json has a malformed '{field}'");
        }
    }
    if let Some(v) = manifest.get("validUntil") {
        let until = v.as_str().and_then(instant).ok_or_else(|| refused!("pack.json has a validUntil that is not a date and time"))?;
        if until < ctx.now {
            refuse!("the pack's validUntil has passed");
        }
    }
    if let Some(source) = manifest.get("source") {
        let shape = reg.shape("manifestSource");
        let ok = source.as_obj().filter(|s| {
            s.0.iter().all(|(k, v)| holds(shape, k) && v.as_str().is_some_and(|t| (1..=200).contains(&t.chars().count())))
        });
        let s = ok.ok_or_else(|| refused!("pack.json has a malformed 'source'"))?;
        if s.str("builtAt").is_some_and(|b| instant(b).is_none()) {
            refuse!("pack.json has a source.builtAt that is not a date and time");
        }
        if s.str("commit").is_some_and(|c| !is_commit(c)) {
            refuse!("pack.json has a source.commit that is not 7 to 64 hexadecimal characters");
        }
    }
    let has_records = content.keys().any(|p| p.starts_with("records/") && p.ends_with(".jsonl"));
    if (kind == "knowledge" || kind == "snapshot") && !has_records {
        refuse!("a {kind} pack holds records");
    }
    if kind == "definition" && has_records {
        refuse!("a definition pack holds no records");
    }
    if kind == "composite" || kind == "set" {
        for field in ["sdk", "requires", "containableBy"] {
            if manifest.has(field) {
                refuse!("a {kind} pack declares no {field}: its members carry their own");
            }
        }
        if content.contains_key(reg.policy_path.as_str()) {
            refuse!("a {kind} pack carries no usage policy (policy.json): its members carry their own");
        }
    }
    if kind == "composite" {
        let contains_some = manifest.get("contains").and_then(Value::as_arr).is_some_and(|c| !c.is_empty());
        if !content.contains_key(LOCK) || !contains_some {
            refuse!("a composite names what it contains and pins it in META/lock.json");
        }
        if has_records {
            refuse!("a composite pack holds no records of its own");
        }
        composite_checks(reg, manifest, content)?;
    }
    if kind == "set" && !content.keys().any(|p| p.starts_with("packs/")) {
        refuse!("a pack set carries its members under packs/");
    }
    let protection = manifest.str("protection").or(reg.default_of("protection")).unwrap_or("open");
    if protection != "open" && !content.contains_key("META/seal.json") {
        refuse!("a protected pack carries META/seal.json");
    }
    Ok(())
}

fn read_card(reg: &Registry, data: &[u8]) -> R<()> {
    let card = profile(data)?;
    let c = match card.as_obj() {
        Some(c) if c.keys_within(reg.shape("card")) => c,
        _ => refuse!("card.json holds other fields than a pack card"),
    };
    if !c.get("format").is_some_and(|f| f.is_str(CARD_FORMAT)) {
        refuse!("card.json must name format '{CARD_FORMAT}'");
    }
    let owner_ok = c.get("owner").and_then(Value::as_obj).is_some_and(|o| {
        o.keys_within(reg.shape("cardOwner")) && reg.is_text(o.get("name"), 200) && (!o.has("contact") || reg.is_text(o.get("contact"), 200))
    });
    if !owner_ok {
        refuse!("card.json names its owner as 'name' and, optionally, 'contact'");
    }
    for (name, maximum, required) in [
        ("intendedUse", 2000, true),
        ("limitations", 2000, true),
        ("provenance", 2000, false),
        ("audience", 500, false),
        ("domain", 100, false),
        ("whenToUse", 400, false),
    ] {
        if (required || c.has(name)) && !reg.is_text(c.get(name), maximum) {
            refuse!("card.json: '{name}' is text of 1 to {maximum} characters");
        }
    }
    for (name, maximum) in [("boundaries", 32), ("risks", 32), ("references", 16)] {
        let ok = match c.get(name) {
            None => true,
            Some(v) => v.as_arr().is_some_and(|items| {
                items.len() <= maximum
                    && items.iter().all(|i| reg.is_text(Some(i), 500))
                    && items.iter().filter_map(Value::as_str).collect::<BTreeSet<_>>().len() == items.len()
            }),
        };
        if !ok {
            refuse!("card.json: '{name}' is a list of at most {maximum} distinct texts");
        }
    }
    Ok(())
}

fn read_derivation(reg: &Registry, data: &[u8]) -> R<()> {
    let path = &reg.derivation_path;
    let d = profile_integers(data)?;
    if canonical(&d).as_bytes() != data {
        refuse!("{path} is not canonical (RFC 8785)");
    }
    let d = match d.as_obj() {
        Some(o) if o.keys_within(reg.shape("derivation")) => o.clone(),
        _ => refuse!("{path} holds other fields than a derivation"),
    };
    if !d.get("format").is_some_and(|f| f.is_str(&reg.derivation_format)) {
        refuse!("{path} must name format '{}'", reg.derivation_format);
    }
    let inputs = match d.get("inputs").and_then(Value::as_arr) {
        Some(i) if (1..=256).contains(&i.len()) => i,
        _ => refuse!("{path} names 1 to 256 inputs"),
    };
    let mut names = BTreeSet::new();
    for i in inputs {
        let ok = i.as_obj().filter(|i| {
            let micro_ok = match i.get("confidenceMicro") {
                None => true,
                Some(Value::Int(m)) => (0..=1_000_000).contains(m),
                _ => false,
            };
            i.keys_within(reg.shape("derivationInput"))
                && i.str("digest").is_some_and(is_digest)
                && reg.is_line(i.get("name"), 256)
                && (!i.has("licence") || reg.is_line(i.get("licence"), 128))
                && micro_ok
        });
        let i = ok.ok_or_else(|| {
            refused!("{path}: an input is a digest, a name of 1 to 256 characters, and optionally a licence and a confidenceMicro of 0 to 1000000")
        })?;
        let name = i.str("name").unwrap();
        if !names.insert(name) {
            refuse!("{path} names the input '{name}' twice");
        }
    }
    let tool_ok = d.get("tool").and_then(Value::as_obj).is_some_and(|t| {
        t.keys_within(reg.shape("derivationTool")) && reg.is_line(t.get("name"), 128) && reg.is_line(t.get("version"), 128)
    });
    if !tool_ok {
        refuse!("{path} names its tool as a name and a version, each 1 to 128 characters");
    }
    if let Some(model) = d.get("model") {
        let ok = model.as_obj().is_some_and(|m| {
            m.keys_within(reg.shape("derivationModel")) && reg.is_line(m.get("id"), 256) && (!m.has("version") || reg.is_line(m.get("version"), 128))
        });
        if !ok {
            refuse!("{path} names its model as an id and, optionally, a version");
        }
    }
    if d.has("promptDigest") && !d.str("promptDigest").is_some_and(is_digest) {
        refuse!("{path}: promptDigest is sha256: and 64 lowercase hexadecimal characters");
    }
    if let Some(parameters) = d.get("parameters") {
        let ok = parameters.as_obj().is_some_and(|p| {
            p.len() <= 64
                && p.0.iter().all(|(k, v)| reg.is_display_text(k, 128, true) && v.as_str().is_some_and(|v| reg.is_display_text(v, 1024, true)))
        });
        if !ok {
            refuse!("{path}: parameters is an object of at most 64 texts");
        }
    }
    if let Some(parent) = d.get("parent") {
        let ok = parent.as_obj().is_some_and(|p| {
            p.keys_within(reg.shape("derivationParent")) && p.str("id").is_some_and(valid_id) && p.str("digest").is_some_and(is_digest)
        });
        if !ok {
            refuse!("{path} names its parent as a pack id and the digest of its file list");
        }
    }
    let actions_ok = d.get("actions").and_then(Value::as_strs).is_some_and(|a| {
        !a.is_empty() && a.iter().all(|x| holds(&reg.derivation_actions, x)) && a.iter().collect::<BTreeSet<_>>().len() == a.len()
    });
    if !actions_ok {
        refuse!("{path}: actions are distinct, of {}", sorted(&reg.derivation_actions));
    }
    Ok(())
}

/// Names in code point order, joined for a message.
fn sorted(names: &[String]) -> String {
    let mut v: Vec<&str> = names.iter().map(|s| s.as_str()).collect();
    v.sort();
    v.join(", ")
}

/// One rule of a usage policy.
pub struct Rule {
    pub audience: Vec<String>,
    pub allow: Vec<String>,
    pub deny: Vec<String>,
    pub not_after: Option<Ticks>,
}

/// What a receiver may do with a pack (feature 10): canonical and strict.
pub fn read_policy(reg: &Registry, data: &[u8]) -> R<Vec<Rule>> {
    let d = profile_integers(data)?;
    if canonical(&d).as_bytes() != data {
        refuse!("policy.json is not canonical (RFC 8785)");
    }
    let d = match d.as_obj() {
        Some(o) if o.keys_within(reg.shape("policy")) => o.clone(),
        _ => refuse!("policy.json holds other fields than a usage policy"),
    };
    if !d.get("format").is_some_and(|f| f.is_str(&reg.policy_format)) {
        refuse!("policy.json must name format '{}'", reg.policy_format);
    }
    let rules = match d.get("rules").and_then(Value::as_arr) {
        Some(r) if (1..=64).contains(&r.len()) => r,
        _ => refuse!("policy.json holds 1 to 64 rules"),
    };
    let mut read = Vec::new();
    for r in rules {
        let r = match r.as_obj() {
            Some(r) if r.keys_within(reg.shape("policyRule")) => r,
            _ => refuse!("policy.json: a rule holds an audience, and allow, deny and notAfter only"),
        };
        let audience = match r.get("audience").and_then(Value::as_strs) {
            Some(a) if (1..=16).contains(&a.len()) && a.iter().all(|x| *x == "*" || valid_webid(x)) && a.iter().collect::<BTreeSet<_>>().len() == a.len() => a,
            _ => refuse!("policy.json: a rule's audience is 1 to 16 distinct entries, each '*' or a WebID"),
        };
        let mut verbs: [Vec<String>; 2] = [Vec::new(), Vec::new()];
        for (k, name) in ["allow", "deny"].iter().enumerate() {
            let given = match r.get(name) {
                None => Vec::new(),
                Some(v) => match v.as_strs() {
                    Some(g) if g.iter().all(|x| holds(&reg.policy_verbs, x)) && g.iter().collect::<BTreeSet<_>>().len() == g.len() => g,
                    _ => refuse!("policy.json: allow and deny are distinct verbs, of {}", sorted(&reg.policy_verbs)),
                },
            };
            verbs[k] = given.iter().map(|s| s.to_string()).collect();
        }
        if verbs[0].is_empty() && verbs[1].is_empty() {
            refuse!("policy.json: a rule allows or denies at least one verb");
        }
        let not_after = match r.get("notAfter") {
            None => None,
            Some(v) => Some(v.as_str().and_then(utc_second).ok_or_else(|| refused!("policy.json: a rule's notAfter is an instant in UTC to the second"))?),
        };
        let [allow, deny] = verbs;
        read.push(Rule { audience: audience.iter().map(|s| s.to_string()).collect(), allow, deny, not_after });
    }
    Ok(read)
}

/// Deny wins; a rule's denials outlive its grants (a past notAfter ends what it allowed, never what it refused).
pub fn policy_allows(rules: &[Rule], verb: &str, identities: &[String], now: Ticks) -> bool {
    let applying: Vec<&Rule> = rules.iter().filter(|r| holds(&r.audience, "*") || r.audience.iter().any(|a| holds(identities, a))).collect();
    !applying.iter().any(|r| holds(&r.deny, verb)) && applying.iter().any(|r| holds(&r.allow, verb) && r.not_after.map_or(true, |na| now <= na))
}

fn embeddings_name(path: &str) -> Option<&str> {
    let rest = path.strip_prefix("embeddings/")?;
    let stem = rest.strip_suffix(".st").or_else(|| rest.strip_suffix(".json"))?;
    stem_shaped(stem).then_some(stem)
}

fn read_embeddings(reg: &Registry, content: &BTreeMap<&str, &Vec<u8>>) -> R<()> {
    let paths: Vec<&str> = content.keys().filter(|p| p.starts_with("embeddings/")).copied().collect();
    if paths.is_empty() {
        return Ok(());
    }
    let mut names = BTreeSet::new();
    for p in &paths {
        match embeddings_name(p) {
            Some(n) => {
                names.insert(n);
            }
            None => refuse!("'{}' is not an embeddings file", crate::text::shown(p)),
        }
    }
    if names.len() > 8 {
        refuse!("a pack carries at most 8 embedding sets");
    }
    // Every (type, key) the record lines name.
    let mut records: BTreeSet<(String, String)> = BTreeSet::new();
    for (p, b) in content.iter() {
        if !(p.starts_with("records/") && p.ends_with(".jsonl")) {
            continue;
        }
        for line in b.split(|&c| c == b'\n') {
            if line.is_empty() {
                continue;
            }
            if let Ok(Value::Obj(o)) = profile(line) {
                if let (Some(t), Some(k)) = (o.str("type"), o.str("key")) {
                    records.insert((t.to_string(), k.to_string()));
                }
            }
        }
    }
    for name in names {
        let (st, ix) = match (content.get(alloc::format!("embeddings/{name}.st").as_str()), content.get(alloc::format!("embeddings/{name}.json").as_str())) {
            (Some(s), Some(i)) => (*s, *i),
            _ => refuse!("the embedding set '{name}' needs both its .st and its .json"),
        };
        let index = profile(ix)?;
        let index = match index.as_obj() {
            Some(i) if i.keys_within(reg.shape("embeddingsIndex")) => i.clone(),
            _ => refuse!("an embeddings index holds other fields than its own"),
        };
        if !index.get("format").is_some_and(|f| f.is_str("ampack.embeddings/1")) {
            refuse!("an embeddings index names format 'ampack.embeddings/1'");
        }
        if !reg.is_line(index.get("model"), 200) || (index.has("modelDigest") && !reg.is_line(index.get("modelDigest"), 200)) {
            refuse!("an embeddings index names its model, and its digest when it gives one, as one line of 1 to 200 characters");
        }
        let dtype = match index.str("dtype") {
            Some(d) if holds(&reg.dtypes, d) => d.to_string(),
            _ => refuse!("an embeddings index names a known dtype"),
        };
        let dimension = match index.get("dimension").and_then(Value::as_int) {
            Some(d) if (1..=4096).contains(&d) => d as usize,
            _ => refuse!("an embeddings index names a dimension of 1 to 4096"),
        };
        if index.get("normalized").and_then(Value::as_bool).is_none() {
            refuse!("an embeddings index says whether its vectors are normalized");
        }
        if !index.str("distance").is_some_and(|d| holds(&reg.distances, d)) {
            refuse!("an embeddings index names a known distance");
        }
        let rows = match index.get("rows").and_then(Value::as_arr) {
            Some(r) if (1..=5000).contains(&r.len()) => r,
            _ => refuse!("an embeddings index lists 1 to 5000 rows"),
        };
        let mut seen = BTreeSet::new();
        for row in rows {
            let pair = match row.as_strs() {
                Some(r) if r.len() == 2 => (r[0].to_string(), r[1].to_string()),
                _ => refuse!("each row of an embeddings index is [type, key], two strings"),
            };
            if seen.contains(&pair) {
                refuse!("an embeddings index lists a row twice");
            }
            if !records.contains(&pair) {
                refuse!("an embeddings index names a row the pack holds no record of");
            }
            seen.insert(pair);
        }
        if st.len() < 8 {
            refuse!("an embeddings tensor is shorter than its header length");
        }
        let length = u64::from_le_bytes(st[..8].try_into().unwrap());
        if length == 0 || length > 64 * 1024 || (st.len() as u64) < 8 + length {
            refuse!("an embeddings tensor states a header past its bound or its end");
        }
        let length = length as usize;
        let mut body = &st[8..8 + length];
        while let [rest @ .., b' '] = body {
            body = rest;
        }
        if body.first() != Some(&b'{') || body.last() != Some(&b'}') {
            refuse!("an embeddings tensor's header is a JSON object from its first byte, padded with spaces only");
        }
        let header = profile(body)?;
        let tensor = match header.as_obj() {
            Some(h) if h.len() == 1 && h.has("vectors") => h.get("vectors").unwrap(),
            _ => refuse!("an embeddings tensor's header holds exactly one tensor, 'vectors', and no metadata"),
        };
        let tensor_fields = ["dtype".to_string(), "shape".to_string(), "data_offsets".to_string()];
        let tensor = match tensor.as_obj() {
            Some(t) if t.keys_within(&tensor_fields) => t,
            _ => refuse!("an embeddings tensor is exactly dtype, shape and data_offsets"),
        };
        if !tensor.str("dtype").is_some_and(|d| d == dtype) {
            refuse!("an embeddings tensor is not of its index's dtype");
        }
        let size = if dtype == "F32" { 4 } else { 2 };
        let expected = rows.len() * dimension * size;
        let ints = |v: Option<&Value>| -> Option<Vec<Option<i64>>> { v.and_then(Value::as_arr).map(|a| a.iter().map(Value::as_int).collect()) };
        if ints(tensor.get("shape")) != Some(alloc::vec![Some(rows.len() as i64), Some(dimension as i64)]) {
            refuse!("an embeddings tensor's shape is not its rows and dimension");
        }
        if ints(tensor.get("data_offsets")) != Some(alloc::vec![Some(0), Some(expected as i64)]) {
            refuse!("an embeddings tensor's data offsets are not [0, its bytes]");
        }
        let data = &st[8 + length..];
        if data.len() != expected {
            refuse!("an embeddings tensor holds other than its tensor's bytes");
        }
        let bad = if size == 4 {
            data.chunks_exact(4).any(|c| u32::from_le_bytes([c[0], c[1], c[2], c[3]]) & 0x7F80_0000 == 0x7F80_0000)
        } else {
            data.chunks_exact(2).any(|c| u16::from_le_bytes([c[0], c[1]]) & 0x7C00 == 0x7C00)
        };
        if bad {
            refuse!("an embeddings tensor holds a NaN or infinite value");
        }
    }
    Ok(())
}

/// One pack (not its set members): every Core check, in the verification order of Core 10.
pub fn verify(ctx: &Ctx, files: &Files) -> R<Verified> {
    let reg = ctx.reg;
    for p in files.keys() {
        if !safe_path(&reg.devices, p) {
            refuse!("'{p}' is not a safe pack path");
        }
    }
    let mut folders: BTreeMap<String, String> = BTreeMap::new();
    for p in files.keys() {
        let parts: Vec<&str> = p.split('/').collect();
        for i in 1..parts.len() {
            let folder = parts[..i].join("/");
            let known = folders.entry(folder.to_ascii_lowercase()).or_insert_with(|| folder.clone());
            if *known != folder {
                refuse!("folders differ only by case: {known}, {folder}");
            }
        }
    }
    for p in files.keys() {
        if folders.contains_key(&p.to_ascii_lowercase()) {
            refuse!("'{p}' is both a file and a folder");
        }
    }
    let list_bytes = files.get(FILES).ok_or_else(|| refused!("there is no META/files.json"))?;
    if list_bytes.len() > MAX_FILES_LIST {
        refuse!("META/files.json is larger than {} MiB", MAX_FILES_LIST >> 20);
    }
    let listing = profile_integers(list_bytes)?;
    let l = listing.as_obj().ok_or_else(|| refused!("META/files.json is not a v1.1 files list"))?;
    if canonical(&listing).as_bytes() != list_bytes.as_slice() {
        refuse!("META/files.json is not canonical");
    }
    if !l.keys_are(reg.shape("files")) || !l.get("format").is_some_and(|f| f.is_str(FILES_FORMAT)) {
        refuse!("META/files.json is not a v1.1 files list");
    }
    let (pid, version) = match (l.str("id"), l.str("version")) {
        (Some(i), Some(v)) if valid_id(i) && valid_version(v) => (i.to_string(), v.to_string()),
        _ => refuse!("META/files.json names no valid id or version"),
    };
    let entries = l.get("files").and_then(Value::as_arr).ok_or_else(|| refused!("META/files.json has no files array"))?;
    let mut listed: BTreeMap<&str, (i64, &str)> = BTreeMap::new();
    let mut paths: Vec<&str> = Vec::new();
    for e in entries {
        let ok = e.as_obj().filter(|e| {
            e.keys_are(reg.shape("filesEntry"))
                && e.str("path").is_some()
                && e.get("length").and_then(Value::as_int).is_some_and(|n| n >= 0)
                && e.str("sha256").is_some_and(|h| hex_n(h, 64))
        });
        let e = ok.ok_or_else(|| refused!("META/files.json has a malformed entry"))?;
        let path = e.str("path").unwrap();
        listed.insert(path, (e.get("length").and_then(Value::as_int).unwrap(), e.str("sha256").unwrap()));
        paths.push(path);
    }
    if paths.windows(2).any(|w| w[0] >= w[1]) {
        refuse!("META/files.json is not sorted or repeats a path");
    }
    let content: BTreeMap<&str, &Vec<u8>> = files
        .iter()
        .filter(|(p, _)| p.as_str() != FILES && !p.starts_with(SIGNATURES) && !p.starts_with(ATTESTATIONS))
        .map(|(p, b)| (p.as_str(), b))
        .collect();
    if content.len() != listed.len() || !content.keys().all(|p| listed.contains_key(p)) {
        refuse!("the pack does not hold exactly the listed files");
    }
    attestation_checks(files)?;
    for (p, (length, sha)) in &listed {
        let b = content[p];
        if b.len() as i64 != *length || crypto::sha256_hex(b) != *sha {
            refuse!("'{p}' does not match META/files.json");
        }
    }
    let list_hex = crypto::sha256_hex(list_bytes);
    let envelopes: Vec<&str> = files.keys().filter(|p| p.starts_with(SIGNATURES) && !p.ends_with(TIMESTAMP_SUFFIX)).map(|p| p.as_str()).collect();
    if envelopes.len() > MAX_ENVELOPES {
        refuse!("the pack carries {} signature envelopes; at most {MAX_ENVELOPES}", envelopes.len());
    }
    for (token, data) in files.iter().filter(|(p, _)| p.starts_with(SIGNATURES) && p.ends_with(TIMESTAMP_SUFFIX)) {
        if !envelopes.contains(&&token[..token.len() - TIMESTAMP_SUFFIX.len()]) {
            refuse!("{token} is a timestamp token beside no envelope");
        }
        if data.len() > MAX_ENVELOPE {
            refuse!("{token} is larger than 64 KiB");
        }
    }
    let mut roles: BTreeSet<String> = BTreeSet::new();
    let mut publisher_keys: BTreeSet<String> = BTreeSet::new();
    let mut reasons: Vec<String> = Vec::new();
    for path in &envelopes {
        let data = &files[*path];
        if data.len() > MAX_ENVELOPE {
            refuse!("{path} is larger than 64 KiB");
        }
        let env = profile(data).map_err(|_| refused!("{path} is not a JSON envelope"))?;
        let shaped = env.as_obj().is_some_and(|o| {
            o.get("payloadType").is_some_and(|t| t.is_str(crypto::PAYLOAD_TYPE)) && o.str("payload").is_some() && o.get("signatures").and_then(Value::as_arr).is_some()
        });
        if !shaped {
            refuse!("{path} is not a DSSE envelope of an in-toto statement");
        }
        let o = env.as_obj().unwrap();
        let sigs = o.get("signatures").and_then(Value::as_arr).unwrap();
        if sigs.len() > MAX_SIGNATURES {
            refuse!("{path} holds more than {MAX_SIGNATURES} signatures");
        }
        if sigs.is_empty() {
            refuse!("{path} holds no signature");
        }
        if !o.keys_are(reg.shape("envelope")) {
            refuse!("{path} holds a field a DSSE envelope does not have");
        }
        if !sigs.iter().all(|s| entry_shaped(reg, s)) {
            refuse!("{path} has a signature entry that is not a key id and a signature");
        }
        let payload = b64::decode(o.str("payload").unwrap()).ok_or_else(|| refused!("{path} has a payload that is not base64"))?;
        let not_this = || refused!("{path} does not sign this pack");
        let st = profile(&payload).map_err(|_| not_this())?;
        let (role, signed_at) = (|| -> Option<(String, Ticks)> {
            let st = st.as_obj()?;
            let pred = st.get("predicate")?.as_obj()?;
            if !st.keys_are(reg.shape("statement")) || !pred.keys_are(reg.shape("signaturePredicate")) {
                return None;
            }
            let subject = st.get("subject")?.as_arr()?;
            let first = subject.first()?.as_obj()?;
            if !first.keys_are(reg.shape("subject")) {
                return None;
            }
            let digest = first.get("digest")?.as_obj()?;
            if digest.len() != 1 || !digest.has("sha256") {
                return None;
            }
            let role = pred.str("role")?;
            let ok = st.get("_type")?.is_str(STATEMENT)
                && subject.len() == 1
                && first.get("name")?.is_str(FILES)
                && digest.get("sha256")?.is_str(&list_hex)
                && holds(&reg.signing_roles, role)
                && st.get("predicateType")?.is_str(&alloc::format!("urn:agentmemory:pack:{role}:v1"))
                && pred.get("id")?.is_str(&pid)
                && pred.get("version")?.is_str(&version);
            let signed_at = utc_second(pred.str("signedAt")?)?;
            ok.then(|| (role.to_string(), signed_at))
        })()
        .ok_or_else(not_this)?;
        let digest = crypto::pae_digest(crypto::PAYLOAD_TYPE, &payload);
        for (key, _) in holding(ctx.policy, sigs, &digest) {
            if let Some(why) = ctx.policy.key_refusal(key, &role, &pid, signed_at, ctx.now) {
                reasons.push(alloc::format!("{path}: {why}"));
                continue;
            }
            roles.insert(role.clone());
            if role == "publisher" {
                publisher_keys.insert(key.key_id.clone());
            }
        }
    }
    let missing: Vec<&String> = ctx.policy.require_roles.iter().filter(|r| !roles.contains(*r)).collect::<BTreeSet<_>>().into_iter().collect();
    if !missing.is_empty() {
        let names: Vec<&str> = missing.iter().map(|s| s.as_str()).collect();
        let mut why = alloc::format!("no valid signature in role {}", names.join(", "));
        for r in &reasons {
            why.push_str("; ");
            why.push_str(r);
        }
        return Err(Refused(why));
    }
    // Once the signatures hold: the JSON profile on every listed JSON file, pack.json, the kind, the card, the lifetime.
    for (p, b) in &content {
        if p.ends_with(".json") {
            if b.len() > MAX_JSON {
                refuse!("'{p}' is larger than {} MiB", MAX_JSON >> 20);
            }
            if *p == LOCK {
                profile_integers(b)?;
            } else {
                profile(b)?;
            }
        }
    }
    let manifest = match content.get("pack.json") {
        Some(b) => profile(b)?,
        None => Value::Null,
    };
    let manifest = match manifest {
        Value::Obj(o) => o,
        _ => refuse!("pack.json is not an object"),
    };
    let minor = format_minor(manifest.get("format"));
    let mut unknown: Vec<&str> = manifest.keys().filter(|k| !holds(&reg.manifest_fields, k)).collect();
    unknown.sort();
    if !unknown.is_empty() && !minor.is_some_and(|m| m > PACK_MINOR) {
        refuse!("pack.json has an unknown field '{}'", unknown[0]);
    }
    critical_fields(reg, &manifest)?;
    if minor.is_none() {
        refuse!("pack.json has a format this verifier does not read; it knows {PACK_FORMAT} and reads the later 1.x minors");
    }
    if !manifest.get("id").is_some_and(|v| v.is_str(&pid)) || !manifest.get("version").is_some_and(|v| v.is_str(&version)) {
        refuse!("pack.json does not name the same pack as META/files.json");
    }
    let empty_obj = Obj::default();
    let extensions = match manifest.get("extensions") {
        None => &empty_obj,
        Some(Value::Obj(e)) => e,
        Some(_) => refuse!("pack.json has a malformed 'extensions'"),
    };
    let critical = match extensions.get("critical") {
        None => Vec::new(),
        Some(c) => match c.as_strs() {
            Some(c) if c.iter().collect::<BTreeSet<_>>().len() == c.len() => c,
            _ => refuse!("pack.json has a malformed 'extensions.critical'"),
        },
    };
    for name in &critical {
        if *name == "critical" || !extensions.has(name) {
            refuse!("pack.json marks '{name}' critical, but holds no such extension");
        }
        if !holds(&reg.extensions, name) {
            refuse!("pack.json marks the extension '{name}' critical, and this verifier does not understand it");
        }
    }
    let federation = read_federation(reg, extensions)?;
    manifest_checks(ctx, &manifest, &content)?;
    if let Some(card) = content.get(CARD) {
        read_card(reg, card)?;
    }
    if let Some(d) = content.get(reg.derivation_path.as_str()) {
        read_derivation(reg, d)?;
    }
    let usage = match content.get(reg.policy_path.as_str()) {
        Some(p) => Some(read_policy(reg, p)?),
        None => None,
    };
    let digest = alloc::format!("sha256:{list_hex}");
    if ctx.policy.revoked_digests.contains(&digest) {
        refuse!("the pack is revoked");
    }
    if ctx.policy.revoked_expires.is_some_and(|e| e < ctx.now) {
        refuse!("the host's revocation list has expired");
    }
    if let Some(f) = &federation {
        let bound: Vec<Option<&str>> = publisher_keys.iter().map(|k| ctx.policy.key(k).and_then(|k| k.web_id.as_deref())).collect();
        if bound.iter().any(|w| w.is_some_and(|w| w != f.publisher)) {
            refuse!("a key bound to another WebID signed the pack as publisher");
        }
        if !bound.contains(&Some(f.publisher.as_str())) {
            refuse!("no publisher signature from a key this host bound to the pack's publisher");
        }
        if let Some(a) = &f.audience {
            if !ctx.policy.host.iter().any(|h| holds(a, h)) {
                refuse!("the pack is not published for this host");
            }
        }
    }
    if let Some(rules) = &usage {
        if !policy_allows(rules, "read", &ctx.policy.host, ctx.now) {
            refuse!("the pack's usage policy (policy.json) does not let this host read it now");
        }
    }
    for (p, b) in &content {
        if p.ends_with(".jsonl") && !b.is_empty() {
            if b.last() != Some(&b'\n') {
                refuse!("'{p}' does not end with a line feed");
            }
            for line in b[..b.len() - 1].split(|&c| c == b'\n') {
                if line.is_empty() {
                    refuse!("'{p}' has an empty line");
                }
                if line.len() > MAX_JSON {
                    refuse!("'{p}' has a line longer than {} MiB", MAX_JSON >> 20);
                }
                profile(line)?;
            }
        }
    }
    read_embeddings(reg, &content)?;
    Ok(Verified { id: pid, version, digest, manifest })
}

/// A pack, or a pack set and every member on its own (v1.2 5).
pub fn verify_any(ctx: &Ctx, files: &Files) -> R<Verified> {
    let v = verify(ctx, files)?;
    if !v.manifest.get("kind").is_some_and(|k| k.is_str("set")) {
        return Ok(v);
    }
    let mut members: BTreeMap<String, Files> = BTreeMap::new();
    for (path, data) in files {
        let rest = match path.strip_prefix("packs/") {
            Some(r) => r,
            None => continue,
        };
        match rest.split_once('/') {
            Some((m, inner)) if !m.is_empty() && !inner.is_empty() => {
                members.entry(m.to_string()).or_default().insert(inner.to_string(), data.clone());
            }
            _ => refuse!("'{path}' is not inside a member folder"),
        }
    }
    let mut contains: Vec<&str> = v.manifest.get("contains").and_then(Value::as_strs).unwrap_or_default();
    contains.sort();
    if members.keys().map(|s| s.as_str()).collect::<Vec<_>>() != contains {
        refuse!("the set's pack.json names other members than it carries");
    }
    if v.manifest.str("id").is_some_and(|id| members.contains_key(id)) {
        refuse!("the set carries a member with its own id");
    }
    let empty_obj = Obj::default();
    let ext = |m: &Obj| -> Obj { m.get("extensions").and_then(Value::as_obj).cloned().unwrap_or_else(|| empty_obj.clone()) };
    let federation = read_federation(ctx.reg, &ext(&v.manifest))?;
    for (member, member_files) in &members {
        let inner = verify(ctx, member_files).map_err(|e| refused!("packs/{member}/: {}", e.0))?;
        if inner.id != *member {
            refuse!("the member in packs/{member}/ is {}", inner.id);
        }
        if inner.manifest.get("kind").is_some_and(|k| k.is_str("set")) {
            refuse!("packs/{member}/ is a pack set: a set carries packs, not sets");
        }
        if let Some(f) = &federation {
            let own = match read_federation(ctx.reg, &ext(&inner.manifest))? {
                Some(o) => o,
                None => refuse!("packs/{member}/: a member of a federated set is not federated itself"),
            };
            if own.publisher != f.publisher {
                refuse!("packs/{member}/: the member names another publisher than its set");
            }
            if let Some(a) = &f.audience {
                if own.audience.as_ref().map_or(true, |o| o.iter().any(|x| !holds(a, x))) {
                    refuse!("packs/{member}/: the member is published for a wider audience than its set");
                }
            }
        }
    }
    Ok(v)
}

/// The profiles a pack uses, in the registry's order, derived from what it holds; and those this reader does not implement.
pub fn profiles_used(reg: &Registry, manifest: &Obj, files: &Files) -> Vec<(String, bool)> {
    let paths: Vec<&str> = files.keys().map(|p| p.as_str()).filter(|p| !p.starts_with(SIGNATURES) && !p.starts_with(ATTESTATIONS)).collect();
    let mut used = Vec::new();
    for p in &reg.profiles {
        let by_file = p.files.iter().any(|f| paths.iter().any(|path| if f.ends_with('/') { path.starts_with(f.as_str()) } else { path == f }));
        let by_field = p.fields.iter().any(|f| manifest.has(f));
        let by_kind = manifest.str("kind").is_some_and(|k| holds(&p.kinds, k));
        let by_ext = manifest.get("extensions").and_then(Value::as_obj).is_some_and(|e| p.extensions.iter().any(|x| e.has(x)));
        let by_value = p.values.iter().any(|(field, values)| {
            let v = manifest.get(field).and_then(Value::as_str).or(reg.default_of(field));
            v.is_some_and(|v| holds(values, v))
        });
        if by_file || by_field || by_kind || by_ext || by_value {
            used.push((p.name.clone(), p.implemented));
        }
    }
    used
}

/// A verified archive: its files, the pack, and the profiles it uses.
pub struct Outcome {
    pub files: Files,
    pub verified: Verified,
    pub profiles: Vec<(String, bool)>,
}

/// Read and verify an archive against a policy at a time.
pub fn verify_files(ctx: &Ctx, archive: &[u8]) -> R<Outcome> {
    let files = read_zip(&ctx.reg.devices, archive)?;
    let verified = verify_any(ctx, &files)?;
    let profiles = profiles_used(ctx.reg, &verified.manifest, &files);
    Ok(Outcome { files, verified, profiles })
}

/// Verified, with what it is.
pub use Outcome as VerifiedArchive;

/// The result of `verify_archive`.
pub use Verified as Pack;

/// Read and verify an archive with the policy's bytes, at `now`.
pub fn verify_archive(archive: &[u8], policy: &[u8], revocations: &[Vec<u8>], now: Ticks) -> R<Outcome> {
    let reg = Registry::load()?;
    let mut pol = crate::trust::load_policy(&reg, policy)?;
    if !revocations.is_empty() {
        pol = crate::trust::apply_all(&reg, &pol, revocations, now)?;
    }
    let ctx = Ctx { reg: &reg, policy: &pol, now };
    verify_files(&ctx, archive)
}

/// What an archive says about itself, read in the archive profile, before (and without) any trust decision.
pub struct Inspection {
    pub files: usize,
    pub bytes: u64,
    pub id: Option<String>,
    pub version: Option<String>,
    pub kind: Option<String>,
    pub digest: Option<String>,
    pub envelopes: usize,
    pub title: Option<String>,
}

/// Inspect an archive: the strict archive profile and META/files.json; no signature is judged.
pub fn inspect(archive: &[u8]) -> R<Inspection> {
    let reg = Registry::load()?;
    let files = read_zip(&reg.devices, archive)?;
    let bytes = files.values().map(|b| b.len() as u64).sum();
    let listing = files.get(FILES).and_then(|b| profile_integers(b).ok());
    let manifest = files.get("pack.json").and_then(|b| profile(b).ok());
    let m = manifest.as_ref().and_then(Value::as_obj);
    let l = listing.as_ref().and_then(Value::as_obj);
    Ok(Inspection {
        files: files.len(),
        bytes,
        id: l.and_then(|l| l.str("id")).or_else(|| m.and_then(|m| m.str("id"))).map(String::from),
        version: l.and_then(|l| l.str("version")).or_else(|| m.and_then(|m| m.str("version"))).map(String::from),
        kind: m.and_then(|m| m.str("kind")).map(String::from),
        digest: files.get(FILES).map(|b| alloc::format!("sha256:{}", crypto::sha256_hex(b))),
        envelopes: files.keys().filter(|p| p.starts_with(SIGNATURES) && !p.ends_with(TIMESTAMP_SUFFIX)).count(),
        title: m.and_then(|m| m.get("display")).and_then(Value::as_obj).and_then(|d| d.str("title")).map(String::from),
    })
}
