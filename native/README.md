# AgentMemory native library

AgentMemory's read path as one shared library built with .NET Native AOT. The library carries its own runtime: the
host needs no .NET installation. Any language with a C foreign-function interface can use it; the Python package in
`python/` wraps it for agents, agent frameworks and voice servers.

| Path | What it is |
|---|---|
| [`include/agentmemory_native.h`](include/agentmemory_native.h) | The C ABI, version 1.0: open, submit, poll, cancel, release, free, state, close |
| [`CONTRACT.md`](CONTRACT.md) | The JSON documents that cross it: configuration, requests, results |
| [`python/agentmemory_native`](python/agentmemory_native) | The Python binding: `MemoryClient` (sync) and `AsyncMemoryClient` (asyncio) |
| [`gate/`](gate) | Build and proof on Linux x64: publish, seed a throw-away Neo4j, run the gate tests on the target image |
| [`../tools/AgentMemory.Native`](../tools/AgentMemory.Native) | The .NET project the library is published from |

## What version 1.0 does

The `read_only` profile: owner-scoped recall of facts and preferences by a query vector the caller supplies
(`memory_get_context`), and `capabilities_get`. It writes nothing to the store, not even access stamps. The owner is
fixed by configuration (one trusted owner) or named by the host's own code on every request (a multi-user application);
recall without an owner is refused either way.

The `explicit_write` profile adds remembering the owner's own words (`memory_remember`) through a durable journal in a
state directory, with receipts that say truthfully whether a write was applied and verified, rejected, or is uncertain;
recall pauses while a write is pending or uncertain. It also forgets (`memory_forget`: what one remember produced, or
memories by id, closed and kept as history) and erases everything of the owner (`memory_erase_owner`: verified against
the store, and the remembered words purged from the journal); an uncertain write is kept or forgotten by the operator
(`memory_operation_resolve`).

The `learning` profile is `explicit_write` with every remember applied once by its operation id: the memories and a
receipt are written in one store transaction, so a write interrupted by a crash or a store failure is simply retried
and the receipt decides whether anything is left to do. Recall is not paused by pending remembers, and a remember may be
marked `"origin": "observed"` for words a host learns without being asked. See [CONTRACT.md](CONTRACT.md) for every
operation and state.

| Profile | Recall | Remember | Forget, erase | A write interrupted by a crash |
|---|---|---|---|---|
| `read_only` | yes | no | no | (none) |
| `explicit_write` | yes, paused while a write is pending or uncertain | asked for only | yes | ends `unknown`; the operator decides |
| `learning` | yes, paused only by an uncertain write | asked for, or observed | yes | retried; applied once by its receipt |

```python
from agentmemory_native import MemoryClient

config = {
    "schemaVersion": 1,
    "profile": "read_only",
    "owner": {"mode": "fixed", "id": "owner-1"},
    "store": {"uri": "bolt://localhost:7687", "username": "neo4j", "password": "..."},
    "embedding": {"dimensions": 768, "model": "nomic-embed-text-v1.5"},
}
with MemoryClient(config) as memory:
    result = memory.get_context(session_id="session-1", query_vector=vector)
    for fact in result.facts:
        print(fact.subject, fact.predicate, fact.object, dict(fact.metadata))
```

The store must already hold AgentMemory's schema (the managed `AgentMemory.Cli bootstrap` creates it); the library
never creates or migrates schema.

## Build and prove (Linux x64)

From the repository root, with Docker:

```bash
native/gate/publish-linux.sh artifacts/native/current                # publish in the Ubuntu 24.04 SDK image
native/gate/neo4j-up.sh artifacts/native/neo4j.env                   # throw-away Neo4j 5.26, random password
(set -a; . artifacts/native/neo4j.env; set +a; AM_GATE_PW="$PASSWORD" \
  dotnet run native/gate/seed/seed.cs -- "bolt://127.0.0.1:$HOST_PORT" neo4j AM_GATE_PW)
docker build -t am-speech-gate-runtime:24.04 -f native/gate/Dockerfile.runtime native/gate
native/gate/run-gate.sh artifacts/native/neo4j.env artifacts/native/current/bin artifacts/native/gate
```

`run-gate.sh` runs the gate tests (`gate/tests`, standard library only) inside plain Ubuntu 24.04 with the
distribution's Python and no .NET, and fingerprints the whole graph before and after: a read-only run must leave it
unchanged.

The explicit-write tests (`gate/writetests`) run on their own throw-away store, because they write:

```bash
native/gate/run-write-gate.sh artifacts/native/current/bin artifacts/native/write-gate
```

The write path itself is also proven on its own, outside the ABI:

```bash
native/gate/run-write-probe.sh artifacts/native/write-probe   # publish, own throw-away Neo4j, run on plain Ubuntu
```

The probe (`gate/writeprobe`) compiles the library's own explicit-write composition and its OpenAI-compatible chat and
embedding clients, serves a stub model on 127.0.0.1 inside the process, and remembers two turns: the first adds a dated
fact and a preference (after the model's first answer, a 503, is retried), the second corrects the fact, which the
store-aware writer must see and the update judge must confirm. It reads every written property back and prints one
JSON report; exit code 0 when every check passes.

The wheel carries the library inside the package, so users only run `pip install`:

```bash
python native/python/build_wheel.py artifacts/native/current/bin/agentmemory_native.so artifacts/native/wheel \
  --provenance artifacts/native/current/provenance --commit "$(git rev-parse HEAD)"
GATE_MODE=wheel native/gate/run-gate.sh artifacts/native/neo4j.env artifacts/native/wheel artifacts/native/gate-wheel
```

The builder uses the standard library only and is reproducible: the same library gives the same wheel bytes. The tag
is `manylinux_2_34_x86_64` because the library's newest glibc symbol is `GLIBC_2.34` and it links only `libc` and
`libm`. Wheel mode installs it with `pip` into a fresh virtual environment on the target image and runs the same tests
against the installed package.

With `--provenance` (written by `publish-linux.sh`), the wheel carries `agentmemory_native/_native/MANIFEST.json`
(package and ABI versions, platform tag, the library's size and SHA-256, commit, SDK and native runtime versions, every
compiled-in package with its licence; `agentmemory_native.manifest()` returns it) and, in its `dist-info/licenses`, the
AgentMemory licence and the third-party notices: each package's own licence and notice files, or the standard text of
the licence it declares where it ships none, and the .NET runtime's licence and notices. The builder stops on a licence
it has no text for.

Loading refuses, with `NativeLibraryError` naming the file, the platform and what to install instead: a file that
cannot be loaded here (another architecture or platform, damaged), a library without the AgentMemory exports, and a
packaged library whose bytes differ from the manifest. Nothing else is tried in its place.

## Agent frameworks

The binding stays framework-neutral; two thin adapters sit beside it, each importing its framework only when used:

- `agentmemory_native.langchain.AgentMemoryRetriever` (needs `langchain-core`): owner-scoped recall as a LangChain
  retriever, one `Document` per fact or preference. A failed recall raises (or returns nothing, if asked), never reads
  as "nothing stored".
- `agentmemory_native.langgraph.AgentMemoryStore` (needs `langgraph`): a LangGraph `BaseStore`. AgentMemory is not a
  document store, and the adapter says so: `put` remembers the words in `value["text"]` through the journal (the
  writer turns them into facts and preferences of the context's one owner), `get` returns that remember's receipt,
  `search` returns the facts and preferences recall finds for the query, and a delete forgets (closes, not deletes)
  what the key produced. Other words under the same key forget the earlier ones and remember the new. Puts and deletes
  need a writing profile (`explicit_write` or `learning`).

## Platform

Built and proven for linux-x64 on Ubuntu 24.04 (glibc 2.39). A binary built against a newer glibc does not load on an
older one, so build on the oldest distribution you support. Windows and macOS builds are possible from the same project
on those systems; they are not qualified yet. Plain `bolt://` is qualified; TLS (`bolt+s://`) is not yet.

The workflow `.github/workflows/native-platforms.yml` builds the library for linux-x64, linux-arm64, osx-arm64 and win-x64,
each on its own runner, as pull requests that touch the native library run it. It checks the compiler against the
security floor (`packaging/check_toolchain.py`), writes `MANIFEST.json` (`packaging/native_manifest.py`: size, SHA-256,
compiler, and on Linux the newest glibc symbol, at most 2.34), checks the macOS signature, and runs the gate's tier A from
Python on each. Tier A is what the gate tests that needs no store: loading, the ABI, handles, configuration and contract
refusals, closed contexts, the embedding checks that end before the store, and an unreachable store. Run it anywhere with
`GATE_TIER=A` and `AGENTMEMORY_NATIVE_LIBRARY` set; the store tiers still run in `run-gate.sh`. This builds and loads the
library on those platforms; it does not yet qualify them against a store, and nothing is published from it.
