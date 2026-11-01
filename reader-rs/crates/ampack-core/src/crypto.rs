//! SHA-256, DSSE pre-authentication encoding, and ECDSA P-256 over SHA-256 (DER signatures), on audited crates.

use alloc::string::String;
use alloc::vec::Vec;
use p256::ecdsa::signature::hazmat::PrehashVerifier;
use p256::ecdsa::{Signature, VerifyingKey};
use sha2::{Digest, Sha256};

use crate::b64;

pub const PAYLOAD_TYPE: &str = "application/vnd.in-toto+json";

pub fn sha256(data: &[u8]) -> [u8; 32] {
    Sha256::digest(data).into()
}

pub fn hex(bytes: &[u8]) -> String {
    const H: &[u8; 16] = b"0123456789abcdef";
    let mut s = String::with_capacity(bytes.len() * 2);
    for b in bytes {
        s.push(H[(b >> 4) as usize] as char);
        s.push(H[(b & 15) as usize] as char);
    }
    s
}

pub fn sha256_hex(data: &[u8]) -> String {
    hex(&sha256(data))
}

/// DSSE's pre-authentication encoding of a payload of a type, hashed once.
pub fn pae_digest(payload_type: &str, payload: &[u8]) -> [u8; 32] {
    let mut h = Sha256::new();
    h.update(alloc::format!("DSSEv1 {} {} {} ", payload_type.len(), payload_type, payload.len()).as_bytes());
    h.update(payload);
    h.finalize().into()
}

/// The DER SubjectPublicKeyInfo prefix of an uncompressed P-256 point.
pub const P256_SPKI_PREFIX: [u8; 26] = [
    0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x02, 0x01, 0x06, 0x08, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x03, 0x01,
    0x07, 0x03, 0x42, 0x00,
];

/// One PEM 'PUBLIC KEY' block and nothing else: BEGIN line, 1 to 16 lines of 1 to 76 base64 characters, END line, an
/// optional final line break; each break LF or CRLF.
pub fn public_pem_shaped(pem: &str) -> bool {
    let rest = match pem.strip_prefix("-----BEGIN PUBLIC KEY-----") {
        Some(r) => r,
        None => return false,
    };
    let eol = |s: &str| -> Option<usize> {
        if s.starts_with("\r\n") {
            Some(2)
        } else if s.starts_with('\n') {
            Some(1)
        } else {
            None
        }
    };
    let mut s = match eol(rest) {
        Some(n) => &rest[n..],
        None => return false,
    };
    let mut lines = 0;
    loop {
        if let Some(after) = s.strip_prefix("-----END PUBLIC KEY-----") {
            if lines == 0 {
                return false;
            }
            return after.is_empty() || after == "\n" || after == "\r\n";
        }
        let n = s.bytes().take_while(|c| c.is_ascii_alphanumeric() || *c == b'+' || *c == b'/' || *c == b'=').count();
        if !(1..=76).contains(&n) {
            return false;
        }
        s = &s[n..];
        match eol(s) {
            Some(k) => s = &s[k..],
            None => return false,
        }
        lines += 1;
        if lines > 16 {
            return false;
        }
    }
}

/// The 91-byte DER of a P-256 key in its one form (an uncompressed point, canonical base64), or None.
pub fn canonical_p256_der(pem: &str) -> Option<Vec<u8>> {
    let body: String = pem.split('\n').filter(|l| !l.starts_with("-----")).map(|l| l.trim_end_matches('\r')).collect();
    let der = b64::decode(&body)?;
    if der.len() != 91 || der[..26] != P256_SPKI_PREFIX || der[26] != 4 || b64::encode(&der) != body {
        return None;
    }
    Some(der)
}

/// A verifying key from its SPKI DER: the point must lie on the curve.
pub fn key_from_der(der: &[u8]) -> Option<VerifyingKey> {
    VerifyingKey::from_sec1_bytes(&der[26..]).ok()
}

/// Whether a DER signature holds over a SHA-256 digest.
pub fn verify_prehash(key: &VerifyingKey, digest: &[u8; 32], sig_der: &[u8]) -> bool {
    match Signature::from_der(sig_der) {
        Ok(sig) => key.verify_prehash(digest, &sig).is_ok(),
        Err(_) => false,
    }
}
