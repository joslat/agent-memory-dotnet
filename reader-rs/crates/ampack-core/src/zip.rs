//! The archive profile (Core 4): a strict ZIP reader of its own. Contiguous entries from offset 0, sorted, each local
//! header equal to its central entry, stored or deflate, no data descriptor, extra field, comment, folder entry or ZIP64,
//! the fixed time, the end record last with no comment; every deflate stream valid, final, and ending where its bytes end;
//! inflation counted against the declared size; the ratio, count and size limits; every CRC.

use alloc::collections::{BTreeMap, BTreeSet};
use alloc::string::String;
use alloc::vec;
use alloc::vec::Vec;
use miniz_oxide::inflate::core::{decompress, inflate_flags, DecompressorOxide};
use miniz_oxide::inflate::TINFLStatus;

use crate::text::safe_path;
use crate::{refuse, R};

pub const MAX_ARCHIVE: usize = 512 << 20;
pub const MAX_FILES: usize = 4096;
pub const MAX_FILE: u64 = 64 << 20;
pub const MAX_TOTAL: u64 = 256 << 20;
pub const MAX_RATIO: u64 = 250;

/// The files of an archive by path, in path order.
pub type Files = BTreeMap<String, Vec<u8>>;

fn u16_at(d: &[u8], i: usize) -> u16 {
    u16::from_le_bytes([d[i], d[i + 1]])
}
fn u32_at(d: &[u8], i: usize) -> u32 {
    u32::from_le_bytes([d[i], d[i + 1], d[i + 2], d[i + 3]])
}

pub fn crc32(data: &[u8]) -> u32 {
    static TABLE: [u32; 256] = {
        let mut t = [0u32; 256];
        let mut i = 0;
        while i < 256 {
            let mut c = i as u32;
            let mut k = 0;
            while k < 8 {
                c = if c & 1 != 0 { 0xEDB8_8320 ^ (c >> 1) } else { c >> 1 };
                k += 1;
            }
            t[i] = c;
            i += 1;
        }
        t
    };
    let mut c = 0xFFFF_FFFFu32;
    for &b in data {
        c = TABLE[((c ^ b as u32) & 0xFF) as usize] ^ (c >> 8);
    }
    c ^ 0xFFFF_FFFF
}

/// A raw deflate stream inflated to exactly `size` bytes, ending exactly at the end of `raw`.
fn inflate(raw: &[u8], size: usize, name: &str) -> R<Vec<u8>> {
    let mut out = vec![0u8; size + 1];
    let mut state = DecompressorOxide::new();
    let flags = inflate_flags::TINFL_FLAG_USING_NON_WRAPPING_OUTPUT_BUF;
    let (status, consumed, written) = decompress(&mut state, raw, &mut out, 0, flags);
    match status {
        TINFLStatus::Done => {}
        TINFLStatus::HasMoreOutput => refuse!("entry '{name}' does not inflate to its declared size"),
        TINFLStatus::NeedsMoreInput | TINFLStatus::FailedCannotMakeProgress => {
            refuse!("entry '{name}' does not inflate to its declared size")
        }
        _ => refuse!("entry '{name}' is not a valid deflate stream"),
    }
    if written != size {
        refuse!("entry '{name}' does not inflate to its declared size");
    }
    if consumed != raw.len() {
        refuse!("entry '{name}' has bytes after the end of its deflate stream");
    }
    out.truncate(size);
    Ok(out)
}

/// The files of an archive in the strict profile, or why it is refused.
pub fn read_zip(devices: &[String], data: &[u8]) -> R<Files> {
    if data.len() > MAX_ARCHIVE {
        refuse!("the archive is larger than {} MiB", MAX_ARCHIVE >> 20);
    }
    if data.len() < 22 || &data[data.len() - 22..data.len() - 18] != b"PK\x05\x06" {
        refuse!("the archive does not end with the end record");
    }
    let e = data.len() - 22;
    let (disk, cd_disk, n, total) = (u16_at(data, e + 4), u16_at(data, e + 6), u16_at(data, e + 8), u16_at(data, e + 10));
    let (cd_size, cd_start, comment) = (u32_at(data, e + 12) as usize, u32_at(data, e + 16) as usize, u16_at(data, e + 20));
    if disk != 0 || cd_disk != 0 || comment != 0 || n != total || n == 0xFFFF {
        refuse!("the end record is not in the profile");
    }
    let n = n as usize;
    if n > MAX_FILES {
        refuse!("the archive holds more than {MAX_FILES} files");
    }
    if cd_start.checked_add(cd_size) != Some(e) {
        refuse!("the central directory is not where the end record says");
    }
    let mut files = Files::new();
    let (mut cursor, mut pos) = (0usize, cd_start);
    let mut previous: Option<String> = None;
    let mut folded: BTreeSet<String> = BTreeSet::new();
    let mut unpacked: u64 = 0;
    for _ in 0..n {
        if pos + 46 > data.len() {
            refuse!("a central entry is truncated");
        }
        let h = &data[pos..pos + 46];
        let sig = u32_at(h, 0);
        let (made, need, flags, method, time, date) = (u16_at(h, 4), u16_at(h, 6), u16_at(h, 8), u16_at(h, 10), u16_at(h, 12), u16_at(h, 14));
        let (crc, csize, size) = (u32_at(h, 16), u32_at(h, 20) as usize, u32_at(h, 24) as usize);
        let (nlen, xlen, clen, dsk, iattr) = (u16_at(h, 28) as usize, u16_at(h, 30), u16_at(h, 32), u16_at(h, 34), u16_at(h, 36));
        let (eattr, off) = (u32_at(h, 38), u32_at(h, 42) as usize);
        let name_end = (pos + 46 + nlen).min(data.len());
        let name_bytes = &data[pos + 46..name_end];
        if sig != 0x0201_4B50 || name_bytes.iter().any(|&b| !(0x20..=0x7E).contains(&b)) {
            refuse!("a central entry is malformed");
        }
        let name = String::from_utf8_lossy(name_bytes).into_owned();
        if (made >> 8) != 0 || (made & 0xFF) > 63 || !(need == 10 || need == 20) || flags != 0 || !(method == 0 || method == 8) || time != 0 || date != 0x21 {
            refuse!("entry '{name}' is not in the profile");
        }
        if xlen != 0 || clen != 0 || dsk != 0 || iattr != 0 || eattr != 0 || name.ends_with('/') || !safe_path(devices, &name) {
            refuse!("entry '{name}' is not in the profile");
        }
        if previous.as_ref().is_some_and(|p| *p >= name) || off != cursor {
            refuse!("entry '{name}' is out of order, repeated or not contiguous");
        }
        if folded.contains(&name.to_ascii_lowercase()) {
            refuse!("entry '{name}' differs from another only by case");
        }
        // The local header: the same need, flags, method, time, date, CRC, sizes and name length, no extra field.
        let local_ok = cursor + 30 <= data.len() && {
            let l = &data[cursor..cursor + 30];
            &l[..4] == b"PK\x03\x04"
                && u16_at(l, 4) == need
                && u16_at(l, 6) == flags
                && u16_at(l, 8) == method
                && u16_at(l, 10) == time
                && u16_at(l, 12) == date
                && u32_at(l, 14) == crc
                && u32_at(l, 18) as usize == csize
                && u32_at(l, 22) as usize == size
                && u16_at(l, 26) as usize == nlen
                && u16_at(l, 28) == 0
        } && data.get(cursor + 30..cursor + 30 + nlen) == Some(name_bytes);
        if !local_ok {
            refuse!("entry '{name}' has a local header that differs from the central directory");
        }
        let start = cursor + 30 + nlen;
        let ratio_bad = method == 8 && (if csize == 0 { size > 0 } else { (size / csize) as u64 > MAX_RATIO });
        if start + csize > cd_start || size as u64 > MAX_FILE || ratio_bad || (method == 0 && csize != size) {
            refuse!("entry '{name}' breaks a size rule");
        }
        unpacked += size as u64;
        if unpacked > MAX_TOTAL {
            refuse!("the archive unpacks to too much");
        }
        let raw = &data[start..start + csize];
        let content = if method == 8 { inflate(raw, size, &name)? } else { raw.to_vec() };
        if crc32(&content) != crc {
            refuse!("entry '{name}' fails its CRC");
        }
        folded.insert(name.to_ascii_lowercase());
        files.insert(name.clone(), content);
        previous = Some(name);
        cursor = start + csize;
        pos += 46 + nlen;
    }
    if cursor != cd_start || pos != cd_start + cd_size {
        refuse!("there are bytes outside the entries");
    }
    Ok(files)
}

#[cfg(test)]
pub mod tests {
    use super::*;

    fn read_zip(data: &[u8]) -> R<Files> {
        super::read_zip(&crate::registry::Registry::load().unwrap().devices, data)
    }

    /// A profile archive of stored entries, as a writer makes it (for tests).
    pub fn write(entries: &[(&str, &[u8])]) -> Vec<u8> {
        let mut out = Vec::new();
        let mut central = Vec::new();
        for (name, body) in entries {
            let off = out.len() as u32;
            let crc = crc32(body);
            let mut fixed = Vec::new();
            fixed.extend_from_slice(&20u16.to_le_bytes());
            fixed.extend_from_slice(&0u16.to_le_bytes());
            fixed.extend_from_slice(&0u16.to_le_bytes());
            fixed.extend_from_slice(&0u16.to_le_bytes());
            fixed.extend_from_slice(&0x21u16.to_le_bytes());
            fixed.extend_from_slice(&crc.to_le_bytes());
            fixed.extend_from_slice(&(body.len() as u32).to_le_bytes());
            fixed.extend_from_slice(&(body.len() as u32).to_le_bytes());
            fixed.extend_from_slice(&(name.len() as u16).to_le_bytes());
            out.extend_from_slice(b"PK\x03\x04");
            out.extend_from_slice(&fixed);
            out.extend_from_slice(&0u16.to_le_bytes());
            out.extend_from_slice(name.as_bytes());
            out.extend_from_slice(body);
            central.extend_from_slice(b"PK\x01\x02");
            central.extend_from_slice(&20u16.to_le_bytes());
            central.extend_from_slice(&fixed);
            central.extend_from_slice(&[0u8; 12]);
            central.extend_from_slice(&off.to_le_bytes());
            central.extend_from_slice(name.as_bytes());
        }
        let cd_start = out.len() as u32;
        out.extend_from_slice(&central);
        out.extend_from_slice(b"PK\x05\x06\0\0\0\0");
        out.extend_from_slice(&(entries.len() as u16).to_le_bytes());
        out.extend_from_slice(&(entries.len() as u16).to_le_bytes());
        out.extend_from_slice(&(central.len() as u32).to_le_bytes());
        out.extend_from_slice(&cd_start.to_le_bytes());
        out.extend_from_slice(&0u16.to_le_bytes());
        out
    }

    #[test]
    fn crc_is_crc32() {
        assert_eq!(crc32(b"123456789"), 0xCBF4_3926);
    }

    #[test]
    fn a_profile_archive_reads() {
        let z = write(&[("a.json", b"{}"), ("b/c.txt", b"hi")]);
        let f = read_zip(&z).unwrap();
        assert_eq!(f.len(), 2);
        assert_eq!(f["b/c.txt"], b"hi");
    }

    #[test]
    fn unsorted_repeated_case_and_trailing_bytes_refuse() {
        assert!(read_zip(&write(&[("b", b"1"), ("a", b"2")])).is_err());
        assert!(read_zip(&write(&[("a", b"1"), ("a", b"2")])).is_err());
        assert!(read_zip(&write(&[("A", b"1"), ("a", b"2")])).is_err());
        let mut z = write(&[("a", b"1")]);
        z.push(0);
        assert!(read_zip(&z).is_err());
        let mut z = write(&[("a", b"1")]);
        z.insert(0, 0);
        assert!(read_zip(&z).is_err());
    }

    #[test]
    fn a_bad_crc_or_unsafe_name_refuses() {
        let mut z = write(&[("a", b"1")]);
        z[30 + 1] ^= 1; // the body byte
        assert!(read_zip(&z).is_err());
        assert!(read_zip(&write(&[("../a", b"1")])).is_err());
        assert!(read_zip(&write(&[("con.txt", b"1")])).is_err());
    }

    #[test]
    fn deflate_must_end_where_its_bytes_end() {
        // A final stored deflate block holding "hi", then a junk byte.
        let block = [0x01u8, 0x02, 0x00, 0xFD, 0xFF, b'h', b'i'];
        assert_eq!(inflate(&block, 2, "x").unwrap(), b"hi");
        let mut junk = block.to_vec();
        junk.push(0);
        assert!(inflate(&junk, 2, "x").is_err());
        assert!(inflate(&block, 1, "x").is_err());
        assert!(inflate(&block, 3, "x").is_err());
        assert!(inflate(&block[..6], 2, "x").is_err());
        // Not final: block type bit 0 clear.
        let open = [0x00u8, 0x02, 0x00, 0xFD, 0xFF, b'h', b'i'];
        assert!(inflate(&open, 2, "x").is_err());
    }
}
