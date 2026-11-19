# ampack-core

Verify and read **ampack** memory packs: knowledge, ontologies and minds for AI agents, in one signed file that is
checked where it lands.

This crate is an independent, strict verifier of the format. It is `no_std` with `alloc`, forbids unsafe code, and brings
its own archive and JSON readers, because the general zip and JSON crates are lenient by design. It verifies and reads;
it does not build, sign or certify a pack. This is a preview (0.1.0).

```toml
[dependencies]
ampack-core = "0.1"
```

```rust
use ampack_core::{time::instant, verify_archive};

fn check(archive: &[u8], trust_policy: &[u8]) -> Result<(), String> {
    let now = instant("2026-11-19T12:00:00Z").ok_or("not a date-time")?;
    // No revocation documents here; pass the host's own as the third argument.
    let outcome = verify_archive(archive, trust_policy, &[], now).map_err(|refused| refused.0)?;
    println!("{} {} {}", outcome.verified.id, outcome.verified.version, outcome.verified.digest);
    for (path, bytes) in &outcome.files {
        println!("{path}: {} bytes", bytes.len());
    }
    Ok(())
}
```

A pack is accepted only when a key in **your** trust policy signed it as publisher for its id. `inspect` describes an
archive without judging any signature: never treat it as a verdict. The crate has no clock: you pass the time.

It shares no code with the .NET and Python verifiers of the format, and must reach the same verdict as they do on every
conformance vector. The command line is the crate `ampack` (`cargo install ampack`, command `ampack-rs`).

- The format, the hub and certified packs: https://memcert.dev
- Specification and test vectors: https://github.com/joslat/agent-memory-dotnet/tree/main/spec
- Paper: https://doi.org/10.5281/zenodo.24012989

Apache-2.0. Patent pending (Swiss application CH001531/2026); see `PATENTS.md` in the repository for the proposed
pledge.
