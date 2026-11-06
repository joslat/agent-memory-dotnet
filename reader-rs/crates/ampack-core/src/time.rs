// SPDX-License-Identifier: Apache-2.0
//! Date and time (R-CORE-034): RFC 3339 written YYYY-MM-DDTHH:MM:SS, an optional fraction of 1 to 7 digits, then Z or an
//! offset whose minutes are below 60 and whose size is at most 14 hours, in ASCII digits. An instant is 100 ns ticks
//! since 1970 (UTC), so two times compare to 100 ns. The UTC time lies within years 1 to 9999.

/// 100 ns ticks since 1970-01-01T00:00:00Z.
pub type Ticks = i128;

pub const TICKS_PER_SECOND: Ticks = 10_000_000;

fn days_from_civil(y: i64, m: i64, d: i64) -> i64 {
    let y = if m <= 2 { y - 1 } else { y };
    let era = (if y >= 0 { y } else { y - 399 }) / 400;
    let yoe = y - era * 400;
    let mp = (m + 9) % 12;
    let doy = (153 * mp + 2) / 5 + d - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    era * 146_097 + doe - 719_468
}

/// The civil date and time of ticks (UTC): year, month, day, hour, minute, second.
pub fn civil(t: Ticks) -> (i64, u32, u32, u32, u32, u32) {
    let secs = t.div_euclid(TICKS_PER_SECOND) as i64;
    let days = secs.div_euclid(86_400);
    let sod = secs.rem_euclid(86_400);
    let z = days + 719_468;
    let era = (if z >= 0 { z } else { z - 146_096 }) / 146_097;
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = (doy - (153 * mp + 2) / 5 + 1) as u32;
    let m = (if mp < 10 { mp + 3 } else { mp - 9 }) as u32;
    let y = yoe + era * 400 + if m <= 2 { 1 } else { 0 };
    (y, m, d, (sod / 3600) as u32, (sod / 60 % 60) as u32, (sod % 60) as u32)
}

fn leap(y: i64) -> bool {
    (y % 4 == 0 && y % 100 != 0) || y % 400 == 0
}

fn days_in_month(y: i64, m: i64) -> i64 {
    match m {
        1 | 3 | 5 | 7 | 8 | 10 | 12 => 31,
        4 | 6 | 9 | 11 => 30,
        _ => {
            if leap(y) {
                29
            } else {
                28
            }
        }
    }
}

fn digits(b: &[u8]) -> Option<i64> {
    if b.is_empty() || !b.iter().all(|c| c.is_ascii_digit()) {
        return None;
    }
    Some(b.iter().fold(0i64, |a, &c| a * 10 + (c - b'0') as i64))
}

fn ticks_of(y: i64, mo: i64, d: i64, h: i64, mi: i64, s: i64) -> Ticks {
    let days = days_from_civil(y, mo, d) as i128;
    (days * 86_400 + (h * 3600 + mi * 60 + s) as i128) * TICKS_PER_SECOND
}

/// An RFC 3339 date-time as ticks, or None when the text is not one the format reads.
pub fn instant(text: &str) -> Option<Ticks> {
    let b = text.as_bytes();
    if b.len() < 20 || b[4] != b'-' || b[7] != b'-' || b[10] != b'T' || b[13] != b':' || b[16] != b':' {
        return None;
    }
    let (y, mo, d) = (digits(&b[0..4])?, digits(&b[5..7])?, digits(&b[8..10])?);
    let (h, mi, s) = (digits(&b[11..13])?, digits(&b[14..16])?, digits(&b[17..19])?);
    let mut i = 19;
    let mut fraction: Ticks = 0;
    if b[i] == b'.' {
        let n = b[i + 1..].iter().take_while(|c| c.is_ascii_digit()).count();
        if !(1..=7).contains(&n) {
            return None;
        }
        let mut f = digits(&b[i + 1..i + 1 + n])? as Ticks;
        for _ in n..7 {
            f *= 10;
        }
        fraction = f;
        i += 1 + n;
    }
    let rest = &b[i..];
    let offset_seconds: i64 = if rest == b"Z" {
        0
    } else if rest.len() == 6 && (rest[0] == b'+' || rest[0] == b'-') && rest[3] == b':' {
        let (oh, om) = (digits(&rest[1..3])?, digits(&rest[4..6])?);
        if om >= 60 {
            return None;
        }
        let total = oh * 3600 + om * 60;
        if total > 14 * 3600 {
            return None;
        }
        if rest[0] == b'-' {
            -total
        } else {
            total
        }
    } else {
        return None;
    };
    if !(1..=9999).contains(&y) || !(1..=12).contains(&mo) || d < 1 || d > days_in_month(y, mo) || h > 23 || mi > 59 || s > 59 {
        return None;
    }
    let utc = ticks_of(y, mo, d, h, mi, s) - offset_seconds as Ticks * TICKS_PER_SECOND + fraction;
    let (lo, hi) = (ticks_of(1, 1, 1, 0, 0, 0), ticks_of(10000, 1, 1, 0, 0, 0));
    if utc < lo || utc >= hi {
        return None;
    }
    Some(utc)
}

/// An instant in UTC to the second (YYYY-MM-DDTHH:MM:SSZ): the signing, publishing and evaluation times.
pub fn utc_second(text: &str) -> Option<Ticks> {
    if text.len() != 20 || !text.ends_with('Z') {
        return None;
    }
    instant(text)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn instants() {
        assert_eq!(instant("1970-01-01T00:00:00Z"), Some(0));
        assert_eq!(instant("1970-01-01T00:00:00.0000001Z"), Some(1));
        assert_eq!(instant("1970-01-01T01:00:00+01:00"), Some(0));
        assert_eq!(instant("2024-02-29T00:00:00Z").is_some(), true);
        for bad in [
            "2026-02-29T00:00:00Z", "2026-10-31T24:00:00Z", "2026-10-31T23:59:60Z", "0000-01-01T00:00:00Z",
            "0001-01-01T00:00:00+01:00", "9999-12-31T23:59:59-00:01", "2026-10-31T12:00:00+14:01", "2026-10-31T12:00:00-23:59",
            "2026-10-31t12:00:00z", "2026-13-01T00:00:00Z", "2026-10-31T12:00:00.12345678Z", "2026-10-31T12:00:00.Z",
            "2026-10-31T12:00:00+05:60", "2026-10-31T12:00:00", "\u{662}026-10-31T12:00:00Z",
        ] {
            assert_eq!(instant(bad), None, "{bad}");
        }
        assert!(instant("0001-01-01T00:00:00-01:00").is_some());
        assert!(instant("2026-10-31T12:00:00+14:00").is_some());
        assert_eq!(utc_second("2026-10-31T12:00:00+00:00"), None);
        assert_eq!(utc_second("2026-10-31T12:00:00.1Z"), None);
        let t = instant("2026-10-27T12:34:56Z").unwrap();
        assert_eq!(civil(t), (2026, 10, 27, 12, 34, 56));
    }
}
