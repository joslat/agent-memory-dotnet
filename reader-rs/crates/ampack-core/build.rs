// SPDX-License-Identifier: Apache-2.0
//! Puts the format registry where the crate embeds it from (OUT_DIR/registry.json).
//!
//! In the repository the registry is spec/registry.json, its one real place, and it is read from there, so a shape added
//! on the main line reaches this reader on the next build. A crate published to crates.io cannot reach outside itself: it
//! carries a copy, registry.json beside Cargo.toml, made by ../../sync_sources.py when the crate is packaged. The
//! repository's file wins whenever it is there, so a stale copy is never used in a checkout.
use std::{env, fs, path::Path};

fn main() {
    let manifest = env::var("CARGO_MANIFEST_DIR").expect("CARGO_MANIFEST_DIR");
    let manifest = Path::new(&manifest);
    let repository = manifest.join("..").join("..").join("..").join("spec").join("registry.json");
    let packaged = manifest.join("registry.json");
    println!("cargo:rerun-if-changed={}", repository.display());
    println!("cargo:rerun-if-changed={}", packaged.display());
    let source = if repository.is_file() { repository } else { packaged };
    let text = fs::read_to_string(&source)
        .unwrap_or_else(|e| panic!("the format registry is missing ({}): {e}", source.display()));
    let out = env::var("OUT_DIR").expect("OUT_DIR");
    fs::write(Path::new(&out).join("registry.json"), text).expect("write the registry into OUT_DIR");
}
