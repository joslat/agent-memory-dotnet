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

### 1. `mention_count` is never incremented by the single-add API — so the working-memory tier admits nothing added through it

**Confirmed by measurement, both arms, on this host.**

`Fact` upserts MERGE on the triple and `ON MATCH SET f.mention_count = coalesce(f.mention_count,1)+1`.
But `LongTermMemoryService.AddFactCoreAsync` only reaches that MERGE when dedup-on-create is off or
the embedding is missing. With `LongTerm.DeduplicateOnCreate = true` — **the default** — a re-asserted
fact goes to `FindDuplicateAsync` → `MarkDeduplicatedAsync`, and that Cypher is:

```cypher
MATCH (f:Fact {id: $id}) SET f.confidence = $confidence RETURN f
```

Confidence is reinforced; `mention_count` is not touched. An exact byte-identical triple takes this
path too, since it trivially clears the similarity threshold.

Measured, re-asserting two facts twice each:

| `DeduplicateOnCreate` | `mention_count` | working-memory block |
|---|---|---|
| `true` (default) | 1 | empty — nothing compiled |
| `false` | 2 | compiled, both stable facts present |

The working-memory tier's admission rule is `coalesce(f.mention_count,1) >= MinFactMentionCount`,
default **2**. So at shipped defaults, a fact ingested through `AddFactAsync` can never become stable,
however many times the world re-asserts it. `MentionFrequencyReranker` loses the same signal.

**Scope — the shipped conversational pipeline is unaffected.** Extraction persists through
`PersistenceStage` → `UpsertBatchAsync`, whose `ON MATCH` increments correctly, and whose comment
explicitly says the counter must not "depend on which write path ran". That is exactly the invariant
the single-add path breaks. The gap is on the direct-API surface — which is what this adapter, and any
non-conversational integration, uses.

**Not fixed here.** The demo track's binding rule is zero diff to `src/`, and this is a `src/` change
with test implications (the fix is presumably `SET f.mention_count = coalesce(f.mention_count,1)+1` in
`MarkDeduplicated`, plus the preference twin). The spike host sets `DeduplicateOnCreate = false` with a
comment naming this finding — to make the tier **observable**, not to paper over it.
`SPIKE0_DEDUP_ON_CREATE=true` reproduces the failing arm.

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

- **Zero diff to `src/`** — verified at every commit. Finding 1 is reported, not fixed.
- **Pure Python, stdlib + `langgraph`** — no SDK, no generated client, nothing to install from us.
- **No PyPI, no npm, no repo publish, no README claim, no announcement.**
- The host answers `/v1/meta` with `PROTOTYPE` on its face.
