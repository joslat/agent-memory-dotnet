//! ampack-core as a WebAssembly component (feature 18, R4): `verify(archive, policy, now) -> verdict` and
//! `inspect(archive) -> summary`. Built for wasm32-unknown-unknown and wrapped as a component, it imports nothing: no
//! file system, no network, no clock.

wit_bindgen::generate!({ world: "reader", path: "wit" });

use exports::ampack::reader::verifier::{Guest, PackInfo, Summary, Verdict};

struct Reader;

impl Guest for Reader {
    fn verify(archive: Vec<u8>, policy: Vec<u8>, now: String) -> Verdict {
        let now = match ampack_core::time::instant(&now) {
            Some(t) => t,
            None => return Verdict::Refused("now is not an RFC 3339 date-time".into()),
        };
        match ampack_core::verify_archive(&archive, &policy, &[], now) {
            Ok(out) => {
                // A profile this reader does not implement is named as such, as the command line names it (review 18, L2).
                let mut profiles: Vec<String> = out.profiles.iter().map(|(n, i)| if *i { n.clone() } else { alloc_name(n) }).collect();
                if profiles.is_empty() {
                    profiles.push("core".into());
                }
                let v = out.verified;
                Verdict::Verified(PackInfo {
                    kind: v.manifest.str("kind").unwrap_or("").into(),
                    id: v.id,
                    version: v.version,
                    digest: v.digest,
                    profiles,
                })
            }
            // Shown as the command line shows it: a line break or control in pack text cannot fake a line (review 18, L1).
            Err(e) => Verdict::Refused(ampack_core::text::shown(&e.0)),
        }
    }

    fn inspect(archive: Vec<u8>) -> Result<Summary, String> {
        let i = ampack_core::inspect(&archive).map_err(|e| ampack_core::text::shown(&e.0))?;
        Ok(Summary {
            files: i.files as u32,
            bytes: i.bytes,
            id: i.id,
            version: i.version,
            kind: i.kind,
            digest: i.digest,
            envelopes: i.envelopes as u32,
            title: i.title,
        })
    }
}

export!(Reader);

fn alloc_name(name: &str) -> String {
    let mut s = String::from(name);
    s.push_str(" (not implemented by this reader)");
    s
}
