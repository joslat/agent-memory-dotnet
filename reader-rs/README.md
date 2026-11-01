# reader-rs: a third, independent memory-pack verifier (Rust, WebAssembly)

Feature 18. A Rust implementation of ampack Core verify, read and inspect, sharing no code with the .NET verifier
(`src/AgentMemory.Packs.Format`) or the Python one (`conformance/pack-v1.1/python/ampack_verify.py`). It reads the same
`spec/registry.json`, embedded at build time with `include_str!` from its one real path, so a shape added on the main line
reaches it on the next build with no copy to keep in step.

Local only, like the rest of the pack work: nothing here is published.

## Crates

| Crate | What it is |
|---|---|
| `crates/ampack-core` | The verifier, `no_std` with `alloc`. Its own strict archive reader (`zip.rs`) and strict JSON reader (`json.rs`); the files list and digest, DSSE with ECDSA P-256, the trust policy, revocation documents, pack.json, kinds, composites and their locks, pack sets, the card, the derivation, the usage policy's read door, records, embeddings (`pack.rs`, `trust.rs`); certificates, publication receipts and erasures (`certify.rs`). |
| `crates/ampack-cli` | `ampack-rs`, the command line, run as the Python verifier is run. |
| `crates/ampack-wasm` | The WebAssembly component: `verify(archive, policy, now) -> verdict`, `inspect(archive) -> summary` (`wit/reader.wit`). It imports nothing: no file system, no network, no clock. |

Dependencies, pinned exactly: `p256 =0.13.2` (ECDSA P-256), `sha2 =0.10.9`, `unicode-normalization =0.1.22` (Unicode
15.0.0, the format's tables; a unit test checks the version), `miniz_oxide =0.8.9` (the raw inflater only: every rule of
the archive profile around it is this crate's own), and `wit-bindgen` for the component. No zip or serde_json crate: they
are lenient by design.

## Command line

```
ampack-rs verify <pack.ampack> <trust.json> [<revocations.json>...]
          [--receipt <file> --location <url>] [--certificate <META/attestations/...|file>] [--erasure <META/attestations/...|file>]
ampack-rs inspect <pack.ampack>
```

Exit 0 verified, 1 refused, 2 usage or a defect. A verified pack prints `verified <id> <version> <digest>` and
`profiles <the profiles it uses>`, as the Python verifier does; a refusal prints `refused: <why>`, worded as the Python
verifier words it.

## Build, test, run the kit

One heavy task at a time on this machine: wait while `dotnet.exe` or `testhost.exe` runs, then build with two jobs.

```
cd reader-rs
set CARGO_TARGET_DIR=%CD%\target
cargo test -j 2
cargo build --release -j 2
cd ..
python conformance/pack-v1.1/run_third.py            # every vector in results.json, per family and in total
python conformance/pack-v1.1/run_third.py --show-all  # one line a vector
```

`run_third.py` reads `results.json` (each vector's expected verdict, kind, trust file, revocations, receipt location or
document) and runs `ampack-rs` on each; it never regenerates a vector and never calls `run_vectors.py`.

## The component and its JavaScript binding

```
rustup target add wasm32-unknown-unknown
cargo build -p ampack-wasm --target wasm32-unknown-unknown --release -j 2
cargo install wasm-tools --version =1.263.0 --locked --root .tools --no-default-features --features component -j 2
.tools\bin\wasm-tools component new target\wasm32-unknown-unknown\release\ampack_wasm.wasm -o dist\ampack-reader.wasm
.tools\bin\wasm-tools component wit dist\ampack-reader.wasm      # world root { export ampack:reader/verifier@0.1.0; } - no import
cd js && npm install && npm run transpile && npm run kit           # jco 1.11.1 -> dist/js; the kit's pack vectors through JavaScript
```

The component's `verify` takes no revocation documents (R4's signature): `js/kit.mjs` runs it on every pack vector
that needs none.

## Results (1 Nov 2026)

- `run_third.py`: 496 / 496 vectors agree with the expected verdict, every family; 281 of the 282 refusals that
  `reasons.json` pins name the same rule in the Python verifier's words (the one left: `policy-with-a-private-key`, whose
  trust file `.work/trust-private-key.json` is not in the kit, so every verifier refuses it for a missing file).
- Sabotage checks (one rule broken at a time, rebuilt, kit rerun): 8 / 8 caught (NFC, repeated keys, signatures,
  the read door, revocation circles, bytes after a deflate stream, the combining-mark run, the key window).
- The component through jco: 338 / 338 pack vectors agree; about 1.2 ms a verify of a small pack in Node.
- Sizes: `ampack-rs.exe` 638 KB (release, LTO, size-optimised); the component 499 KB, 205 KB gzipped.
