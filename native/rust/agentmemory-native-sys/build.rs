//! Build script of agentmemory-native-sys.
//!
//! The crate never links the native library at build time: `src/lib.rs` loads it at run time. The owner's plan adds a
//! download of the prebuilt library from a GitHub Release here, checked against a pinned SHA-256 for each target, once
//! that release exists (NATIVE-RELEASE). No release assets exist yet, so the table below is empty and this script
//! fetches nothing. It already honours the variables the download will need to honour.

use std::env;

/// One prebuilt library attached to a GitHub Release.
#[expect(dead_code, reason = "constructed once NATIVE-RELEASE fills PREBUILT")]
struct Prebuilt {
    /// The Rust target triple it serves, for example `x86_64-unknown-linux-gnu`.
    target: &'static str,
    /// The release asset's URL.
    url: &'static str,
    /// The asset's SHA-256, lower-case hexadecimal. A download whose digest differs is refused.
    sha256: &'static str,
}

/// Empty on purpose: the libraries are not published as release assets yet (NATIVE-RELEASE). Each row will name a
/// target, its asset under the release of this crate's version, and the asset's SHA-256.
const PREBUILT: &[Prebuilt] = &[];

/// What this build does about the library.
enum Plan {
    /// Nothing: the library is found at run time (`AGENTMEMORY_NATIVE_LIBRARY`, or next to the executable).
    Nothing,
    /// Download this prebuilt library (NATIVE-RELEASE).
    Download(&'static Prebuilt),
}

fn main() {
    println!("cargo:rerun-if-changed=build.rs");
    for name in ["AGENTMEMORY_NATIVE_LIBRARY", "CARGO_NET_OFFLINE", "DOCS_RS"] {
        println!("cargo:rerun-if-env-changed={name}");
    }
    match plan() {
        Plan::Nothing => {}
        Plan::Download(prebuilt) => download(prebuilt),
    }
}

fn plan() -> Plan {
    // docs.rs builds have no network, and documentation needs no library.
    if env::var_os("DOCS_RS").is_some() {
        return Plan::Nothing;
    }
    // The user brings a library of their own; it is loaded from that path at run time.
    if env::var_os("AGENTMEMORY_NATIVE_LIBRARY").is_some_and(|value| !value.is_empty()) {
        return Plan::Nothing;
    }
    // cargo --offline, or net.offline in the configuration: never touch the network.
    if matches!(env::var("CARGO_NET_OFFLINE").as_deref(), Ok("true") | Ok("1")) {
        return Plan::Nothing;
    }
    let target = env::var("TARGET").unwrap_or_default();
    match PREBUILT.iter().find(|prebuilt| prebuilt.target == target) {
        Some(prebuilt) => Plan::Download(prebuilt),
        None => Plan::Nothing,
    }
}

/// NATIVE-RELEASE: fetch the asset, refuse it unless its SHA-256 is the pinned one, and put it where the loader looks
/// (next to the executables, or a third place the loader is taught). Unreachable while `PREBUILT` is empty.
fn download(_prebuilt: &Prebuilt) {
    panic!(
        "agentmemory-native-sys: downloading the prebuilt library is not implemented yet (NATIVE-RELEASE); \
         set AGENTMEMORY_NATIVE_LIBRARY to a library built for this platform"
    );
}
