# AgentMemory native library for Rust

Rust bindings of AgentMemory's native library (`native/include/agentmemory_native.h`, `native/CONTRACT.md`): owner-scoped
recall and journaled writes against Neo4j, with no .NET on the machine. The same library as the Python and TypeScript
packages.

| Crate | What it is |
|---|---|
| `agentmemory-native-sys` | The C ABI 1.1 as raw declarations, loaded at run time with `libloading` (never linked at build time). |
| `agentmemory-native` | The safe API: an owned `Context`, jobs, typed results, one waiter thread per context, an optional async API. |
| `gate` (`agentmemory-native-gate`) | The runner of the shared conformance gate (`native/gate/cases`). Never published. |

Version 0.2.0, as the library. MSRV 1.81 for the two crates (the gate runner needs 1.85). Not published yet:
`publish = false` until the owner decides on crates.io.

```rust
use agentmemory_native::{Context, GetContext};
use serde_json::json;

let memory = Context::open(&json!({
    "schemaVersion": 1,
    "profile": "read_only",
    "owner": {"mode": "fixed", "id": "user-42"},
    "store": {"uri": "neo4j+s://xxxx.databases.neo4j.io", "username": "neo4j", "password": password},
    "embedding": {"dimensions": 1536, "model": "text-embedding-3-small",
                  "endpoint": {"url": "https://api.openai.com/v1/embeddings", "apiKey": key}}
}))?;
let recall = memory.get_context(&GetContext { max_connection_facts: 4, ..GetContext::query("s1", "Where does my sister live?") })?;
if recall.is_ok() {
    for fact in &recall.facts {
        println!("{} {} {}", fact.subject, fact.predicate, fact.object);
    }
}
// Dropping the context closes it.
```

## Where the library comes from

The library is loaded at run time from a known place only, and never unloaded:

1. `Native::load_from(path)` / `Library::load(path)`: an explicit path.
2. The `AGENTMEMORY_NATIVE_LIBRARY` environment variable.
3. The platform's file name next to the running executable: `agentmemory_native.so`, `agentmemory_native.dylib` or
   `agentmemory_native.dll`.

Nothing searches the loader's paths or the working directory. A file that cannot be loaded, a library without the
`am_v1_*` exports, and a library of another ABI major version are refused with a `LoadError` that names the file and the
fix. `am_v1_wait` (ABI 1.1) is looked up as optional: on a 1.0 library the waiter polls instead.

The build script downloads nothing yet. The plan is to fetch the prebuilt library from a GitHub Release, checked
against a pinned SHA-256 per target, honouring `CARGO_NET_OFFLINE` and `DOCS_RS`; no release assets exist until
NATIVE-RELEASE, so its table is empty and the library always comes from one of the places above.

## The API

- **Three status layers stay apart.** A call's return code is an error: `Error::Call` with a `CallError` (`Overloaded`,
  `Closing`, `InvalidHandle`, ...), or `Error::Open` with the library's own code (`authority_held`,
  `unknown_property`, ...). A poll's disposition is `Polled::Pending` or `Polled::Terminal`. The operation's own
  status is the `status` of its result (`OperationStatus::Ok`, `Timeout`, `Unavailable`, `Invalid`, ...): a recall
  that found the store unreachable is a `Recall` with that status, not an error.
- **Operations block.** `get_context`, `remember`, `forget`, `erase_owner`, `operation`, `resolve`, `capabilities`,
  `schema_status`, `schema_bootstrap`, or `run` with any `Operation`. Each submits, waits, and releases its job.
- **One waiter thread per context**, started at the first wait: it waits inside the library (`am_v1_wait`), wakes
  whoever waits for the job that finished, and ends when the context closes. On an ABI 1.0 library it polls instead.
- **The low level** maps one to one onto the ABI: `Context::submit`, `Job::poll`, `Job::cancel`, `Job::release`,
  `Context::state` (`ContextState`), `Context::wait_any`, and `raw::Native` for raw handles and bytes. Do not mix
  `wait_any` with the waiter on one context: each finished job is reported once, to whichever asks first.
- **Handles** are `u64`. Every buffer the library returns is copied and freed exactly once with `am_v1_buffer_free`.
- **Closing**: `Context::close(wait)` retries while the library answers BUSY (work still running), up to `wait`;
  dropping a context does the same for up to 10 s. A context the library cannot close in that time stays closing and
  holds the store until the process ends.
- **Async** (feature `async`): `Context::call_async`, `Context::run_async`, and `job.await`. Standard futures and wakers
  only, so any executor runs them; the waiter thread wakes them. Dropping a future cancels its job in the library and
  releases it. There is no binding-side deadline: the job's own `timeout_ms` ends it, and an executor's timeout can drop
  the future sooner.
- **Unsafe code** is confined to `agentmemory-native-sys` and the `raw` module of `agentmemory-native`; each block says
  why it is sound.

## The gate

The binding passes the shared gate (`native/gate/cases`, `native/gate/cases/FORMAT.md`) through the `gate` binary, and
the tests `native/gate/REQUIRED.md` asks of each binding through `agentmemory-native/tests/required.rs` and
`agentmemory-native-sys/tests/abi.rs`. From `native/rust`:

```bash
export AGENTMEMORY_NATIVE_LIBRARY=/path/to/agentmemory_native.so
cargo test --all-features
cargo run --release --bin gate -- --tier A
AMP_URI=bolt://127.0.0.1:7687 AMP_USER=neo4j AMP_PASSWORD=... cargo run --release --bin gate -- --tier B
GATE_TIER=B AMP_URI=... AMP_USER=... AMP_PASSWORD=... cargo test --all-features
```

The runner starts the gate's stub servers (`native/gate/stubs.py`) with `PYTHON` (else `python3`, or `python` on
Windows) and ends with a summary line: `tier A: 22 passed, 0 failed, 0 skipped (Rust, rustc 1.99.1)`. Tier B also runs
tier A's cases, against the store. `--case <id>` runs one case.

REQUIRED.md items: R-HANDLES, R-LOAD-PATH, R-LOAD-UNLOADABLE, R-LOAD-FOREIGN, R-FREE-NULL, R-WAITER-CLOSE, B8 and B15
need no store. R-ASYNC, B7 and R-MEMORY recall from the seeded store with `GATE_TIER=B`, and use `capabilities_get`
without one. R-NO-DOTNET runs where `AGENTMEMORY_TARGET_IMAGE=1` says the machine is the target image (CI runners have
.NET). R-PACKAGE waits for NATIVE-RELEASE: the crates carry no library yet.

CI (`.github/workflows/native-platforms.yml`, job `build`) runs `cargo test --all-features` and the gate's tier A on
linux-x64, linux-arm64, osx-arm64 and win-x64 against the library each runner builds, with Rust 1.99.1.

## On Windows without a Rust linker: Docker

Build the Linux library and run everything in the official `rust` image (the repository is mounted; cargo's registry,
the toolchain's components and the build output live in named volumes):

```bash
native/gate/publish-linux.sh artifacts/native-linux     # the library, in artifacts/native-linux/bin/
MSYS_NO_PATHCONV=1 docker run --rm \
  -v "$(cygpath -m "$PWD"):/src" \
  -v am-rust-cargo-registry:/usr/local/cargo/registry \
  -v am-rust-rustup-1.99.1:/usr/local/rustup \
  -v am-rust-target:/target -e CARGO_TARGET_DIR=/target \
  -e AGENTMEMORY_NATIVE_LIBRARY=/src/artifacts/native-linux/bin/agentmemory_native.so \
  -e AGENTMEMORY_TARGET_IMAGE=1 \
  -w /src/native/rust rust:1.99.1-bookworm bash -c '
    rustup component add clippy rustfmt &&
    cargo fmt --check &&
    cargo clippy --all-targets --all-features -- -D warnings &&
    cargo test --all-features &&
    cargo run --release --bin gate -- --tier A'
```

Run it from the repository root in Git Bash (`cygpath` and `MSYS_NO_PATHCONV=1` keep the paths Docker's). The image has
Python 3 for the stubs and no .NET.
