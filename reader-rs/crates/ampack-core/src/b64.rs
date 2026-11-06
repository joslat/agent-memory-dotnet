// SPDX-License-Identifier: Apache-2.0
//! Strict base64 (R-CORE-035): the standard alphabet, a length that is a multiple of four, padding only at the end (at
//! most two), no white space. Unused trailing bits are not judged, as the two other verifiers read them.

use alloc::string::String;
use alloc::vec::Vec;

const ALPHABET: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

fn sextet(c: u8) -> Option<u32> {
    match c {
        b'A'..=b'Z' => Some((c - b'A') as u32),
        b'a'..=b'z' => Some((c - b'a' + 26) as u32),
        b'0'..=b'9' => Some((c - b'0' + 52) as u32),
        b'+' => Some(62),
        b'/' => Some(63),
        _ => None,
    }
}

/// The bytes of strict base64, or None.
pub fn decode(text: &str) -> Option<Vec<u8>> {
    let b = text.as_bytes();
    if b.len() % 4 != 0 {
        return None;
    }
    let pad = b.iter().rev().take_while(|&&c| c == b'=').count();
    if pad > 2 || (pad > 0 && b.len() == pad) {
        return None;
    }
    let body = &b[..b.len() - pad];
    let mut out = Vec::with_capacity(b.len() / 4 * 3);
    let mut acc = 0u32;
    let mut bits = 0u32;
    for &c in body {
        acc = (acc << 6) | sextet(c)?;
        bits += 6;
        if bits >= 8 {
            bits -= 8;
            out.push((acc >> bits) as u8);
            acc &= (1 << bits) - 1;
        }
    }
    // One data character past a whole quad cannot be (6 bits make no byte).
    if body.len() % 4 == 1 {
        return None;
    }
    Some(out)
}

/// Canonical base64 of bytes.
pub fn encode(data: &[u8]) -> String {
    let mut out = String::with_capacity(data.len().div_ceil(3) * 4);
    for chunk in data.chunks(3) {
        let n = chunk.len();
        let v = (chunk[0] as u32) << 16 | (*chunk.get(1).unwrap_or(&0) as u32) << 8 | *chunk.get(2).unwrap_or(&0) as u32;
        out.push(ALPHABET[(v >> 18) as usize & 63] as char);
        out.push(ALPHABET[(v >> 12) as usize & 63] as char);
        out.push(if n > 1 { ALPHABET[(v >> 6) as usize & 63] as char } else { '=' });
        out.push(if n > 2 { ALPHABET[v as usize & 63] as char } else { '=' });
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn strict_as_the_others_read_it() {
        assert_eq!(decode("QQ==").unwrap(), b"A");
        assert_eq!(decode("QR==").unwrap(), b"A");
        assert_eq!(decode("QUJD").unwrap(), b"ABC");
        assert_eq!(decode("QUI=").unwrap(), b"AB");
        assert_eq!(decode("").unwrap(), b"");
        for bad in ["QQ", "QQ=", "QQ==QQ==", "=QQ=", "QQ===", "Q===", "====", "QQ==\n", "QUJDQ=Q=", "QQ=A", "Q", "QU J", "QQ-_"] {
            assert!(decode(bad).is_none(), "{bad}");
        }
        assert_eq!(encode(b"AB"), "QUI=");
        assert_eq!(encode(b"ABC"), "QUJD");
        assert_eq!(encode(b"A"), "QQ==");
    }
}
