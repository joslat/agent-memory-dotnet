# Cost model — release 1.3.0

What one agent turn costs at shipped defaults, measured per phase.

**Measured:** 2026-07-25 · AgentMemory 1.3.0 · Neo4j 5.26 (container) · deterministic embeddings
(384-dim) · scripted model · ~5k-node graph · 10 iterations after 3 warm-up · .NET 9.

**Reproduce:** `dotnet run --project tools/AgentMemory.Cli -- perf --label mine --iterations 10`

> Read [README.md](README.md) first if you have not. In particular: the counters below are portable and
> reproducible; the timings are proportions from a local container, **not** deployment performance.

---

## 1. Structural cost — the portable part

Per turn, at shipped defaults. **Two consecutive runs produced identical values for all 27 counters**,
on every one of these.

| | Phase 1 — Recall | Phase 2 — Ingestion |
|---|---:|---:|
| Neo4j **read** transactions | 6 | 4 |
| Neo4j **write** transactions | 1 | 18 |
| Neo4j queries | 9 | 43 |
| Embedding provider requests | 1 | 4 |
| **Model completions** | 0 | **4** |
| Model tokens (in / out) | – | 947 / 668 |
| Memory items returned | 43 | – |
| Prompt characters added | 3,901 (≈975 tokens) | – |

### What drives each number

**Phase 1 — recall.** Six reads: recent messages, semantic message search, entities, facts,
preferences, reasoning traces — issued concurrently, one per enabled category. One embedding request for
the query. One write, batching the access-timestamp updates for every recalled long-term item. The 43
items are the shipped `RecallOptions` limits: 10 recent + 5 relevant messages, 10 entities, 10 facts,
5 preferences, 3 traces.

**Phase 2 — ingestion.** The four model completions are the headline: the LLM extraction backend
registers **one extractor per memory kind** — entity, fact, preference, relationship — and each issues
its own completion carrying its own copy of the turn. They run concurrently, so they cost roughly one
completion of *latency* but four completions of *spend*. The four embedding requests are one per
extracted memory. The 18 writes are the message plus the extracted entities, facts, preferences, and
their provenance edges.

### Greeting-only default-policy control

`PERF-R-01` sends “thanks, that's great” through the shipped default recall policy. Even though the
turn does not need memory, the policy still performs the recall pipeline:

| Counter | Per greeting turn |
|---|---:|
| Neo4j read / write transactions | 6 / 1 |
| Neo4j queries | 7 |
| Embedding provider requests | 1 |
| Items retrieved | 11 |
| Access timestamps updated | 1 |

The 11 items are 10 recent messages plus one deterministic preference-bucket match; no semantically
relevant message is returned. This is a control for selective/task-aware recall: the optimization
target is to drive all recall work to zero on a skipped greeting while the retrieval quality guard
remains unchanged. Hermetic milliseconds are intentionally not used for this claim.

### Degraded-dependency recall control

`PERF-R-07` applies a scenario-scoped deterministic stimulus: 2,000 ms for query embedding and 250 ms
inside each database transaction. Current behavior has no recall deadline, so it waits and returns the
same complete shape as `PERF-R-04`:

| Counter | Normal full recall (`R-04`) | Degraded (`R-07`) |
|---|---:|---:|
| Neo4j read / write transactions | 6 / 1 | **6 / 1** |
| Neo4j queries | 9 | **9** |
| Embedding requests | 1 | **1** |
| Items retrieved | 43 | **43** |
| Access timestamps updated | 25 | **25** |
| Configured embedding wait | – | **1 × 2,000 ms** |
| Configured database wait | – | **7 × 250 ms** |

In the five-iteration hermetic run, the embedding span had a 2,009 ms median, the seven transaction
spans summed to 1,958 ms, and elapsed turn time was 2,566 ms because the six reads overlap. These are
controlled-stimulus validation figures—not deployment performance. The portable finding is that the
entire 43-item result is preserved and both degraded stages are now observable. Timeout/deadline work
can use this control to prove bounded completion; graceful-degradation work must additionally report
which categories were omitted rather than silently returning less.

### GraphRAG orchestration control

`PERF-R-08` adds a scenario-only deterministic GraphRAG source to the complete `PERF-R-04` memory
shape. It returns two known context items after a configured 300 ms wait:

| Counter | Full recall (`R-04`) | GraphRAG (`R-08`) |
|---|---:|---:|
| Memory items retrieved | 43 | **43** |
| GraphRAG items | – | **2** |
| Prompt messages | 14 | **15** |
| Embedding requests | 1 | **1** |
| Neo4j read / write transactions | 6 / 1 | **6 / 1** |
| Neo4j queries | 9 | **9** |
| Access timestamps updated | 25 | **25** |
| Configured GraphRAG wait | – | **1 × 300 ms** |

The portable result is that enabling this optional path adds its two known items without changing or
dropping any of the 43 memory items. One `memory.recall.graphrag` span and the marker text in the final
Agent Framework context prove the source was registered, enabled, invoked, and materialized.

The remote-shaped hermetic run also validates rank 17's orchestration target. The provider embedding
span had a 126 ms median and completed before the 312 ms GraphRAG span because the provider currently
awaits embedding before entering the assembler; median turn elapsed was 468 ms. Rank 17 should overlap
those controlled stages so their contribution tends toward the slower stage instead of their sum.
These are deterministic stimulus figures for an A/B control—not deployment performance.

### Six-message tool-heavy ingestion control

`PERF-W-03` holds the scripted extraction result constant while increasing response messages from one
to six. This isolates the structural fan-out of a tool-heavy MAF turn:

| Counter | One message (`W-02`) | Six messages (`W-03`) | Change |
|---|---:|---:|---:|
| Messages persisted | 1 | **6** | +5 |
| Embedding requests | 4 | **9** | +5 |
| Neo4j read transactions | 4 | **4** | 0 |
| Neo4j write transactions | 18 | **48** | +30 |
| Neo4j queries | 43 | **88** | +45 |
| Model completions | 4 | **4** | 0 |
| Model input tokens | 947 | **1,170** | +223 |
| Persisted entities / facts / preferences | 2 / 2 / 1 | **2 / 2 / 1** | 0 |

The write increase is larger than the five extra message nodes. The same five extracted memories
remain guarded, and each gains an `EXTRACTED_FROM` link to each of the five additional source
messages: **5 message writes + (5 memories × 5 provenance links) = 30 additional writes**. Query
growth similarly consists of four message-persistence queries per additional message plus those 25
provenance queries: **(5 × 4) + 25 = 45**. This control makes true batch response-message persistence
measurable while also exposing the separate provenance fan-out it will not remove.

### Whole-session extraction control

`PERF-W-05` bulk-seeds 50 messages outside the measured scope, then measures the production
`ExtractFromSessionAsync` path once. A post-turn raw-driver check, also outside the measured scope,
proves that the graph contains every input and learned item:

| Counter | Whole-session extraction (`W-05`) |
|---|---:|
| Source messages read | **50** |
| Model completions | **4** |
| Model input / output tokens | **7,774 / 668** |
| Embedding requests | **7** |
| Neo4j read / write transactions | **5 / 257** |
| Neo4j queries | **278** |
| Persisted entities / facts / preferences | **2 / 2 / 1** |
| Provenance relationships found by graph read-back | **250** |

Two fresh-container runs produced the same exact counters; both judged quality fixtures remained at
1.000 with zero forbidden retrievals and zero false positives.

The four model calls are one per extractor category regardless of transcript length. They lock the
session-end side of matrix rank 8's comparison: extracting every one of 20 turns separately costs
80 completions today, while one final session extraction costs four, a predicted **95% reduction**.
That reduction is the target of the later optimization, not a product improvement shipped by this
measurement scenario; extracted-item count and judged quality remain mandatory guards.

The 257 writes expose a separate current cost. The five learned memories each link to all 50 source
messages, producing 250 per-message provenance transactions in addition to seven entity-resolution
and memory-upsert writes. Repository upserts also issue their own bulk provenance queries, so the
resulting graph has 250 unique `EXTRACTED_FROM` relationships while the measured path executes 278
Cypher queries. This fan-out is now visible for a later batching optimization; it is not hidden from
the baseline.

### How these scale

Worth knowing before you tune anything:

| If you change | Phase 1 | Phase 2 |
|---|---|---|
| Raise a `RecallOptions` limit | reads unchanged; items, prompt size, and tracked writes grow | – |
| Enable another recall category | +1 read transaction | – |
| Register another extractor | – | **+1 model completion per turn** |
| Turn produces more memories | – | writes and embedding requests grow linearly |
| Grow the graph | read cost grows with index behaviour (not yet characterised) | resolution cost grows |

### Quality guard applied beside this cost baseline

The cost counters are only accepted when deterministic quality remains at this committed baseline:

| Guard | Baseline |
|---|---:|
| Retrieval Recall@K / MRR | 1.000 / 1.000 |
| Retrieval cases with forbidden results | 0 of 19 |
| Entity precision / recall | 1.000 / 1.000 |
| Fact precision / recall | 1.000 / 1.000 |
| Preference precision / recall | 1.000 / 1.000 |
| Extraction false positives on learn-nothing turns | 0 of 6 (20 total cases) |

Every value above was identical across five fresh-container runs: maximum observed variance **0.000**.
The derived tolerance is therefore **zero**, recorded in
`eng/perf/baselines/quality.json`. The combined reviewable counter + quality snapshot used by pull
request CI is [`eng/perf/baselines/hermetic-S.json`](../../eng/perf/baselines/hermetic-S.json).
The `perf` command gates quality by default, while `perf gate` also rejects structural-counter
regressions against that snapshot. Neither command grades hermetic elapsed milliseconds. This guard is
about deterministic pipeline behavior; it does not claim to score
the quality of a live model's prose.

---

## 2. Where the time goes — proportions, not expectations

Same turn, measured twice: once with no injected provider latency (isolating database and CPU work),
once with a remote-like shape (embedding 120 ms, model 900 ms). **The two tell different stories, which
is the single most useful thing on this page.**

### Phase 1 — Recall

| | No provider latency | Remote-like |
|---|---:|---:|
| **Total elapsed** | **29 ms** | **155 ms** |
| Query embedding | ~0 | ~120 ms |
| Category retrieval (6 concurrent) | ~10 ms each | ~11 ms each |
| Access-tracking write | 19 ms | 18 ms |

With a local database, recall is database-bound and small. With a remote embedding provider, **the
single query embedding dominates everything else combined**. If you are optimising recall latency in a
real deployment, that is where to look first — not at the database.

### Phase 2 — Ingestion

| | No provider latency | Remote-like |
|---|---:|---:|
| **Total elapsed** | **218 ms** | **1,718 ms** |
| Extraction (4 concurrent completions) | ~0 | **~908 ms** |
| Persistence of extracted memories | 128 ms | 579 ms |
| Entity resolution | 41 ms | (within extraction) |
| Message persistence | 22 ms | 24 ms |

With a local database and an instant model, ingestion is dominated by **persisting** what was extracted
(≈59%) and by **entity resolution** (≈19%) — not by extraction itself. Add a real model and the picture
inverts: the four completions become the cost.

Note the concurrency effect: four completions at ~908 ms each contribute **~908 ms of elapsed time**,
not 3.6 s. Concurrency hides the cost in latency — but not in the bill, and not in your provider's rate
limit.

---

## 3. Collecting these from your own deployment

The instrumentation is in the product, not the harness. Subscribe to the `AgentMemory`
`ActivitySource` with OpenTelemetry and you get the same breakdown from live traffic.

**Phase 1 — recall**

| Span | Covers |
|---|---|
| `memory.recall.total` | The whole phase. Tags: item counts per category, total, characters |
| `memory.recall.embedding` | Query embedding |
| `memory.recall.recent` / `.messages` | Recent history / semantic message search |
| `memory.recall.entities` / `.facts` / `.preferences` / `.traces` | Long-term category retrieval |
| `memory.recall.graphrag` | GraphRAG, when enabled |
| `memory.recall.access_tracking` | Access-timestamp writes. Tag: items tracked |

**Phase 2 — ingestion**

| Span | Covers |
|---|---|
| `memory.store.messages` | Response-message persistence. Tag: message count |
| `memory.store.extract` | The whole extraction + persistence stage |
| `memory.extract.entity` / `.fact` / `.preference` / `.relationship` | One per extractor category |
| `memory.extract.resolution` | Entity resolution. Tag: candidate entities |
| `memory.persist.total` | Persistence. Tags: counts per memory kind |

**Both phases**

| Span | Covers |
|---|---|
| `memory.db.tx` | One per database transaction. Tag: `db.mode` = read / write |
| `memory.db.query` | One per Cypher query. **Counts are exact; duration covers dispatch only**, since results stream after the span closes |

Cost is a null check when nothing is listening.

---

## 4. Honest limits of this page

- Timings are local-container with stand-in providers. **Not deployment performance.**
- Payload bytes are not measured; recall currently materialises embedding vectors it does not use, and
  that cost is not yet quantified.
- Cold start is not measured — everything here is warm.
- One graph size (~5k nodes), one session, no concurrency or saturation figures.
- No managed-database (Aura) or hosted-backend (NAMS) figures.

Anything not listed in section 1 should be treated as directional. When these gaps close, this page will
say so and the numbers will be dated.
