// SPDX-License-Identifier: Apache-2.0
//! The host's trust policy (host/trust-policy.md, Core 9): keys with scopes, roles and windows, revoked digests and keys,
//! the host's WebIDs and required roles; and the revocation documents of one run applied to it.

use alloc::collections::BTreeSet;
use alloc::string::{String, ToString};
use alloc::vec;
use alloc::vec::Vec;
use p256::ecdsa::VerifyingKey;

use crate::json::{profile, Obj, Value};
use crate::registry::{holds, Registry};
use crate::text::{hex_n, is_digest, valid_prefix, valid_webid};
use crate::time::{instant, Ticks, TICKS_PER_SECOND};
use crate::{b64, crypto, refuse, refused, R};

pub const MAX_POLICY: usize = 4 << 20;
pub const MAX_REVOCATIONS: usize = 4 << 20;
pub const MAX_SIGNATURES: usize = 16;
pub const MAX_ENVELOPE: usize = 64 * 1024;
pub const MAX_CONFIDENCE_MICRO: i64 = 1_000_000;
pub const POLICY_FORMAT: &str = "agentmemory.pack.trust/1.1";
pub const REVOCATIONS_FORMAT: &str = "agentmemory.pack.revocations/1.2";
pub const REVOCATIONS_TYPE: &str = "application/vnd.agentmemory.revocations+json";
/// The tolerance for a time claimed in the future (five minutes).
pub const SKEW: Ticks = 300 * TICKS_PER_SECOND;

#[derive(Clone)]
pub struct Key {
    pub key_id: String,
    pub roles: Vec<String>,
    pub prefixes: Vec<String>,
    pub not_before: Option<Ticks>,
    pub not_after: Option<Ticks>,
    pub web_id: Option<String>,
    pub public: VerifyingKey,
}

#[derive(Clone)]
pub struct Policy {
    pub keys: Vec<Key>,
    pub revoked_digests: BTreeSet<String>,
    pub revoked_keys: BTreeSet<String>,
    pub revoked_expires: Option<Ticks>,
    pub host: Vec<String>,
    pub require_roles: Vec<String>,
}

impl Policy {
    pub fn key(&self, id: &str) -> Option<&Key> {
        self.keys.iter().find(|k| k.key_id == id)
    }

    /// Why a signature by `key` in `role` for the pack `pid`, signed at `signed_at`, does not count; or None.
    pub fn key_refusal(&self, key: &Key, role: &str, pid: &str, signed_at: Ticks, now: Ticks) -> Option<&'static str> {
        if !holds(&key.roles, role) {
            return Some("the key may not sign in this role");
        }
        let scoped = key.prefixes.iter().any(|pre| {
            pid == pre || if pre.ends_with('.') { pid.starts_with(pre.as_str()) } else { pid.starts_with(&alloc::format!("{pre}.")) }
        });
        if !scoped {
            return Some("the key may not sign for this pack id");
        }
        if let Some(nb) = key.not_before {
            if signed_at < nb || now < nb {
                return Some("the key was not yet valid");
            }
        }
        if let Some(na) = key.not_after {
            if signed_at > na {
                return Some("the key had expired at the signing time");
            }
            if now > na {
                return Some("the key has expired; without a verified timestamp token its signatures no longer count");
            }
        }
        if signed_at > now + SKEW {
            return Some("the signature claims a time in the future");
        }
        if self.revoked_keys.contains(&key.key_id) {
            return Some("the key is revoked");
        }
        None
    }

    fn valid_now(&self, id: &str, now: Ticks) -> bool {
        match self.key(id) {
            None => true,
            Some(k) => !(k.not_before.is_some_and(|nb| now < nb)) && !(k.not_after.is_some_and(|na| now > na)),
        }
    }

    fn expired(&self, id: &str, now: Ticks) -> bool {
        self.key(id).and_then(|k| k.not_after).is_some_and(|na| now > na)
    }
}

/// An object holding only the fields named.
fn closed(v: &Value, fields: &[String], what: &str) -> R<()> {
    let o = v.as_obj().ok_or_else(|| refused!("{what} is not an object"))?;
    if let Some(k) = o.keys().find(|k| !holds(fields, k)) {
        refuse!("{what} holds a field the trust policy does not define: '{k}'");
    }
    Ok(())
}

/// A time of the policy: absent, or an RFC 3339 date-time; null is not absent.
fn present_time(o: &Obj, name: &str) -> R<Option<Ticks>> {
    match o.get(name) {
        None => Ok(None),
        Some(v) => v.as_str().and_then(instant).map(Some).ok_or_else(|| refused!("the trust policy has a time that is not an RFC 3339 date-time")),
    }
}

/// The host's trust policy, as the other verifiers read it: a BOM and CRLF are tolerated, the JSON profile holds, every
/// level is closed, every key is checked.
pub fn load_policy(reg: &Registry, raw: &[u8]) -> R<Policy> {
    let text = core::str::from_utf8(raw).map_err(|_| refused!("the trust policy is not UTF-8"))?;
    let text = text.strip_prefix('\u{feff}').unwrap_or(text);
    let text = text.replace("\r\n", "\n");
    let policy = profile(text.as_bytes())?;
    closed(&policy, reg.shape("trustPolicy"), "the trust policy")?;
    let p = policy.as_obj().unwrap();
    if !p.get("format").is_some_and(|f| f.is_str(POLICY_FORMAT)) {
        refuse!("the trust policy must name format '{POLICY_FORMAT}'");
    }
    let mut keys: Vec<Key> = Vec::new();
    let empty = Value::Arr(Vec::new());
    let key_values: Vec<Value> = match p.get("keys").unwrap_or(&empty) {
        Value::Arr(a) => a.clone(),
        _ => vec![Value::Null],
    };
    for kv in &key_values {
        let k = match kv.as_obj() {
            Some(k) if k.str("keyId").is_some() && k.str("publicKey").is_some() => k,
            _ => refuse!("the trust policy has a malformed key"),
        };
        closed(kv, reg.shape("trustKey"), "a trusted key")?;
        let prefixes = match k.get("idPrefixes").unwrap_or(&empty).as_strs() {
            Some(p) if !p.is_empty() => p,
            _ => refuse!("a trusted key names no pack id prefixes"),
        };
        if !prefixes.iter().all(|p| p.chars().count() <= 64 && valid_prefix(p)) {
            refuse!("a trusted key names something that is not a pack id prefix");
        }
        let roles = match k.get("roles").unwrap_or(&empty).as_strs() {
            Some(r) if !r.is_empty() && r.iter().all(|x| holds(&reg.key_roles, x)) => r,
            _ => refuse!("a trusted key needs known roles"),
        };
        if roles.contains(&"revoker") && roles.len() > 1 {
            refuse!("a revoker key holds that role alone");
        }
        if roles.contains(&"evaluator") && roles.len() > 1 {
            refuse!("an evaluator key holds that role alone");
        }
        match k.get("confidenceMicro") {
            None => {}
            Some(Value::Int(m)) if (0..=MAX_CONFIDENCE_MICRO).contains(m) => {}
            _ => refuse!("a trusted key's confidenceMicro is an integer from 0 to 1000000"),
        }
        if k.has("confidenceMicro") && !roles.iter().any(|r| holds(&reg.signing_roles, r)) {
            refuse!("a trusted key that signs no pack carries no confidenceMicro");
        }
        let pem = k.str("publicKey").unwrap();
        if !crypto::public_pem_shaped(pem) {
            refuse!("a trusted key's publicKey is not one PEM 'PUBLIC KEY' block and nothing else");
        }
        let der = crypto::canonical_p256_der(pem)
            .ok_or_else(|| refused!("a trusted key's publicKey is not a P-256 key in its one form (an uncompressed point, canonical base64)"))?;
        let public = crypto::key_from_der(&der).ok_or_else(|| refused!("a trusted key is not a PEM public key"))?;
        let key_id = k.str("keyId").unwrap();
        if crypto::sha256_hex(&der) != key_id {
            refuse!("a trusted key's id is not the SHA-256 of its key");
        }
        if keys.iter().any(|x| x.key_id == key_id) {
            refuse!("the trust policy lists a key twice");
        }
        let web_id = match k.get("webId") {
            None => None,
            Some(w) => {
                let w = w.as_str().filter(|w| valid_webid(w)).ok_or_else(|| refused!("a trusted key's webId is not a WebID"))?;
                if roles.contains(&"revoker") {
                    refuse!("a revoker key is never bound to a WebID");
                }
                Some(w.to_string())
            }
        };
        let not_before = present_time(k, "notBefore")?;
        let not_after = present_time(k, "notAfter")?;
        keys.push(Key {
            key_id: key_id.to_string(),
            roles: roles.iter().map(|s| s.to_string()).collect(),
            prefixes: prefixes.iter().map(|s| s.to_string()).collect(),
            not_before,
            not_after,
            web_id,
            public,
        });
    }
    let empty_obj = Value::Obj(Obj::default());
    let revoked = p.get("revoked").unwrap_or(&empty_obj);
    if revoked.as_obj().is_none() {
        refuse!("the trust policy has a malformed revoked section");
    }
    closed(revoked, reg.shape("trustRevoked"), "'revoked'")?;
    let r = revoked.as_obj().unwrap();
    let digests = r.get("digests").unwrap_or(&empty).as_strs().ok_or_else(|| refused!("the trust policy has a malformed revoked digest list"))?;
    if !digests.iter().all(|d| is_digest(d)) {
        refuse!("a revoked digest is not sha256: and 64 lowercase hexadecimal digits");
    }
    let mut revoked_keys = BTreeSet::new();
    let rk: Vec<Value> = match r.get("keys").unwrap_or(&empty) {
        Value::Arr(a) => a.clone(),
        _ => vec![Value::Null],
    };
    for kv in &rk {
        let k = match kv.as_obj() {
            Some(k) if k.str("keyId").is_some() => k,
            _ => refuse!("the trust policy has a malformed revoked key"),
        };
        closed(kv, reg.shape("revokedKey"), "a revoked key")?;
        if !hex_n(k.str("keyId").unwrap(), 64) {
            refuse!("a revoked key id is not 64 lowercase hexadecimal digits");
        }
        present_time(k, "compromisedAt")?;
        revoked_keys.insert(k.str("keyId").unwrap().to_string());
    }
    let revoked_expires = present_time(r, "expires")?;
    let mut host = Vec::new();
    if let Some(h) = p.get("host") {
        let ok = h.as_obj().filter(|o| o.len() == 1).and_then(|o| o.get("webIds")).and_then(Value::as_strs).filter(|w| {
            (1..=16).contains(&w.len()) && w.iter().all(|x| valid_webid(x)) && w.iter().collect::<BTreeSet<_>>().len() == w.len()
        });
        match ok {
            Some(w) => host = w.iter().map(|s| s.to_string()).collect(),
            None => refuse!("'host' is an object holding only 'webIds': 1 to 16 distinct WebIDs"),
        }
    }
    let mut require_roles = vec!["publisher".to_string()];
    if let Some(rr) = p.get("requireRoles") {
        match rr.as_strs() {
            Some(r) if !r.is_empty() && r.iter().all(|x| holds(&reg.signing_roles, x)) => require_roles = r.iter().map(|s| s.to_string()).collect(),
            _ => refuse!("requireRoles must name known roles"),
        }
    }
    Ok(Policy {
        keys,
        revoked_digests: digests.iter().map(|s| s.to_string()).collect(),
        revoked_keys,
        revoked_expires,
        host,
        require_roles,
    })
}

/// A DSSE signature entry as the format writes it: a key id of 64 lowercase hex and a signature of strict base64 decoding
/// to 1 to 72 bytes, nothing else.
pub fn entry_shaped(reg: &Registry, s: &Value) -> bool {
    s.as_obj().is_some_and(|o| {
        o.keys_are(reg.shape("signatureEntry"))
            && o.str("keyid").is_some_and(|k| hex_n(k, 64))
            && o.str("sig").and_then(b64::decode).is_some_and(|b| !b.is_empty() && b.len() <= 72)
    })
}

/// The key ids whose signatures over `digest` hold, among the entries of an envelope, with their keys.
pub fn holding<'p>(policy: &'p Policy, entries: &[Value], digest: &[u8; 32]) -> Vec<(&'p Key, String)> {
    let mut out = Vec::new();
    for e in entries {
        let o = match e.as_obj() {
            Some(o) => o,
            None => continue,
        };
        let (id, sig) = match (o.str("keyid"), o.str("sig")) {
            (Some(i), Some(s)) => (i, s),
            _ => continue,
        };
        let key = match policy.key(id) {
            Some(k) => k,
            None => continue,
        };
        let sig = match b64::decode(sig) {
            Some(s) => s,
            None => continue,
        };
        if crypto::verify_prehash(&key.public, digest, &sig) {
            out.push((key, id.to_string()));
        }
    }
    out
}

struct RevocationList {
    signers: BTreeSet<String>,
    issued: Ticks,
    expires: Ticks,
    digests: Vec<String>,
    keys: Vec<String>,
    identity: [u8; 32],
}

fn read_revocations(reg: &Registry, policy: &Policy, data: &[u8], now: Ticks) -> R<RevocationList> {
    if data.len() > MAX_REVOCATIONS {
        refuse!("the revocation document is larger than {} MiB", MAX_REVOCATIONS >> 20);
    }
    let env = profile(data)?;
    let shaped = env.as_obj().is_some_and(|o| {
        o.get("payloadType").is_some_and(|t| t.is_str(REVOCATIONS_TYPE))
            && o.str("payload").is_some()
            && o.get("signatures").and_then(Value::as_arr).is_some_and(|s| s.iter().all(|e| entry_shaped(reg, e)))
            && o.keys_are(reg.shape("envelope"))
    });
    if !shaped {
        refuse!("the revocation document is not a DSSE envelope of a revocation document");
    }
    let o = env.as_obj().unwrap();
    let payload = b64::decode(o.str("payload").unwrap()).ok_or_else(|| refused!("the revocation document has a payload that is not base64"))?;
    let entries = o.get("signatures").and_then(Value::as_arr).unwrap();
    if entries.len() > MAX_SIGNATURES {
        refuse!("the revocation document holds more than {MAX_SIGNATURES} signatures");
    }
    let digest = crypto::pae_digest(REVOCATIONS_TYPE, &payload);
    let signers: BTreeSet<String> =
        holding(policy, entries, &digest).into_iter().filter(|(k, _)| holds(&k.roles, "revoker")).map(|(k, _)| k.key_id.clone()).collect();
    if signers.is_empty() {
        refuse!("the revocation document is not signed by a key the trust policy names as revoker");
    }
    let doc = profile(&payload)?;
    let d = match doc.as_obj() {
        Some(d) if d.keys_within(reg.shape("revocations")) && d.get("format").is_some_and(|f| f.is_str(REVOCATIONS_FORMAT)) => d,
        _ => refuse!("the revocation document is not a v1.2 revocation document"),
    };
    let issued = d.str("issuedAt").and_then(instant);
    let expires = d.str("expires").and_then(instant);
    let (issued, expires) = match (issued, expires) {
        (Some(i), Some(e)) if e > i => (i, e),
        _ => refuse!("the revocation document needs issuedAt and expires, expiring after it was issued"),
    };
    // As the other verifiers compare it: to the microsecond.
    if issued / 10 > (now + SKEW) / 10 {
        refuse!("the revocation document claims to be issued in the future");
    }
    let digests = match d.get("digests").and_then(Value::as_strs) {
        Some(ds) if ds.iter().all(|x| is_digest(x)) => ds.iter().map(|s| s.to_string()).collect(),
        _ => refuse!("the revocation document has a malformed digests list"),
    };
    let mut keys = Vec::new();
    let ok = d.get("keys").and_then(Value::as_arr).is_some_and(|ks| {
        ks.iter().all(|k| {
            k.as_obj().is_some_and(|k| {
                let good = k.keys_within(reg.shape("revokedKey"))
                    && k.str("keyId").is_some_and(|i| hex_n(i, 64))
                    && (!k.has("compromisedAt") || k.str("compromisedAt").and_then(instant).is_some());
                if good {
                    keys.push(k.str("keyId").unwrap().to_string());
                }
                good
            })
        })
    });
    if !ok {
        refuse!("the revocation document has a malformed keys list");
    }
    Ok(RevocationList { signers, issued, expires, digests, keys, identity: digest })
}

/// The revocation documents of one run applied to the policy, by the rule the other verifiers keep:
/// - every authentic list from a key the policy names as revoker denies the packs and non-revoker keys it names;
/// - only a list with a live signer (valid now, revoked by nobody: the policy, or another counted list) revokes revokers
///   and brings freshness, the newest such list's expiry, capped by the policy's own;
/// - a list revokes a key against its will only when a live signer other than that key signed it;
/// - a list without a live signer that revokes a revoker that has not expired, which no counted list revokes, refuses
///   the run; lists that revoke each other's signers in a circle refuse it too;
/// - the same list twice is one list.
pub fn apply_all(reg: &Registry, policy: &Policy, documents: &[Vec<u8>], now: Ticks) -> R<Policy> {
    if documents.len() > 64 {
        refuse!("the revocation documents of this run are more than 64");
    }
    let mut read: Vec<RevocationList> = Vec::new();
    for data in documents {
        let doc = read_revocations(reg, policy, data, now)?;
        if let Some(known) = read.iter_mut().find(|r| r.identity == doc.identity) {
            known.signers.extend(doc.signers);
        } else {
            read.push(doc);
        }
    }
    let revoked_before = &policy.revoked_keys;
    let revoker_ids: BTreeSet<&str> = policy.keys.iter().filter(|k| holds(&k.roles, "revoker")).map(|k| k.key_id.as_str()).collect();
    let live: Vec<Vec<String>> = read
        .iter()
        .map(|r| r.signers.iter().filter(|s| !revoked_before.contains(*s) && policy.valid_now(s, now)).cloned().collect())
        .collect();
    let revokes_other = |j: usize, key_id: &str| read[j].keys.iter().any(|k| k == key_id) && live[j].iter().any(|s| s != key_id);
    // 0 open, 1 counts, 2 without a live signer.
    let mut state = vec![0u8; read.len()];
    let mut changed = true;
    while changed {
        changed = false;
        for i in 0..read.len() {
            if state[i] != 0 {
                continue;
            }
            let revoked_by = |signer: &str, which: &dyn Fn(u8) -> bool, state: &[u8]| {
                (0..read.len()).any(|j| j != i && which(state[j]) && revokes_other(j, signer))
            };
            if live[i].iter().any(|s| !revoked_by(s, &|st| st != 2, &state)) {
                state[i] = 1;
                changed = true;
            } else if live[i].iter().all(|s| revoked_by(s, &|st| st == 1, &state)) {
                state[i] = 2;
                changed = true;
            }
        }
    }
    if state.contains(&0) {
        refuse!("the revocation documents of this run revoke their own signers in a circle");
    }
    let mut revoked_now: BTreeSet<String> = revoked_before.clone();
    for j in 0..read.len() {
        if state[j] == 1 {
            for k in &read[j].keys {
                if revokes_other(j, k) {
                    revoked_now.insert(k.clone());
                }
            }
        }
    }
    for i in 0..read.len() {
        if state[i] == 2 && read[i].keys.iter().any(|k| revoker_ids.contains(k.as_str()) && !revoked_now.contains(k) && !policy.expired(k, now)) {
            refuse!("a revocation document without a live signer revokes a revoker that has not expired; a live revoker must carry it forward");
        }
    }
    let mut merged = policy.clone();
    let mut newest: Option<(Ticks, Ticks)> = None;
    for (i, r) in read.iter().enumerate() {
        let counts = state[i] == 1;
        merged.revoked_digests.extend(r.digests.iter().cloned());
        for k in &r.keys {
            if counts || !revoker_ids.contains(k.as_str()) {
                merged.revoked_keys.insert(k.clone());
            }
        }
        if counts && newest.map_or(true, |(ni, ne)| r.issued > ni || (r.issued == ni && r.expires < ne)) {
            newest = Some((r.issued, r.expires));
        }
    }
    if let Some((_, e)) = newest {
        if policy.revoked_expires.map_or(true, |s| e < s) {
            merged.revoked_expires = Some(e);
        }
    }
    Ok(merged)
}
