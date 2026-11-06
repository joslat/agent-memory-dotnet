// SPDX-License-Identifier: Apache-2.0
//! A third, independent verifier of memory packs (format v1.2, "ampack"): Core verify, read and inspect, and the
//! profiles the conformance kit exercises (compose, federation, certify, erasures, embeddings, permissions' read door).
//!
//! It shares no code with the .NET and Python verifiers. Every closed shape, list, limit and profile comes from
//! `spec/registry.json`, embedded at build time from its one real path. The archive and JSON readers are its own and
//! strict; the general zip and JSON crates are lenient by design.
#![no_std]
#![forbid(unsafe_code)]

extern crate alloc;

pub mod b64;
pub mod certify;
pub mod crypto;
pub mod json;
pub mod pack;
pub mod registry;
pub mod text;
pub mod time;
pub mod trust;
pub mod zip;

use alloc::string::String;

/// A refusal: the pack (or a document beside it) is not verified, and why.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Refused(pub String);

pub type R<T> = core::result::Result<T, Refused>;

/// A refusal with a formatted reason.
#[macro_export]
macro_rules! refuse {
    ($($arg:tt)*) => { return Err($crate::Refused(alloc::format!($($arg)*))) };
}

/// A refusal value with a formatted reason (for map_err and ok_or).
#[macro_export]
macro_rules! refused {
    ($($arg:tt)*) => { $crate::Refused(alloc::format!($($arg)*)) };
}

pub use pack::{inspect, verify_archive, Inspection, Verified};
