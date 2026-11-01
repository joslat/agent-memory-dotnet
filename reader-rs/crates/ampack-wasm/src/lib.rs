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
                let mut profiles: Vec<String> = out.profiles.iter().map(|(n, _)| n.clone()).collect();
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
            Err(e) => Verdict::Refused(e.0),
        }
    }

    fn inspect(archive: Vec<u8>) -> Result<Summary, String> {
        let i = ampack_core::inspect(&archive).map_err(|e| e.0)?;
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
