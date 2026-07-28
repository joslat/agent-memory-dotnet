# Cost model — release 1.3.0

What one agent turn costs at shipped defaults, measured per phase.

**Measured:** 2026-07-25 · AgentMemory 1.3.0 · Neo4j 5.26 (container) · deterministic embeddings
(384-dim) · scripted model · ~5k-node graph · 10 iterations after 3 warm-up · .NET 9.

**Reproduce:** `dotnet run --project tools/AgentMemory.Cli -- perf --label mine --iterations 10`

> Read [README.md](README.md) first if you have not. In particular: the counters below are portable and
> reproducible; the timings are proportions from a local container, **not** deployment performance.
> This file remains the immutable 1.3.0 reference. Measured post-baseline changes are listed in
> [README.md](README.md#measured-improvements-after-the-130-baseline).

---

## 1. Structural cost — the portable part

Per turn, at shipped defaults. **Two consecutive runs produced identical values for all 29 counters**,
on every one of these.

| | Phase 1 — Recall | Phase 2 — Ingestion |
|---|---:|---:|
| Neo4j **read** transactions | 6 | 4 |
| Neo4j **write** transactions | 1 | 18 |
| Neo4j queries | 9 | 43 |
| Neo4j materialized records | **43** | **32** |
| Neo4j estimated payload bytes | **144,591** | **102,960** |
| Embedding provider requests | 1 | 4 |
| **Model completions** | 0 | **4** |
| Model tokens (in / out) | – | 947 / 668 |
| Memory items returned | 43 | – |
| Prompt characters added | 3,906 (≈977 tokens) | – |

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

### Payload volume and causal validation

`neo4j.bytes_est` is an estimate of values materialized through the Neo4j cursor, not a claim about
Bolt wire bytes. The driver exposes values but not protocol volume, so the estimator uses a documented,
stable rule: UTF-16 strings are two bytes per character; numeric values are eight bytes; and
lists/maps/nodes recursively sum their values. It is meaningful as an exact fixture counter and as a
before/after ratio.

Two fresh-container combined runs matched exactly: full recall materialized 43 records / 144,591
estimated bytes, and default ingestion materialized 32 / 102,960. The complete seven-scenario run was
also deterministic.

The counter was tested causally, not accepted because it produced a plausible number. A temporary
entity recall map projection omitted the stored vectors:

| Metric | Full node | Projected fields | Change |
|---|---:|---:|---:|
| Entity-search transaction | 33,758 bytes | 3,038 bytes | **−91.0%** |
| Complete 43-item recall turn | 144,591 bytes | 113,871 bytes | **−21.2%** |

The 30,720-byte difference is exactly 10 returned entities × 384 vector values × 8 estimated bytes.
Retrieved items, access tracking, queries, transactions, deterministic-plumbing Recall@K, and MRR were unchanged. The
projection was then reverted; it is the future rank-6 optimization, not part of this measurement change.

### Round trips by query

Two independent fresh-container runs produced the same distribution. The row totals are exactly the
9 recall queries and 43 ingestion queries above. Values are stable source identifiers, never Cypher
text.

**Phase 1 — recall**

| Query fingerprint | Round trips |
|---|---:|
| `DecayQueries.UpdateAccessTimestampBatch` | **3** |
| `EntityQueries.SearchByVector` | 1 |
| `FactQueries.SearchByVector` | 1 |
| `MessageQueries.GetRecentBySession` | 1 |
| `MessageQueries.SearchByVector` | 1 |
| `PreferenceQueries.SearchByVector` | 1 |
| `ReasoningQueries.SearchByTaskVector` | 1 |

The three access-update queries are one per recalled long-term kind—entity, fact, and preference—but
all run inside the single write transaction delivered by feat-01.

**Phase 2 — ingestion**

| Query fingerprint | Round trips |
|---|---:|
| `EntityQueries.CreateExtractedFrom` | 4 |
| `EntityQueries.UpdateEmbedding` | 4 |
| `EntityQueries.Upsert` | 4 |
| `FactQueries.CreateExtractedFrom` | 4 |
| `FactQueries.UpdateEmbedding` | 2 |
| `FactQueries.Upsert` | 2 |
| `MessageQueries.Add` | 1 |
| `MessageQueries.CreateFirstMessageLink` | 1 |
| `MessageQueries.LinkNextMessage` | 1 |
| `PreferenceQueries.CreateExtractedFromMessages` | 1 |
| `PreferenceQueries.CreateExtractedFromRelationship` | 2 |
| `PreferenceQueries.SetEmbedding` | 1 |
| `PreferenceQueries.Upsert` | 1 |
| `unknown` | 15 |

`unknown` is an explicit safe fallback for method-built or consumer-supplied queries not recognized by
the stable registry. It preserves the exact total without exposing query text or assigning a misleading
name.

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
| Grow the graph | exact structural work held at Scale M; index-dependent selection can change near ties | resolution cost grows |

#### Scale-M structural control

The harness also validates `PERF-R-04` after restoring a reusable dataset containing exactly 250,000
foreign-scope distractor memories (50,000 per memory kind). This is a scale control beside the
small-graph 1.3.0 baseline, not a replacement deployment-performance baseline.

| Counter or guard | Scale S | Scale M | Change |
|---|---:|---:|---:|
| Retrieved items | 43 | 43 | 0 |
| Access-tracked items | 25 | 25 | 0 |
| Queries | 9 | 9 | 0 |
| Read / write transactions | 6 / 1 | 6 / 1 | 0 / 0 |
| Materialized records | 43 | 43 | 0 |
| Estimated payload bytes | 144,591 | 144,555 | −36 (−0.025%) |
| Context characters | 3,906 | 3,886 | −20 (−0.512%) |
| Deterministic-plumbing Recall@K / MRR | 1.000 / 1.000 | 1.000 / 1.000 | 0 / 0 |
| Extraction quality | 1.000 | 1.000 | 0 |

The small payload/context difference repeated exactly across independent Scale-M restores. Neo4j's
approximate vector index selected a different equally relevant near-tied fixture item; no category,
item, query, transaction, record, access-tracking, or quality guard regressed. Warm restore plus the
guarded run completed in 52–60 seconds locally, including a 3.2–3.3 second volume clone. Those setup
figures establish that the tier is practical to run; they are not deployment latency.

### Quality guard applied beside this cost baseline

The cost counters are only accepted when deterministic regression guards remain at this committed baseline:

| Guard | Baseline |
|---|---:|
| Deterministic-plumbing Recall@K / MRR | 1.000 / 1.000 |
| Retrieval cases with forbidden results | 0 of 19 |
| Entity precision / recall | 1.000 / 1.000 |
| Fact precision / recall | 1.000 / 1.000 |
| Preference precision / recall | 1.000 / 1.000 |
| Extraction false positives on learn-nothing turns | 0 of 6 (20 total cases) |

“Deterministic-plumbing” is a permanent scope label, not a footnote. The FNV-1a test embedder and
deliberately disjoint fixture vocabulary make expected neighbors construction-stable; these scores
prove retrieval wiring, ranking, scoping and forbidden-result enforcement. They do **not** claim
perfect semantic quality from a production embedding model. Sampled real-embedding/real-model quality
belongs to M-27 (LongMemEval).

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

### Cold-start control

Cold observations are measured in separate processes and kept out of the warm distributions above.
For `PERF-R-04` under the zero-latency hermetic profile:

| Population | Ordered samples (ms) | Median | Comparison |
|---|---|---:|---:|
| Cold, fresh process | 828.429, 1,044.650, 399.504, 442.386, 411.984 | 442.386 ms | 12.391× warm |
| Warm, after 3 warm-ups | 35.701, 33.929, 36.054, 33.330, 43.167 | 35.701 ms | baseline |

Every exact structural guard and safe query fingerprint matched across both populations: 43 records,
9 queries, 6 reads, 1 write, 25 access-tracked items, and the same per-query distribution. The warm
child retained the committed zero-tolerance quality gate.

“Cold” is deliberately bounded: each observation resets the OS process, JIT state, service provider,
and driver connection pool, and explicitly clears Neo4j's query-plan cache after scenario preparation.
The harness does **not** claim to reset Neo4j's page cache or the host filesystem cache because setup
may touch both. The 12.391× ratio demonstrates a first-scenario-call penalty in this controlled run;
the milliseconds are not a deployment-performance claim.

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
| `memory.db.tx` | One per database transaction. Tags: `db.mode` = read / write, `db.records`, `db.bytes_est` |
| `memory.db.query` | One per Cypher query. Tag: safe `db.query.fingerprint`. **Counts are exact; duration covers dispatch only**, since results stream after the span closes |

When nothing is listening, the original work delegate, query runner, and cursor pass through unchanged;
the payload accumulator, cursor wrapper, and estimator are not allocated or executed.

---

## 4. Honest limits of this page

- Timings are local-container with stand-in providers. **Not deployment performance.**
- Payload is a deterministic value-size **estimate**, not Bolt wire bytes or managed allocation volume.
  Use it for ratios, not bandwidth billing.
- Cold start is not measured — everything here is warm.
- One graph size (~5k nodes), one session, no concurrency or saturation figures.
- No managed-database (Aura) or hosted-backend (NAMS) figures.

Anything not listed in section 1 should be treated as directional. When these gaps close, this page will
say so and the numbers will be dated.
