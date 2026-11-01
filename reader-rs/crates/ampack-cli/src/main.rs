//! ampack-rs: the third verifier's command line, run as the Python verifier is run.
//!
//! usage: ampack-rs <pack.ampack> <trust.json> [<revocations.json>...] [--receipt <file> --location <url>]
//!        [--certificate <META/attestations/...|file>] [--erasure <META/attestations/...|file>]
//!        ampack-rs verify <pack.ampack> <trust.json> [<revocations.json>...] [the same options]
//!        ampack-rs inspect <pack.ampack>
//! exit 0 verified, 1 refused, 2 usage or a defect

use std::fs::File;
use std::io::Read;
use std::process::ExitCode;

use ampack_core::certify::{run_beside, verify_certificate, verify_erasure, verify_receipt};
use ampack_core::pack::{cert_stem, erasure_stem, read_federation, verify_files, Ctx, ATTESTATIONS};
use ampack_core::registry::Registry;
use ampack_core::text::shown;
use ampack_core::time::Ticks;
use ampack_core::trust::{apply_all, load_policy, MAX_ENVELOPE, MAX_POLICY, MAX_REVOCATIONS};
use ampack_core::zip::MAX_ARCHIVE;
use ampack_core::{Refused, R};

const USAGE: &str = "usage: ampack-rs [verify] <pack.ampack> <trust.json> [<revocations.json>...] [--receipt <file> --location <url>] \
[--certificate <path>] [--erasure <path>]\n       ampack-rs inspect <pack.ampack>";

fn cap_text(cap: usize) -> String {
    if cap >= 1 << 20 {
        format!("{} MiB", cap >> 20)
    } else {
        format!("{} KiB", cap >> 10)
    }
}

/// A file's bytes, at most `cap` of them: never through a link and never a folder, checked on the handle that is read; a
/// pipe or a device read with the same bound. A file that cannot be read is refused.
fn read_regular(path: &str, cap: usize, what: &str) -> R<Vec<u8>> {
    let before = std::fs::symlink_metadata(path).map_err(|e| Refused(format!("{what} cannot be read: {e}")))?;
    if before.file_type().is_symlink() || reparse_point(&before) {
        return Err(Refused(format!("{what} is a link")));
    }
    let file = File::open(path).map_err(|e| Refused(format!("{what} cannot be read: {e}")))?;
    let status = file.metadata().map_err(|e| Refused(format!("{what} cannot be read: {e}")))?;
    if status.is_dir() {
        return Err(Refused(format!("{what} is a folder")));
    }
    if status.len() > cap as u64 {
        return Err(Refused(format!("{what} is larger than {}", cap_text(cap))));
    }
    let mut data = Vec::new();
    file.take(cap as u64 + 1).read_to_end(&mut data).map_err(|e| Refused(format!("{what} cannot be read: {e}")))?;
    if data.len() > cap {
        return Err(Refused(format!("{what} is larger than {}", cap_text(cap))));
    }
    Ok(data)
}

#[cfg(windows)]
fn reparse_point(m: &std::fs::Metadata) -> bool {
    use std::os::windows::fs::MetadataExt;
    m.file_attributes() & 0x400 != 0
}

#[cfg(not(windows))]
fn reparse_point(_: &std::fs::Metadata) -> bool {
    false
}

fn now() -> Ticks {
    let d = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap_or_default();
    (d.as_micros() as Ticks) * 10
}

fn take_option(args: &mut Vec<String>, name: &str) -> Result<Option<String>, String> {
    match args.iter().position(|a| a == name) {
        None => Ok(None),
        Some(i) if i + 1 >= args.len() => Err(format!("error: {name} needs a value\n{USAGE}")),
        Some(i) => {
            let v = args[i + 1].clone();
            args.drain(i..i + 2);
            Ok(Some(v))
        }
    }
}

fn inspect(path: &str) -> ExitCode {
    let archive = match read_regular(path, MAX_ARCHIVE, "the archive") {
        Ok(a) => a,
        Err(e) => {
            println!("refused: {}", shown(&e.0));
            return ExitCode::from(1);
        }
    };
    match ampack_core::inspect(&archive) {
        Ok(i) => {
            let s = |v: &Option<String>| v.as_deref().map(shown).unwrap_or_else(|| "-".into());
            println!("id {}\nversion {}\nkind {}\ndigest {}", s(&i.id), s(&i.version), s(&i.kind), s(&i.digest));
            println!("files {}\nbytes {}\nenvelopes {}\ntitle {}", i.files, i.bytes, i.envelopes, s(&i.title));
            ExitCode::SUCCESS
        }
        Err(e) => {
            println!("refused: {}", shown(&e.0));
            ExitCode::from(1)
        }
    }
}

fn run(mut args: Vec<String>) -> ExitCode {
    if args.first().map(String::as_str) == Some("inspect") {
        return if args.len() == 2 { inspect(&args[1]) } else { println!("{USAGE}"); ExitCode::from(2) };
    }
    if args.first().map(String::as_str) == Some("verify") {
        args.remove(0);
    }
    let options = (|| -> Result<_, String> {
        Ok((take_option(&mut args, "--receipt")?, take_option(&mut args, "--location")?, take_option(&mut args, "--certificate")?, take_option(&mut args, "--erasure")?))
    })();
    let (receipt_path, location, certificate_path, erasure_path) = match options {
        Ok(o) => o,
        Err(e) => {
            println!("{e}");
            return ExitCode::from(2);
        }
    };
    if receipt_path.is_some() != location.is_some() {
        println!("error: --receipt and --location go together");
        return ExitCode::from(2);
    }
    if args.len() < 2 || args.iter().any(|a| a.starts_with("--")) {
        println!("{USAGE}");
        return ExitCode::from(2);
    }
    let reg = match Registry::load() {
        Ok(r) => r,
        Err(e) => {
            println!("error: {}", e.0);
            return ExitCode::from(2);
        }
    };
    let now = now();
    let result = (|| -> R<Vec<String>> {
        let mut policy = load_policy(&reg, &read_regular(&args[1], MAX_POLICY, "the trust policy")?)?;
        let documents = args[2..].iter().map(|p| read_regular(p, MAX_REVOCATIONS, "the revocation document")).collect::<R<Vec<_>>>()?;
        if !documents.is_empty() {
            policy = apply_all(&reg, &policy, &documents, now)?;
        }
        let archive = read_regular(&args[0], MAX_ARCHIVE, "the archive")?;
        let ctx = Ctx { reg: &reg, policy: &policy, now };
        let out = verify_files(&ctx, &archive)?;
        let pack = &out.verified;
        let empty = ampack_core::json::Obj::default();
        let extensions = pack.manifest.get("extensions").and_then(|e| e.as_obj()).unwrap_or(&empty);
        let federation = read_federation(&reg, extensions)?;
        let mut lines = Vec::new();
        if let (Some(rp), Some(loc)) = (&receipt_path, &location) {
            let receipt = read_regular(rp, MAX_ENVELOPE, "the receipt")?;
            verify_receipt(&ctx, &receipt, pack, federation.as_ref(), &archive, loc)?;
            lines.push(format!("verified the placement of {} {} at {}", shown(&pack.id), shown(&pack.version), shown(loc)));
        }
        if let Some(cp) = &certificate_path {
            let pred = if cp.starts_with(ATTESTATIONS) {
                let stem = cert_stem(cp).filter(|_| out.files.contains_key(cp)).ok_or_else(|| Refused(format!("the pack carries no {}", shown(cp))))?;
                let run = run_beside(&out.files, stem);
                verify_certificate(&ctx, &out.files[cp], pack, run.as_ref(), Some(stem))?
            } else {
                verify_certificate(&ctx, &read_regular(cp, MAX_ENVELOPE, "the certificate")?, pack, None, None)?
            };
            lines.push(format!("verified the certificate of {} {} by {}", shown(&pack.id), shown(&pack.version), shown(pred.str("evaluator").unwrap_or(""))));
        }
        if let Some(ep) = &erasure_path {
            let envelope = if ep.starts_with(ATTESTATIONS) {
                if erasure_stem(ep).is_none() || !out.files.contains_key(ep) {
                    return Err(Refused(format!("the pack carries no {}", shown(ep))));
                }
                out.files[ep].clone()
            } else {
                read_regular(ep, MAX_ENVELOPE, "the erasure")?
            };
            let pred = verify_erasure(&ctx, &envelope, &pack.id, federation.as_ref().map(|f| f.publisher.as_str()))?;
            let n = pred.get("records").and_then(|r| r.as_arr()).map_or(0, |r| r.len());
            lines.push(format!("verified the erasure of {n} record(s) of {} ({})", shown(&pack.id), shown(pred.str("reason").unwrap_or(""))));
        }
        lines.push(format!("verified {} {} {}", shown(&pack.id), shown(&pack.version), pack.digest));
        let used: Vec<&str> = out.profiles.iter().map(|(n, _)| n.as_str()).collect();
        let missing: Vec<&str> = out.profiles.iter().filter(|(_, i)| !i).map(|(n, _)| n.as_str()).collect();
        let mut profiles = format!("profiles {}", if used.is_empty() { "core".to_string() } else { used.join(", ") });
        if !missing.is_empty() {
            profiles.push_str(&format!(" (not implemented by this reader: {})", missing.join(", ")));
        }
        lines.push(profiles);
        Ok(lines)
    })();
    match result {
        Ok(lines) => {
            for l in lines {
                println!("{l}");
            }
            ExitCode::SUCCESS
        }
        Err(e) => {
            println!("refused: {}", shown(&e.0));
            ExitCode::from(1)
        }
    }
}

fn main() -> ExitCode {
    run(std::env::args().skip(1).collect())
}
