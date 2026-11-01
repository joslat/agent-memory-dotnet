//! The documents beside a verified pack: an evaluation certificate (Certify profile, v1.2 16) with its AEF run, a
//! publication receipt (Federation profile 5), and an erasure (Core 17a).

use alloc::collections::BTreeSet;
use alloc::string::{String, ToString};
use alloc::vec::Vec;

use crate::json::{profile, profile_integers, Obj, Value};
use crate::pack::{read_audience, Ctx, Federation, Verified, ATTESTATIONS, FILES, STATEMENT};
use crate::registry::holds;
use crate::text::{hex_n, is_digest, valid_id, valid_version, valid_webid};
use crate::time::{civil, utc_second, Ticks};
use crate::trust::{entry_shaped, holding, MAX_ENVELOPE, MAX_SIGNATURES};
use crate::zip::Files;
use crate::{b64, crypto, refuse, refused, R};

pub const CERTIFICATE: &str = "urn:agentmemory:pack:certificate:v1";
pub const PUBLICATION: &str = "urn:agentmemory:pack:publication:v1";
pub const ERASURE: &str = "urn:agentmemory:pack:erasure:v1";
const MINIMUM_TASKS: i64 = 10;
const HISTORY_WINDOW: i64 = 2000;
const MAX_TASKS: i64 = 200;
const MAX_OFFERED: i64 = 16;
const MAX_SPACING: i64 = 2000;
const MAX_TASK_BYTES: i64 = 1_000_000_000;
const MAX_ERASED: usize = 256;

/// A DSSE envelope of an in-toto statement with 1 to 16 shaped entries: its payload and entries.
fn envelope(ctx: &Ctx, bytes: &[u8], what: &str) -> R<(Vec<u8>, Vec<Value>)> {
    if bytes.len() > MAX_ENVELOPE {
        refuse!("{what} is larger than 64 KiB");
    }
    let env = profile(bytes)?;
    let ok = env.as_obj().filter(|o| {
        o.keys_are(ctx.reg.shape("envelope"))
            && o.get("payloadType").is_some_and(|t| t.is_str(crypto::PAYLOAD_TYPE))
            && o.str("payload").is_some()
            && o.get("signatures").and_then(Value::as_arr).is_some_and(|s| (1..=MAX_SIGNATURES).contains(&s.len()) && s.iter().all(|e| entry_shaped(ctx.reg, e)))
    });
    let o = ok.ok_or_else(|| refused!("{what} is not a DSSE envelope of an in-toto statement with 1 to 16 signatures"))?;
    let payload = b64::decode(o.str("payload").unwrap()).ok_or_else(|| refused!("{what}'s payload is not base64"))?;
    Ok((payload, o.get("signatures").and_then(Value::as_arr).unwrap().clone()))
}

/// Exactly one subject: META/files.json and its SHA-256 (64 lowercase hex).
fn one_subject(ctx: &Ctx, st: &Obj) -> Option<String> {
    let s = st.get("subject")?.as_arr()?;
    if s.len() != 1 {
        return None;
    }
    let s = s[0].as_obj()?;
    if !s.keys_are(ctx.reg.shape("subject")) || !s.get("name")?.is_str(FILES) {
        return None;
    }
    let d = s.get("digest")?.as_obj()?;
    if d.len() != 1 {
        return None;
    }
    let h = d.str("sha256")?;
    hex_n(h, 64).then(|| h.to_string())
}

fn arm_ok(ctx: &Ctx, arm: Option<&Value>) -> Option<Obj> {
    let arm = arm?.as_obj()?;
    let shape = ctx.reg.shape("certificateArm");
    if !arm.keys_are(shape) {
        return None;
    }
    let int = |k: &str| arm.get(k).and_then(Value::as_int);
    for f in shape.iter().filter(|f| *f != "perTask") {
        int(f)?;
    }
    let (t, h, o, c, p95, cap, spacing) = (int("tasks")?, int("hits")?, int("offered")?, int("correct")?, int("p95Micros")?, int("maximumOffered")?, int("spacing")?);
    if !((0..=MAX_TASKS).contains(&t) && (0..=MAX_OFFERED).contains(&cap) && (1..=MAX_SPACING).contains(&spacing) && (0..=86_400_000_000).contains(&p95)) {
        return None;
    }
    let per = arm.get("perTask")?.as_arr()?;
    if per.len() as i64 != t {
        return None;
    }
    let (mut so, mut sc, mut sh) = (0i64, 0i64, 0i64);
    for e in per {
        let e = e.as_arr()?;
        if !(e.len() == 2 || e.len() == 3) {
            return None;
        }
        let v: Vec<i64> = e.iter().map(Value::as_int).collect::<Option<Vec<_>>>()?;
        if !(0 <= v[1] && v[1] <= v[0] && v[0] <= cap) || (v.len() == 3 && !(0..=MAX_TASK_BYTES).contains(&v[2])) {
            return None;
        }
        so += v[0];
        sc += v[1];
        sh += (v[1] > 0) as i64;
    }
    (so == o && sc == c && sh == h).then(|| arm.clone())
}

fn per_task(arm: &Obj) -> Vec<Vec<i64>> {
    arm.get("perTask")
        .and_then(Value::as_arr)
        .map(|a| a.iter().filter_map(|e| e.as_arr().map(|e| e.iter().filter_map(Value::as_int).collect())).collect())
        .unwrap_or_default()
}

fn int(o: &Obj, k: &str) -> i64 {
    o.get(k).and_then(Value::as_int).unwrap_or(0)
}

/// An evaluation certificate's statement, strictly.
fn read_certificate(ctx: &Ctx, payload: &[u8]) -> R<(String, Obj, Ticks)> {
    let reg = ctx.reg;
    let st = profile(payload)?;
    let st = match st.as_obj() {
        Some(s) if s.keys_are(reg.shape("statement")) && s.get("_type").is_some_and(|t| t.is_str(STATEMENT)) && s.get("predicateType").is_some_and(|t| t.is_str(CERTIFICATE)) => s.clone(),
        _ => refuse!("the certificate is not an evaluation statement"),
    };
    let subject = one_subject(ctx, &st).ok_or_else(|| refused!("the certificate must name exactly one subject: META/files.json and its SHA-256"))?;
    let pred = match st.get("predicate").and_then(Value::as_obj) {
        Some(p) if p.keys_within(reg.shape("certificatePredicate")) => p.clone(),
        _ => refuse!("the certificate's predicate holds other fields than this format defines"),
    };
    if !(pred.str("id").is_some_and(valid_id) && pred.str("version").is_some_and(valid_version)) {
        refuse!("the certificate names no valid pack id and version");
    }
    if !reg.is_line(pred.get("evaluator"), 200) {
        refuse!("the certificate names its evaluator as one line of 1 to 200 characters");
    }
    let tool_ok = pred.get("tool").and_then(Value::as_obj).is_some_and(|t| {
        t.keys_are(reg.shape("certificateTool")) && reg.is_line(t.get("name"), 100) && reg.is_line(t.get("version"), 64)
    });
    if !tool_ok {
        refuse!("the certificate names its tool and version");
    }
    let evaluated_at = pred.str("evaluatedAt").and_then(utc_second).ok_or_else(|| refused!("the certificate has no evaluation time in UTC"))?;
    let one_of = |v: Option<&Value>, names: &[String]| v.and_then(Value::as_str).is_some_and(|s| holds(names, s));
    let trial = match pred.get("trial").and_then(Value::as_obj) {
        Some(t) if t.keys_within(reg.shape("certificateTrial"))
            && one_of(t.get("measure"), &reg.measures)
            && one_of(t.get("baseline"), &reg.baselines)
            && reg.is_line(t.get("taskSource"), 500) =>
        {
            t.clone()
        }
        _ => refuse!("the certificate's trial names its measure, baseline and task source"),
    };
    if !reg.is_line(pred.get("statistic"), 100) {
        refuse!("the certificate names the statistic its checks were computed with");
    }
    if let Some(c) = trial.get("confidencePermille") {
        if !c.as_int().is_some_and(|c| reg.confidences.contains(&c)) {
            refuse!("the certificate's confidencePermille is 900, 950, 975 or 990");
        }
    }
    let checks = match pred.get("checks").and_then(Value::as_arr) {
        Some(c) if (1..=16).contains(&c.len()) => c,
        _ => refuse!("the certificate lists 1 to 16 checks"),
    };
    let mut parsed: Vec<(String, i64, i64, bool)> = Vec::new();
    for c in checks {
        let ok = c.as_obj().filter(|c| {
            c.keys_within(reg.shape("certificateCheck"))
                && one_of(c.get("target"), &reg.certificate_targets)
                && c.get("requiredMicro").and_then(Value::as_int).is_some()
                && c.get("observedMicro").and_then(Value::as_int).is_some()
                && c.get("met").and_then(Value::as_bool).is_some()
                && (!c.has("estimateMicro") || c.get("estimateMicro").and_then(Value::as_int).is_some())
        });
        let c = ok.ok_or_else(|| refused!("a certificate check names a known target, requiredMicro and observedMicro as integers, and met"))?;
        parsed.push((c.str("target").unwrap().to_string(), int(c, "requiredMicro"), int(c, "observedMicro"), c.get("met").and_then(Value::as_bool).unwrap()));
    }
    if parsed.iter().map(|c| c.0.as_str()).collect::<BTreeSet<_>>().len() != parsed.len() {
        refuse!("the certificate checks one target twice");
    }
    let with = arm_ok(ctx, pred.get("with")).ok_or_else(|| refused!("the certificate's 'with' arm is malformed"))?;
    let without = match pred.get("without") {
        None => None,
        Some(w) => Some(arm_ok(ctx, Some(w)).ok_or_else(|| refused!("the certificate's 'without' arm is malformed"))?),
    };
    let passed = pred.get("passed").and_then(Value::as_bool).ok_or_else(|| refused!("the certificate says whether every target was met ('passed')"))?;
    if let Some(aef) = pred.get("aef") {
        let ok = aef.as_obj().is_some_and(|a| {
            a.keys_are(reg.shape("certificateAef"))
                && a.str("runId").is_some_and(|r| (1..=128).contains(&r.len()) && r.bytes().all(|c| c.is_ascii_alphanumeric() || b"._:-".contains(&c)))
                && a.str("runHash").is_some_and(|h| hex_n(h, 64))
        });
        if !ok {
            refuse!("the certificate's 'aef' names an AEF runId and its runHash");
        }
    }
    let mut entries = per_task(&with);
    if let Some(w) = &without {
        entries.extend(per_task(w));
    }
    if entries.iter().any(|e| e.len() == 3) && entries.iter().any(|e| e.len() == 2) {
        refuse!("the certificate records the bytes of some tasks and not others");
    }
    let is_cost = |t: &str| holds(&reg.cost_targets, t);
    if parsed.iter().any(|c| is_cost(&c.0)) && (without.is_none() || entries.iter().any(|e| e.len() == 2)) {
        refuse!("a cost check needs both arms and each task's bytes");
    }
    for arm in [Some(&with), without.as_ref()].into_iter().flatten() {
        if int(arm, "maximumOffered") == 0 && per_task(arm).iter().any(|e| e.len() == 3 && e[2] > 0) {
            refuse!("an arm that offers nothing records no bytes");
        }
    }
    if parsed.iter().any(|c| is_cost(&c.0) && c.3 != (c.2 >= c.1)) {
        refuse!("a cost check's met disagrees with its bound and target");
    }
    if passed != (parsed.iter().all(|c| c.3) && int(&with, "tasks") >= MINIMUM_TASKS) {
        refuse!("the certificate's verdict disagrees with its checks");
    }
    let (wt, ws) = (int(&with, "tasks"), int(&with, "spacing"));
    if without.as_ref().is_some_and(|w| int(w, "spacing") != ws) {
        refuse!("the certificate's arms were drawn at different spacings");
    }
    if wt > 1 && ws * (wt - 1) >= HISTORY_WINDOW {
        refuse!("the certificate's tasks span more than the history a trial draws from");
    }
    let baseline = trial.str("baseline").unwrap_or("");
    if baseline == "none" && without.as_ref().is_some_and(|w| int(w, "maximumOffered") != 0 || int(w, "offered") != 0) {
        refuse!("a certificate measured against none records a baseline arm that offers nothing");
    }
    if baseline == "host" && without.as_ref().is_some_and(|w| int(w, "maximumOffered") == 0) {
        refuse!("a certificate measured against the host's retrieval records that arm with a cap of 1 or more");
    }
    if without.as_ref().is_some_and(|w| int(w, "tasks") != wt) {
        refuse!("the certificate's arms ran different numbers of tasks");
    }
    if baseline == "host" && without.is_none() {
        refuse!("a certificate measured against the host's retrieval records that arm");
    }
    Ok((subject, pred, evaluated_at))
}

/// An AEF run's run hash (AEF 1.0 SEAL-1 to SEAL-4).
fn run_hash(ctx: &Ctx, run: &Files) -> String {
    let unhashed = ctx.reg.shape("aefRunUnhashed");
    let mut lines = String::new();
    for (p, b) in run {
        if holds(unhashed, p) || p.starts_with("overlays/") {
            continue;
        }
        lines.push_str(&alloc::format!("{}  {}  {}\n", crypto::sha256_hex(b), b.len(), p));
    }
    crypto::sha256_hex(lines.as_bytes())
}

fn run_json(run: &Files) -> Option<Obj> {
    let b = run.get("run.json")?;
    if b.len() > 1 << 20 {
        return None;
    }
    match profile(b) {
        Ok(Value::Obj(o)) => Some(o),
        _ => None,
    }
}

/// A certificate's stem: the key's first 16 hex digits and the evaluation time (UTC).
fn stem_for(key_id: &str, t: Ticks) -> String {
    let (y, mo, d, h, mi, s) = civil(t);
    alloc::format!("{}-{y:04}{mo:02}{d:02}t{h:02}{mi:02}{s:02}z", &key_id[..16])
}

/// An evaluation certificate for a verified pack, by a key the host trusts as evaluator for its id. One the pack carries
/// travels under its evaluator's key and evaluation time, with its AEF run beside it when it has one.
pub fn verify_certificate(ctx: &Ctx, bytes: &[u8], pack: &Verified, run: Option<&Files>, stem: Option<&str>) -> R<Obj> {
    let (payload, entries) = envelope(ctx, bytes, "the certificate")?;
    let (subject, pred, evaluated_at) = read_certificate(ctx, &payload)?;
    if alloc::format!("sha256:{subject}") != pack.digest {
        refuse!("the certificate is for another pack");
    }
    if !pred.get("id").is_some_and(|v| v.is_str(&pack.id)) || !pred.get("version").is_some_and(|v| v.is_str(&pack.version)) {
        refuse!("the certificate names another pack id or version");
    }
    if let Some(run) = run {
        let aef = match pred.get("aef").and_then(Value::as_obj) {
            Some(a) => a,
            None => refuse!("an AEF run travels with the certificate, but the certificate names none"),
        };
        if !aef.get("runHash").is_some_and(|h| h.is_str(&run_hash(ctx, run))) {
            refuse!("the AEF run beside the certificate is not the run it names");
        }
        let doc = run_json(run);
        if let Some(named) = doc.as_ref().and_then(|d| d.str("runId")) {
            if !aef.get("runId").is_some_and(|r| r.is_str(named)) {
                refuse!("the AEF run beside the certificate has another run id");
            }
        }
        let of = doc.as_ref().and_then(|d| d.get("subject")).and_then(Value::as_obj).is_some_and(|s| {
            s.get("ref").is_some_and(|r| r.is_str(&alloc::format!("pack:{}@{}", pack.id, pack.digest))) && s.get("version").is_some_and(|v| v.is_str(&pack.version))
        });
        if !of {
            refuse!("the AEF run beside the certificate is the evidence of another decision");
        }
    }
    let digest = crypto::pae_digest(crypto::PAYLOAD_TYPE, &payload);
    let signers: Vec<String> = holding(ctx.policy, &entries, &digest)
        .into_iter()
        .filter(|(k, _)| ctx.policy.key_refusal(k, "evaluator", &pack.id, evaluated_at, ctx.now).is_none())
        .map(|(_, id)| id)
        .collect();
    if signers.is_empty() {
        refuse!("no valid certificate signature by a key this host trusts as evaluator for the pack");
    }
    if let Some(stem) = stem {
        if !signers.iter().any(|k| stem_for(k, evaluated_at) == stem) {
            refuse!("the certificate travels as {stem}, not as its evaluator's key and evaluation time");
        }
    }
    Ok(pred)
}

/// The AEF run folder beside a carried certificate, with paths inside it; None when it has none.
pub fn run_beside(files: &Files, stem: &str) -> Option<Files> {
    let folder = alloc::format!("{ATTESTATIONS}{stem}.aef/");
    let run: Files = files.iter().filter_map(|(p, b)| p.strip_prefix(folder.as_str()).map(|r| (r.to_string(), b.clone()))).collect();
    (!run.is_empty()).then_some(run)
}

/// A publication receipt for a verified federated pack fetched from `location` as `archive`.
pub fn verify_receipt(ctx: &Ctx, bytes: &[u8], pack: &Verified, federation: Option<&Federation>, archive: &[u8], location: &str) -> R<Obj> {
    let reg = ctx.reg;
    let federation = federation.ok_or_else(|| refused!("a publication receipt is for a federated pack"))?;
    let (payload, entries) = envelope(ctx, bytes, "the receipt")?;
    let st = profile(&payload)?;
    let st = match st.as_obj() {
        Some(s) if s.keys_are(reg.shape("statement")) && s.get("_type").is_some_and(|t| t.is_str(STATEMENT)) && s.get("predicateType").is_some_and(|t| t.is_str(PUBLICATION)) => s.clone(),
        _ => refuse!("the receipt is not a publication statement"),
    };
    let subject = one_subject(ctx, &st).ok_or_else(|| refused!("the receipt must name exactly one subject: META/files.json and its SHA-256"))?;
    let pred = match st.get("predicate").and_then(Value::as_obj) {
        Some(p) if p.keys_within(reg.shape("receiptPredicate")) => p.clone(),
        _ => refuse!("the receipt's predicate holds other fields than this format defines"),
    };
    if !(pred.str("id").is_some_and(valid_id) && pred.str("version").is_some_and(valid_version)) {
        refuse!("the receipt names no valid pack id and version");
    }
    let placed_location = pred.str("location").ok_or_else(|| refused!("the receipt names no location"))?;
    if !pred.str("archiveSha256").is_some_and(|h| hex_n(h, 64)) {
        refuse!("the receipt's archiveSha256 is not 64 lowercase hexadecimal characters");
    }
    let placed_for = match pred.get("audience") {
        None => refuse!("the receipt names no audience"),
        Some(a) => read_audience(a, "the receipt's audience")?,
    };
    let access = pred.str("access").ok_or_else(|| refused!("the receipt names no access kind"))?;
    let checked = match pred.get("checked").and_then(Value::as_obj) {
        Some(c) if c.keys_are(reg.shape("receiptCheck")) => c.clone(),
        _ => refuse!("the receipt's 'checked' is an object of 'denied' and 'allowed'"),
    };
    let mut probes: [Vec<String>; 2] = [Vec::new(), Vec::new()];
    for (k, (name, anonymous)) in [("denied", true), ("allowed", false)].iter().enumerate() {
        let ok = checked.get(name).and_then(Value::as_strs).filter(|p| {
            p.len() <= 64 && p.iter().collect::<BTreeSet<_>>().len() == p.len() && p.iter().all(|x| (*anonymous && *x == "anonymous") || valid_webid(x))
        });
        match ok {
            Some(p) => probes[k] = p.iter().map(|s| s.to_string()).collect(),
            None => refuse!("the receipt's checked.{name} is a list of at most 64 WebIDs"),
        }
    }
    let published_at = pred.str("publishedAt").and_then(utc_second).ok_or_else(|| refused!("the receipt has no publishing time in UTC"))?;
    if !valid_webid(placed_location) || placed_location.contains('#') {
        refuse!("the receipt's location is not an https URL in the WebID grammar, without a fragment");
    }
    if !holds(&reg.access_kinds, access) {
        refuse!("the receipt's access is wac, acp or public");
    }
    if (access == "public") != placed_for.is_none() {
        refuse!("a receipt's access is public exactly when its audience is public");
    }
    let [denied, allowed] = probes;
    match &placed_for {
        Some(pf) => {
            if !holds(&denied, "anonymous") {
                refuse!("a receipt for named readers records that anonymous was refused");
            }
            if allowed.iter().any(|a| !holds(pf, a)) {
                refuse!("a receipt records a reader outside its audience as allowed");
            }
            if denied.iter().any(|d| holds(pf, d)) {
                refuse!("a receipt records a reader in its audience as refused");
            }
        }
        None => {
            if !denied.is_empty() {
                refuse!("a public receipt records nobody as refused");
            }
        }
    }
    if alloc::format!("sha256:{subject}") != pack.digest {
        refuse!("the receipt is for another pack");
    }
    if pred.str("id") != Some(pack.id.as_str()) || pred.str("version") != Some(pack.version.as_str()) {
        refuse!("the receipt names another pack id or version");
    }
    if placed_location != location {
        refuse!("the receipt places the pack elsewhere than where it was fetched");
    }
    if pred.str("archiveSha256") != Some(crypto::sha256_hex(archive).as_str()) {
        refuse!("the archive fetched is not the archive the receipt names");
    }
    if let Some(a) = &federation.audience {
        if placed_for.as_ref().map_or(true, |pf| pf.iter().any(|x| !holds(a, x))) {
            refuse!("the receipt grants a wider audience than the pack allows");
        }
    }
    if let Some(pf) = &placed_for {
        if !ctx.policy.host.iter().any(|h| holds(pf, h)) {
            refuse!("the placement is not for this host");
        }
    }
    let digest = crypto::pae_digest(crypto::PAYLOAD_TYPE, &payload);
    for (key, _) in holding(ctx.policy, &entries, &digest) {
        if ctx.policy.key_refusal(key, "publisher", &pack.id, published_at, ctx.now).is_none() && key.web_id.as_deref() == Some(federation.publisher.as_str()) {
            return Ok(pred);
        }
    }
    refuse!("no valid receipt signature by a key bound to the pack's publisher")
}

/// An erasure's statement, strictly.
fn read_erasure(ctx: &Ctx, payload: &[u8]) -> R<(Obj, Ticks)> {
    let reg = ctx.reg;
    let st = profile(payload)?;
    let st = match st.as_obj() {
        Some(s) if s.keys_within(reg.shape("statement")) && s.get("_type").is_some_and(|t| t.is_str(STATEMENT)) && s.get("predicateType").is_some_and(|t| t.is_str(ERASURE)) => s.clone(),
        _ => refuse!("the erasure is not an in-toto statement of {ERASURE}"),
    };
    let pred = match st.get("predicate").and_then(Value::as_obj) {
        Some(p) if p.keys_within(reg.shape("erasurePredicate")) => p.clone(),
        _ => refuse!("the erasure's predicate holds exactly id, issuedAt, reason, salt and records"),
    };
    if !pred.str("id").is_some_and(valid_id) {
        refuse!("the erasure names no valid pack id");
    }
    let issued = pred.str("issuedAt").and_then(utc_second).ok_or_else(|| refused!("the erasure's issuedAt is an instant in UTC to the second"))?;
    if !pred.str("reason").is_some_and(|r| holds(&reg.erasure_reasons, r)) {
        refuse!("the erasure's reason is one the registry names");
    }
    if !pred.str("salt").is_some_and(|s| hex_n(s, 32)) {
        refuse!("the erasure's salt is 32 lowercase hexadecimal characters");
    }
    let records = match pred.get("records").and_then(Value::as_arr) {
        Some(r) if (1..=MAX_ERASED).contains(&r.len()) => r,
        _ => refuse!("the erasure lists 1 to 256 records"),
    };
    let mut seen: BTreeSet<(String, String)> = BTreeSet::new();
    for r in records {
        let r = match r.as_obj() {
            Some(r) if r.keys_within(reg.shape("erasureRecord")) && r.str("type").is_some_and(|t| (1..=256).contains(&t.chars().count())) && (r.has("key") != r.has("keyHash")) => r,
            _ => refuse!("an erased record is its type and exactly one of key and keyHash"),
        };
        if r.has("key") && !r.str("key").is_some_and(|k| (1..=1024).contains(&k.chars().count())) {
            refuse!("an erased record's key is 1 to 1,024 characters");
        }
        if r.has("keyHash") && !r.str("keyHash").is_some_and(|h| hex_n(h, 64)) {
            refuse!("an erased record's keyHash is 64 lowercase hexadecimal characters");
        }
        let named = (r.str("type").unwrap().to_string(), r.str("key").or(r.str("keyHash")).unwrap().to_string());
        if !seen.insert(named) {
            refuse!("the erasure names one record twice");
        }
    }
    Ok((pred, issued))
}

/// An erasure for the pack `pid`: signed by a key the host trusts as publisher or revoker for its id; for a federated pack,
/// by a key bound to its publisher WebID.
pub fn verify_erasure(ctx: &Ctx, bytes: &[u8], pid: &str, publisher_web_id: Option<&str>) -> R<Obj> {
    let (payload, entries) = envelope(ctx, bytes, "the erasure")?;
    let (pred, issued) = read_erasure(ctx, &payload)?;
    if pred.str("id") != Some(pid) {
        refuse!("the erasure is for another pack");
    }
    let digest = crypto::pae_digest(crypto::PAYLOAD_TYPE, &payload);
    for (key, _) in holding(ctx.policy, &entries, &digest) {
        if publisher_web_id.is_some() && key.web_id.as_deref() != publisher_web_id {
            continue;
        }
        if ctx.reg.erasure_roles.iter().any(|role| ctx.policy.key_refusal(key, role, pid, issued, ctx.now).is_none()) {
            return Ok(pred);
        }
    }
    refuse!("the erasure carries no signature by a key this host trusts as publisher or revoker for the pack")
}

/// A host's trial aggregate (feature 13, R-CORE-140), strictly: its predicate and the end of its window.
fn read_aggregate(ctx: &Ctx, payload: &[u8]) -> R<(Obj, Ticks)> {
    let reg = ctx.reg;
    let st = profile_integers(payload)?;
    let st = match st.as_obj() {
        Some(s)
            if s.keys_within(reg.shape("statement"))
                && s.get("_type").is_some_and(|t| t.is_str(STATEMENT))
                && s.get("predicateType").is_some_and(|t| t.is_str(&reg.aggregate_type)) =>
        {
            s.clone()
        }
        _ => refuse!("the aggregate is not an in-toto statement of {}", reg.aggregate_type),
    };
    let p = match st.get("predicate").and_then(Value::as_obj) {
        Some(p) if p.keys_within(reg.shape("aggregatePredicate")) => p.clone(),
        _ => refuse!("the aggregate's predicate holds only the registry's fields: no task, question, record or path has a place in it"),
    };
    if !(p.str("id").is_some_and(valid_id) && p.str("version").is_some_and(|v| (1..=256).contains(&v.chars().count()))) {
        refuse!("the aggregate names a valid pack id and version");
    }
    let digest = p.str("digest").filter(|d| is_digest(d)).ok_or_else(|| refused!("the aggregate's digest is sha256: and 64 lowercase hexadecimal characters"))?;
    let subject_ok = st.get("subject").and_then(Value::as_arr).is_some_and(|s| {
        s.len() == 1
            && s[0].as_obj().is_some_and(|s| {
                s.get("name").is_some_and(|n| n.is_str(FILES))
                    && s.get("digest").and_then(Value::as_obj).is_some_and(|d| d.len() == 1 && d.get("sha256").is_some_and(|h| h.is_str(&digest[7..])))
            })
    });
    if !subject_ok {
        refuse!("the aggregate's subject is the pack's file list, by the digest its predicate names");
    }
    if !p.str("measure").is_some_and(|m| holds(&reg.measures, m)) {
        refuse!("the aggregate names a known measure");
    }
    if !p.get("tasks").and_then(Value::as_int).is_some_and(|t| (1..=1_000_000).contains(&t)) {
        refuse!("the aggregate's tasks are 1 to 1,000,000");
    }
    let window = p.get("window").and_then(Value::as_obj).filter(|w| w.keys_within(reg.shape("aggregateWindow")));
    let (start, end) = match window {
        Some(w) => (w.str("from").and_then(utc_second), w.str("to").and_then(utc_second)),
        None => (None, None),
    };
    let end = match (start, end) {
        (Some(s), Some(e)) if s <= e => e,
        _ => refuse!("the aggregate's window is from and to, instants in UTC to the second, from not after to"),
    };
    let mechanism = match p.str("mechanism") {
        Some(m) if holds(&reg.aggregate_mechanisms, m) => m.to_string(),
        _ => {
            let mut m: Vec<&str> = reg.aggregate_mechanisms.iter().map(|s| s.as_str()).collect();
            m.sort();
            refuse!("the aggregate's mechanism is {}", m.join(" or "))
        }
    };
    let noisy = mechanism != "none";
    match p.get("epsilonMilli") {
        Some(e) => {
            if !noisy || !e.as_int().is_some_and(|e| (1..=100_000).contains(&e)) {
                refuse!("the aggregate's epsilonMilli, 1 to 100,000, goes with noise only");
            }
        }
        None if noisy => refuse!("an aggregate with noise names its epsilonMilli"),
        None => {}
    }
    if !p.str("decision").is_some_and(is_digest) {
        refuse!("the aggregate names its decision by sha256: and 64 lowercase hexadecimal characters");
    }
    let checks = match p.get("checks").and_then(Value::as_arr) {
        Some(c) if (1..=16).contains(&c.len()) => c.clone(),
        _ => refuse!("the aggregate lists 1 to 16 checks"),
    };
    let mut seen = BTreeSet::new();
    let mut all_met = true;
    for c in &checks {
        let c = match c.as_obj() {
            Some(c)
                if c.keys_within(reg.shape("aggregateCheck"))
                    && c.str("target").is_some_and(|t| holds(&reg.trial_targets, t))
                    && ["successes", "trials", "requiredMicro"].iter().all(|f| c.get(f).and_then(Value::as_int).is_some())
                    && c.get("met").and_then(Value::as_bool).is_some() =>
            {
                c
            }
            _ => refuse!("an aggregate check names a known target, its successes, trials and requiredMicro as integers, and met"),
        };
        let (target, successes, trials, required) = (c.str("target").unwrap(), int(c, "successes"), int(c, "trials"), int(c, "requiredMicro"));
        let met = c.get("met").and_then(Value::as_bool).unwrap();
        let bound = match c.get("boundMicro") {
            Some(b) => match b.as_int() {
                Some(b) if !noisy => Some(b),
                _ => refuse!("an aggregate check's boundMicro is an integer, without noise only: noise makes it approximate"),
            },
            None if !noisy => refuse!("an aggregate without noise gives each check its boundMicro"),
            None => None,
        };
        if successes < 0 || trials < 0 {
            refuse!("an aggregate's counts are never negative");
        }
        if !noisy && successes > trials {
            refuse!("an aggregate check's successes are within its trials");
        }
        if let Some(b) = bound {
            let within = if holds(&reg.at_most, target) { b <= required } else { b >= required };
            if within != met {
                refuse!("an aggregate check's met disagrees with its bound and its requiredMicro");
            }
        }
        if !seen.insert(target.to_string()) {
            refuse!("an aggregate checks each target once");
        }
        all_met &= met;
    }
    if p.get("passed").and_then(Value::as_bool) != Some(all_met) {
        refuse!("the aggregate's passed is whether every check was met");
    }
    Ok((p, end))
}

/// A host's trial aggregate of this pack (feature 13): of its id, version and digest, signed by a key trusted as evaluator for its id.
pub fn verify_aggregate(ctx: &Ctx, bytes: &[u8], pid: &str, version: &str, digest: &str) -> R<Obj> {
    let (payload, entries) = envelope(ctx, bytes, "the aggregate")?;
    let (p, end) = read_aggregate(ctx, &payload)?;
    // Of this pack in every name it gives, as .NET and Python (review 11-13, H6).
    if p.str("digest") != Some(digest) || p.str("id") != Some(pid) || p.str("version") != Some(version) {
        refuse!("the aggregate is of another pack");
    }
    let id = p.str("id").unwrap_or("").to_string();
    let pae = crypto::pae_digest(crypto::PAYLOAD_TYPE, &payload);
    for (key, _) in holding(ctx.policy, &entries, &pae) {
        if ctx.reg.aggregate_roles.iter().any(|role| ctx.policy.key_refusal(key, role, &id, end, ctx.now).is_none()) {
            return Ok(p);
        }
    }
    refuse!("the aggregate carries no signature by a key this host trusts as evaluator for the pack")
}
