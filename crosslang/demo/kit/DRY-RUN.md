# D4 — dry run

**2026-08-16.** Full rehearsal against a Neo4j container **destroyed and recreated from scratch**
(`docker rm -f` then `docker run`), so nothing carried over from any earlier run — including the
schema, which the host bootstraps at startup.

Reproduce with `python crosslang/demo/kit/dry_run.py`.

## Timings

| Step | Run 1 | Run 2 | Budget | |
|---|---:|---:|---:|:-:|
| store contract tests | 3.5s | 1.5s | 30s | ✅ |
| preflight | 3.1s | 1.8s | 30s | ✅ |
| `demo_langgraph.py` — beats 1–6 | 2.3s | 1.5s | 120s | ✅ |
| notebook, executed end to end | 5.5s | 4.8s | 180s | ✅ |
| screencast replay (`--fast`) | 0.1s | 0.1s | 15s | ✅ |
| **machine time** | **14.4s** | **9.6s** | | |

**Twice in a row, clean** — the design's definition of done. The second run is the one that matters:
it starts with Acme already superseded, re-asserts it, and re-supersedes. Nothing accumulates and
nothing has to be reset by hand between takes, which is what makes a second take possible if the first
one goes badly.

Machine time is ~13 seconds against a 10-minute budget. **The demo is entirely speaking time**; the
terminal is never what the room is waiting for. Run 1 is slower than run 2 by the cold JIT and the
first vector-index touch — expect the *first* thing you run in the room to be the slow one, which is
another reason preflight runs at T-15 and not at T-0.

## What the closing review caught

Three defects in the adapter, found by reviewing the D2 build rather than by anything failing. All
three are now covered by `test_store_contract.py`, which runs first in this rehearsal, and **each was
red-probed**: reverting one fix fails exactly its own test and nothing else.

| Defect | Pre-fix behaviour | Why it mattered |
|---|---|---|
| `get()` ignored the namespace | `get(("memories","alice"), key)` returned **Bob's fact** | The engine's by-id read is unscoped by design; the *store contract* is not, and isolation is this adapter's headline claim |
| `search()` offset ate the limit | asked the host for `limit` rows, then dropped the first `offset` — **page 2 came back empty** | An empty page reads as "no more results" |
| Ownerless namespace guessed | `("memories",)` scoped to an owner literally named `memories` | Failed closed, but silently: an empty store with no reason given |

## What the clean database caught

Nothing, this time — and that is worth stating rather than skipping, because the two failures that
*did* come out of a cold start earlier in this track were both invisible against a warm one:

- the spike's first recall failed with "no such vector schema index" against an unbootstrapped
  database, which read as a wire problem and was not one;
- the first parity run passed five fixtures **while comparing nothing**, because every result was
  empty and two empty results are byte-identical.

Both are now permanent guards (startup bootstrap; void witnesses), and this run exercised both paths
on a database that was minutes old.

## Claim cross-check — shipped vs in-build

Every claim in `DEMO-SCRIPT.md`, checked against what actually exists. The dotted-muscle rule holds in
the room: in-build features are shown as designs, never demoed as products.

| Claim | Marked | Verified against |
|---|:-:|---|
| Bitemporal recall, two clocks, all kinds | `[S]` | run live this session — `as_of` March → Acme, September → Initech |
| Non-destructive supersession | `[S]` | provenance walk shows the closed fact, its window, and its replacement |
| Owner isolation enforced on reads | `[S]` | demo beat 6: `owner_id` on every row, Bob's fact absent from Alice's search |
| Working-memory block, point-read | `[S]` | Wave C 30.4; compiled and printed in beat 4 |
| Delta recall, half-open window, checkpoint returned | `[S]` | Wave C; `taken_at` handed back and shown |
| Read-audit trail, unmoved by historical reads | `[S]` | measured this session: live search → count 1, two `as_of` searches → unchanged |
| Schema extension system, four extensions | `[S]` | 30.14; ledger row 20 — TCK **178/178 with all four ON**, same build |
| TCK 178/178 | `[S]` | ledger row 20, reviewer-run from a scratch environment |
| Decay as re-ranking | `[S]` | shipped (recency + structural re-rankers) |
| Published accuracy benchmarks | `[S]` | structured 76–90% @ 403 tok/q |
| LangGraph adapter with `as_of` filter | `[P]` | **prototype** — built this session over a prototype host and a draft wire |
| Python/TS SDKs, embedded NativeAOT, cross-language TCK arms | `[D]` | designs only; no code exists. Shown as a diagram. |
| Ontology tooling, GDS in adapters, Python/TS framework adapters | **absent** | conceded in print on the one-pager |

No claim in the script is unsupported. The one line most likely to be over-said in the room is the
adapter — it is a prototype over a prototype, and the run sheet requires saying the word out loud.

## Fallback rehearsal — against induced failure, not on paper

The two most likely fallbacks were rehearsed by actually breaking things, not by reasoning about them.

**Fallback A — host and database both killed** (`Stop-Process` on the listener, `docker stop`):

- `preflight.py` failed on the first check, named the fallback by name, and exited 1. It did not hang
  waiting for a connection, which is the failure mode that would eat the thirty seconds you have.
- `screencast.py` replayed the full run with **no host, no database, no network**. Exit 0.

**Fallback D — the two `as_of` recalls by hand**, run against the live host. Both returned in under a
second, and the difference is legible in raw JSON: `works_at Acme Corp` with a closed `validUntil`
versus `works_at Initech` with `validUntil: null`. Command text is in `preflight.py --curl`, verified
as printed.

Not rehearsed: **B** and **C** (client-level failures), because inducing them faithfully means
breaking a client rather than an environment, and each target is independently verified working
anyway. **E** is A plus a printed handout.

## A cosmetic to know about (repeat runs only)

Running the demo twice against the **same** database leaves the closed fact's `valid_until` at the
*first* supersession instant while `invalidated_at` carries the *second* — the re-assert clears
`invalidated_at` and the supersede coalesces `valid_until`, so the two stamps drift apart by the gap
between runs. Visible only in the provenance walk, only on a repeat run, and harmless — but a sharp
observer would ask, and the answer is a two-minute detour.

The committed screencast is recorded on a freshly created database, where the two agree. If you rehearse
repeatedly, recreate the container before the real thing.

Chasing that drift turned up something worth recording: the hypothesis was that re-asserting a
superseded fact leaves it live on one clock and expired on the other, so no read could return it.
**A probe against the running system disproved it** — the re-asserted fact came back in live recall.
Written down because reading the Cypher made the wrong conclusion look obvious.

## Gaps, stated

- **The video screencast does not exist.** The transcript and its replay do, and the replay needs no
  host, database, or network — so Fallback A is functional and rehearsed. A video file needs a human
  to press record; steps and a review checklist are in `RECORDING.md`.
