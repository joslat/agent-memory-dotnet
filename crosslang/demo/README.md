# A LangGraph `BaseStore` over AgentMemory

> **PROTOTYPE. Throwaway by design.** Not published to PyPI, not packaged, not a preview of the SDK.
> The productized cross-language SDK follows the published designs. `meeting-demo-track.md` D2, built
> over the Spike-0 prototype host and its draft wire.

## The claim, and how it is kept honest

> Any existing LangGraph agent gets **point-in-time recall** by adding one key to a `filter` dict it
> already passes.

`BaseStore` leaves only `batch`/`abatch` abstract; `get`/`put`/`search`/`delete` are concrete and
dispatch through them. Implementing at the batch layer means the public methods stay **LangGraph's
own**, byte for byte — so `store.search(ns, query=…, filter={"as_of": …})` is not a signature we
invented.

That claim is guarded, not asserted. `demo_langgraph.py` fails if any of those four methods is ever
overridden — the same reachability-guard idiom the repository uses for pluggable surfaces, because an
override would make every line of demo output read identically while the claim became false.

## What the demo shows, in order

| Beat | Call | What it proves |
|---|---|---|
| 1 | `store.put` | writes are **triples**, not opaque documents |
| 2 | `store.get` | subject/predicate/object survive the round trip |
| 3 | `put(..., supersedes=…)` | an update **closes** the old fact; it does not overwrite it |
| 4 | `working_memory()` + `delta()` | resume, not cold start: the compiled block, then "what changed" |
| 5 | `store.search(filter={"as_of": …})` | **the beat** — same query, two instants, two answers |
| 6 | `store.search` | Bob's fact is absent from Alice's, and `owner_id` on the wire lets you *check* |

Beats 3 and 5 are the pair that matters. A key-value store can do 1, 2, 4 and 6 in some form. It
cannot do 5, and the reason is 3: it overwrote the only copy of the March answer, so the question is
not slow to answer, it is **unanswerable**.

The two `search` calls in beat 5 differ by one dict key.

## Running it

```bash
docker run -d --name spike0-neo4j -p 7688:7687 -e NEO4J_AUTH=neo4j/spikepassword neo4j:5.26

NEO4J_URI=bolt://localhost:7688 NEO4J_USERNAME=neo4j NEO4J_PASSWORD=spikepassword \
ASPNETCORE_URLS=http://localhost:5173 \
dotnet run --project crosslang/spike0/Spike0.Host -c Release

pip install langgraph          # the only dependency; the adapter itself is stdlib
python crosslang/demo/demo_langgraph.py
```

## The run self-voids rather than reassuring you

Carried over from Spike 0, where the first run passed all five fixtures **while comparing nothing**.
Any beat that produced no evidence marks the run `VOID` and exits non-zero:

- the working-memory block came back empty,
- the delta reported nothing,
- an `as_of` arm returned no employer, or
- **both instants gave the same answer** — a perfect match that demonstrates the clock did nothing.

That last one is the important witness. Two identical answers are exactly what a broken `as_of` looks
like, and it is indistinguishable from success unless something checks.

Both voids below were caught this way, not by reading the code.

## Two findings from building it

### 1. `mention_count` was never incremented by the single-add API — ✅ **FIXED in `0f6ddea`**

*Kept as the record of what building the demo found. The state below is historical; the correction
follows.*

**What was found, by measurement on both arms.** `Fact` upserts MERGE on the triple and
`ON MATCH SET f.mention_count = coalesce(f.mention_count,1)+1`. But `AddFactCoreAsync` only reached
that MERGE when dedup-on-create was off. With `LongTerm.DeduplicateOnCreate = true` — the default —
a re-asserted fact went to `FindDuplicateAsync` → `MarkDeduplicatedAsync`, whose Cypher set
confidence and nothing else. Since the tier admits on `mention_count >= 2`, a fact ingested through
`AddFactAsync` could never become stable however many times the world re-asserted it:

| `DeduplicateOnCreate` | `mention_count` (then) | block (then) | now |
|---|---|---|---|
| `true` (default) | 1 | empty | **2 — compiled** |
| `false` | 2 | compiled | 2 — compiled |

**One thing this section got wrong, and it matters more than the finding.** It said "the shipped
conversational pipeline is unaffected." That was false when written — not because extraction's
counter was broken, but because `PersistenceStage` had **no working-memory rebuild hook at all**
(`working-memory-tier.md` §5.2 specified one; it was never built). So the conversational path was
*also* producing nothing, for a different reason, and this document asserted its safety from a
counter that was working. Both halves were fixed in `0f6ddea` and `2a44537`; a later independent
review found the trigger set is still **incomplete** (entity merge, invalidation and delete paths
remain unhooked) — see the PR body for the current, scoped statement.

**Also corrected:** the guessed fix said "plus the preference twin". There is no preference twin to
fix — `Preference` has no `mention_count`; its block section admits on confidence, which the
preference dedup path already bumps. `PreferenceQueries.MarkDeduplicated` being confidence-only is
correct.

⚠️ **Consequence for this demo:** `SPIKE0_DEDUP_ON_CREATE=true` no longer reproduces a failing arm,
and the host's `DeduplicateOnCreate = false` override now works around a bug that is gone. Dropping
the override would let the demo run **shipped defaults**, which is the stronger story — it needs one
live verification run before the meeting, and until then the committed configuration is what was
rehearsed and what the screencast shows.

### 2. `as_of` moves both clocks, and a demo that ignores that looks broken while the engine is right

The first run of beat 5 returned **nothing** at March. That was correct: `RecallAsOfAsync` defaults
`systemAsOf` to `asOf`, so a March query asks "what did the system know in March" — and every fact had
been recorded seconds earlier, in August.

The wrong fix would have been to pin the transaction clock to "now" on the read, quietly answering a
different question than the caller asked. The right one was to record *when each thing was learned*:
`put(..., recorded_at=…)`. A real deployment gets that for free by having actually been running; a
demo that compresses eight months into one process has to say so.

Worth keeping in the demo script: bitemporality is two clocks, and the confusing case is real.

## Rules this respects

- **Zero diff to `src/`** — verified at every commit *of the demo track*. Finding 1 was reported and
  left unfixed here, exactly as the rule requires; it was fixed afterwards, on its own commits, with
  its own tests.
- **Pure Python, stdlib + `langgraph`** — no SDK, no generated client, nothing to install from us.
- **No PyPI, no npm, no repo publish, no README claim, no announcement.**
- The host answers `/v1/meta` with `PROTOTYPE` on its face.
