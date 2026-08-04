# Performance

What AgentMemory costs you per agent turn, how that cost is measured, and how to reproduce it.

| Doc | What it covers |
|---|---|
| **README.md** (this file) | The two phases, what is and isn't measured, how to run it yourself |
| [`hermetic-S.json`](../../eng/perf/baselines/hermetic-S.json) | Machine-readable counter + quality baseline enforced on pull requests |
| [baseline-1.3.0.md](baseline-1.3.0.md) | The measured cost model for release 1.3.0 |

---

## The two phases

AgentMemory plugs into the Microsoft Agent Framework at two points, and they have completely different
cost profiles. Almost every performance question about this library resolves to "which phase?".

```
                user message
                     │
   ┌─────────────────▼─────────────────┐
   │  PHASE 1 — RECALL                 │   ProvideAIContextAsync
   │  decide → embed query → retrieve  │   BLOCKS the model.
   │  → assemble prompt context        │   Cost lands on time-to-first-token.
   └─────────────────┬─────────────────┘
                     │
                  the model
                     │
   ┌─────────────────▼─────────────────┐
   │  PHASE 2 — INGESTION              │   StoreAIContextAsync
   │  persist messages → extract       │   Runs after the answer.
   │  → resolve → embed → persist      │   Cost lands on run-completion time
   └───────────────────────────────────┘   and on your model bill.
```

**Phase 1 is a latency problem. Phase 2 is mostly a cost problem.** They deserve to be tuned, budgeted,
and reasoned about separately — which is why every measurement here is reported per phase, never as a
single "memory overhead" figure.

| | Phase 1 — Recall | Phase 2 — Ingestion |
|---|---|---|
| MAF hook | `ProvideAIContextAsync` | `StoreAIContextAsync` |
| Blocks the user? | **Yes** — before the first token | No — after the answer |
| Dominant cost, local database | Database round trips | Persistence writes + entity resolution |
| Dominant cost, remote model | Query embedding | **Model completions** |
| Main tuning levers | `RecallOptions` limits, recall policy | `AutoExtractOnPersist`, extractor selection |

---

## What is measured, and what is not

This distinction matters more than any individual number.

### Structural counters — trustworthy, portable

Round trips, queries, materialized database records, estimated payload bytes, embedding requests, model
completions, tokens, items. These are **deterministic**: the same scenario on the same data produces
identical counts on any machine, in any environment. Two consecutive runs matched all 29 counters.

**These are safe to reason about, budget against, and compare across versions.** They are what
[baseline-1.3.0.md](baseline-1.3.0.md) leads with.

`neo4j.bytes_est` is permanently labelled an **estimate**, not Bolt wire bytes: the driver does not
expose wire volume. It sums the values callers materialize (`string` = 2 bytes/character; numeric
values = 8 bytes; lists/maps/nodes recursively sum their values) and is used for repeatable ratios.
A temporary entity projection reduced its transaction by 91.0%, proving the counter sees stored
vectors.

Every `memory.db.query` span also carries a safe `db.query.fingerprint`. Centralized constants use
their stable source name; recognized method-built shapes use a stable method name; anything else is
`unknown`. Reports group round trips by this value. Raw Cypher is never recorded because it can contain
embedded parameters. Two independent fresh-container runs produced the same query distributions and
the per-fingerprint totals exactly matched `neo4j.queries`.

### Quality guards — deterministic and enforced

Every performance run also executes 19 judged retrieval cases and 20 judged extraction cases. Retrieval
is scored with **deterministic-plumbing Recall@K/MRR** and forbidden-result checks; extraction is
scored with precision and recall per memory kind plus false positives on six turns that should teach
the system nothing.

That label is permanent, like `bytes_est`. The fixture uses the deterministic FNV-1a test embedder and
deliberately disjoint vocabulary, so 1.000 / 1.000 proves that retrieval wiring, ranking, scoping and
guard enforcement still behave exactly—not that a production embedding model has perfect semantic
quality. Sampled real-embedding/real-model quality belongs to M-27 (LongMemEval), with its model,
dataset, seed and retrieval configuration fingerprinted.

Five fresh-container runs produced identical values for every guarded metric, so the committed
tolerance is the observed variance: **zero**. The gate is on by default and returns a non-zero exit when
a score falls below `eng/perf/baselines/quality.json` or a forbidden retrieval appears.

### Timings — indicative only

Latency figures published here come from a local container with in-process stand-ins for the embedding
and model providers. **They are not deployment performance**, and we do not present them as such. Your
latency depends on where Neo4j lives relative to your app, which embedding model you use, how large your
graph is, and which model you call — none of which this harness holds fixed for you.

Timings are published to show **proportions** — which phase, and which stage inside it, dominates — not
absolute expectations. Where a figure is elapsed time and where it is a sum across concurrent work is
always labelled, because the two differ by an order of magnitude in the recall phase.

### Cold-versus-warm control

`perf cold` measures each cold observation in a new OS process, then runs a separate normally warmed
reference. For `PERF-R-04`, five ordered cold observations were 828.429, 1,044.650, 399.504, 442.386,
and 411.984 ms: median **442.386 ms**. Five warm observations after three warm-ups had a median of
**35.701 ms**, for a **12.391× cold penalty** on that machine and run.

The two populations are never combined. Every structural counter and safe query fingerprint matched:
43 retrieved records, 9 queries, 6 read transactions, 1 write transaction, and 25 access-tracked
items. The warm child also retained the normal zero-tolerance quality gate.

The manifest records exactly what “cold” means: a new process, JIT state, service provider, and driver
connection pool, plus explicit Neo4j query-plan-cache clearing after scenario setup. It does **not**
claim to reset the Neo4j page cache or host filesystem cache; fixture setup may touch both. These local
hermetic milliseconds are useful as an in-run ratio, not as deployment latency.

### Concurrent correctness and local saturation

`perf concurrency` is an opt-in reliability characterization against one fixed, fingerprinted product
driver pool (16 connections by default). It self-asserts owner-isolated reads, concurrent fact
dedup-on-create, and non-destructive owner-scoped supersession at 1, 10, and 100 logical sessions.

The first red probe proved the command was capable of finding a real defect: 10 concurrent same-owner
near-duplicate fact creates left 10 live facts. After serializing that process-local dedup decision and
scoping exact cosine comparison before ranking, the unchanged test left exactly 1 live fact. Every
other correctness guard stayed exact:

| Sessions | Errors | Owner leaks / misses | Live near-duplicates | Losers present / closed | Edges / live winners | Cross-owner edges |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 0 | 0 / 0 | 1 | 1 / 1 | 1 / 1 | 0 |
| 10 | 0 | 0 / 0 | 1 | 10 / 10 | 10 / 10 | 0 |
| 100 | 0 | 0 / 0 | 1 | 100 / 100 | 100 / 100 | 0 |

The same accepted local run reported request p50/p99 and throughput as follows. These numbers describe
that one hermetic run only; they are not deployment latency:

| Workload | Sessions | p50 ms | p99 ms | operations/s |
|---|---:|---:|---:|---:|
| owner-isolation read | 10 | 14.342 | 14.613 | 662.17 |
| dedup-on-create race | 10 | 202.582 | 255.409 | 38.92 |
| owner-scoped supersession | 10 | 22.076 | 22.167 | 442.39 |
| owner-isolation read | 100 | 1,537.608 | 3,060.237 | 32.65 |
| dedup-on-create race | 100 | 527.468 | 1,295.569 | 76.23 |
| owner-scoped supersession | 100 | 1,533.084 | 3,067.458 | 32.59 |

The artifact also reports `transaction_entry_ms_est` percentiles. This is permanently labelled an
upper-bound estimate: it includes connection acquisition, routing, and transaction begin, not exact
pool queue time. The correctness claim covers concurrent sessions inside one application process;
distributed dedup coordination across multiple application instances is not yet measured.

### Fail-fast torn-write rollback

Fail-fast extraction persistence prepares all external embeddings before opening one explicit Neo4j
transaction. Entity, fact, preference, relationship, provenance, temporal, and supersession repository
operations then join that transaction. Default best-effort mode retains its independent-write behavior.

A dedicated live-Neo4j integration test kills the Neo4j JVM after the first repository write returns
inside the transaction, verifies from a fresh driver that the database is unreachable, restarts the
same container, and compares an isolated-owner graph snapshot with its pre-turn state. The red-first
run without the atomic boundary left 1 entity and 1 provenance edge. With the boundary enabled, the
post-failure snapshot was empty; one exact retry produced 2 entities, 1 fact, 1 preference,
1 relationship, and 4 provenance edges, with no duplicates, invalidation, valid-time closure, or
supersession artifacts. The test also self-asserts that model/embedding calls finish before the
transaction opens and that the Neo4j coordinator and repositories share the same runner instance.

### Not yet measured

- **Managed/hosted deployments** — no Aura or NAMS figures yet.
### Scale-M validation

`--scale M` adds exactly 250,000 foreign-scope distractor memories: 50,000 each of entities, facts,
preferences, messages, and reasoning traces. The first invocation creates and seals a reusable Docker
volume; each measured run clones that template into a disposable volume and verifies the exact node
counts after restore.

On `PERF-R-04`, Scale S and Scale M performed the same structural work: 43 retrieved items, 25
access-tracked items, 9 queries, 6 read transactions, 1 write transaction, and 43 materialized records.
Deterministic-plumbing Recall@K, MRR, and every extraction-quality score remained 1.000. Estimated payload changed from
144,591 to 144,555 bytes (−36; −0.025%) and context length from 3,906 to 3,886 characters because the
approximate vector index selected a different equally relevant near-tied fixture item. A second
independent Scale-M restore reproduced 144,555 bytes and 3,886 characters exactly.

The warm restore path completed in 52–60 seconds on the development machine, including a 3.2–3.3
second Docker volume clone. This is a harness-usability result, **not deployment latency**.

---

## Matched `feat-01` before/after characterization

The exact pre-`feat-01` harness commit (`b1d924e9929b`) and post-`feat-01` commit (`0455c584ce`) were
rerun back-to-back on the same machine with zero provider latency, 10 measured iterations, and 3
warm-ups. “Full phase” is the elapsed time for the complete recall or ingestion harness phase.

| Full phase | Before p50 / p95 | After p50 / p95 | Movement | Interpretation |
|---|---:|---:|---:|---|
| Recall | **313.03 / 641.45 ms** | **50.59 / 113.11 ms** | **−262.44 ms (−83.8%) p50; −528.34 ms (−82.4%) p95** | Attributable to batching 25 access-tracking write transactions into 1; 43 retrieved and 25 tracked items held |
| Ingestion | **336.83 / 2,859.29 ms** | **221.92 / 352.90 ms** | −114.91 ms (−34.1%) p50 | Control variance only: `feat-01` did not change ingestion |

These are local hermetic characterization timings, not deployment latency. The portable causal result
is recall write transactions **25 → 1**, queries **31 → 9**, and total database round trips **31 → 7**,
with retrieved and access-tracked item guards unchanged.

---

## Measured improvements after the 1.3.0 baseline

| Improvement | Scenario | Portable counter | Before | After | Change |
|---|---|---|---:|---:|---:|
| Combined single-message Neo4j persistence | `PERF-W-02` | queries per turn | 43 | **40** | **−3 (−7.0%)** |
| Combined single-message Neo4j persistence | `PERF-W-03` | queries per turn | 88 | **70** | **−18 (−20.5%)** |
| Skip redundant provenance re-writes | `PERF-W-02` | write transactions per turn | 18 | **8** | **−10 (−55.6%)** |
| Skip redundant provenance re-writes | `PERF-W-02` | queries per turn | 40 | **30** | **−10 (−25.0%)** |
| Skip redundant provenance re-writes | `PERF-W-03` | write transactions per turn | 48 | **13** | **−35 (−72.9%)** |
| Skip redundant provenance re-writes | `PERF-W-03` | queries per turn | 70 | **35** | **−35 (−50.0%)** |
| Batch memory upserts | `PERF-W-02` | write transactions per turn | 8 | **6** | **−2 (−25.0%)** |
| Batch memory upserts | `PERF-W-02` | queries per turn | 30 | **28** | **−2 (−6.7%)** |
| Batch memory upserts | `PERF-W-03` | write transactions per turn | 13 | **11** | **−2 (−15.4%)** |
| Batch memory upserts | `PERF-W-03` | queries per turn | 35 | **33** | **−2 (−5.7%)** |
| Batch memory upserts | `PERF-W-05` | write transactions per extraction | 7 | **5** | **−2 (−28.6%)** |
| Batch memory upserts | `PERF-W-05` | queries per extraction | 28 | **26** | **−2 (−7.1%)** |
| Batch entity-resolution snapshots | `PERF-W-12-X01` | entity candidate reads per 40 sessions | 80 | **20** | **−60 (−75.0%)** |
| Batch entity-resolution snapshots | `PERF-W-12-X01` | total read transactions per 40 sessions | 120 | **60** | **−60 (−50.0%)** |
| Batch entity-resolution snapshots | `PERF-W-12-X01` | queries per 40 sessions | 930 | **870** | **−60 (−6.5%)** |
| Batch entity-resolution snapshots | `PERF-W-12-X01` | estimated payload bytes per 40 sessions | 2,583,298 | **2,053,922** | **−529,376 (−20.5%)** |

Message creation, optional embedding persistence, `HAS_MESSAGE`, `FIRST_MESSAGE`, and `NEXT_MESSAGE`
maintenance now execute as one parameterized Cypher operation. Write transactions remain 18 / 48,
message counts remain 1 / 6, and estimated payload remains 102,960 / 108,964 bytes. Deterministic
retrieval and extraction quality guards remain unchanged at 1.000, with a 0% extraction false-positive
rate. Local-container milliseconds are intentionally omitted because they are not deployment timings.

Neo4j entity, fact, and preference upserts already create every `EXTRACTED_FROM` edge from the
memory's source-message IDs. The core persistence stage now recognizes that internal capability and
does not issue the same `MERGE` again in a separate transaction per memory/message pair. Repositories
without the capability retain the existing explicit provenance behavior. The 50-message whole-session
guard still reads back exactly 250 provenance edges (5 learned memories × 50 source messages), while
payload, records, learned items, and deterministic quality stay unchanged.

The remaining entity and fact writes now use one atomic `UNWIND` upsert per memory kind when the
repository advertises batch support. The same opt-in capability also covers preferences and graph
relationships when a turn contains more than one; live Neo4j tests verify their owner, temporal,
embedding, metadata, and provenance fields. `ExtractionOptions.EnableBatchMemoryUpserts` can disable
the optimization. Default best-effort mode rolls a failed atomic batch back and replays the existing
item path so per-item outcomes are preserved; fail-fast mode intentionally keeps item writes inside
its whole-turn transaction so an error still identifies the exact failing item. Two fresh-container
runs reproduced every counter above exactly. Records, estimated bytes, learned items, and both
zero-tolerance quality guards were unchanged.

Multi-session extraction now fetches each owner/type entity candidate set once, prefetches independent
types concurrently, and updates that request-local snapshot as chronological sessions are resolved.
`ExtractionOptions.UseBatchEntityResolutionSnapshots` can disable the default-on optimization. A
remote-latency-shaped, fresh-container control/candidate characterization moved the X01 extraction-wave
p50 from **47,341.00 to 32,600.96 ms (−31.1%)** and X10 from **7,568.38 to 3,621.08 ms
(−52.2%)**. Writes remained 250; model calls 10; embedding work 130 requests / 720 items; the learned
80/40/40/40 entity/fact/preference/relationship graph, provenance, source order, owner isolation, and
both zero-tolerance quality gates were unchanged. These milliseconds include injected provider delay
and local Docker orchestration; they are controlled-host causal evidence, not deployment latency.
The related five-worker scaling gate reached **2.991×** rather than the locked 3.000×, so the broader
cold-build phase remains fail-closed pending the separate persistence candidate.

### Cold structured-memory build laboratory

These opt-in laboratory arms measure preparation-workflow candidates; they are not yet shipped
AgentMemory defaults and their controlled-host milliseconds are not deployment latency.

| Candidate | Controlled comparison | Before p50 / p95 | After p50 / p95 | Movement | Correctness guards |
|---|---|---:|---:|---:|---|
| Batch 50 raw-message embeddings + writes | `PERF-W-06` control/candidate | 167.84 / 323.08 ms | 60.24 / 86.03 ms | **−64.1% / −73.4%** | 50 messages/vectors; requests 50 → 1; queries 102 → 1; quality 1.000 |
| One typed extraction response | `PERF-W-07` → `PERF-W-09` | 903.66 / 909.96 ms | 908.79 / 916.96 ms | +0.6% / +0.8% wall; calls **4 → 1**; total tokens **979 → 353** | Exact 2/2/1/1 output; zero retries/failures; quality 1.000 |
| Bounded independent-owner cold build | `PERF-W-10-C01` → `PERF-W-10-C10` | 34,202.82 / 47,516.61 ms | 3,195.68 / 4,732.44 ms | **10.70× / 10.04× faster** | Exact 10 calls, 10 messages, 20/20/10/10 learned graph, 80 embeddings, 40/70/270 reads/writes/queries, provenance/isolation, quality 1.000 |

The unified response reduces provider capacity and token cost, but not one-unit wall time because the
four original category calls already overlap. The wall-time lever is bounded concurrency across
independent owners. The next gate integrates that evidence into the prepared LongMemEval cold-build
path and must project the fixed ten-question build below 15 minutes before another full build is run.

---

## Reproduce it yourself

Requires Docker. The harness provisions its own pinned Neo4j, so it does not touch your database.

```bash
# Isolates database and CPU cost — no injected provider latency
dotnet run --project tools/AgentMemory.Cli -- perf --label mine --iterations 10

# Reproduces the shape of a remote deployment (embedding 120 ms, model 900 ms)
dotnet run --project tools/AgentMemory.Cli -- perf --label mine --latency remote --iterations 10

# Exercises the scenario-scoped degraded control (embedding 2 s, each DB transaction 250 ms)
dotnet run --project tools/AgentMemory.Cli -- perf --label degraded \
  --scenarios PERF-R-07 --iterations 3

# Measures full memory + deterministic GraphRAG orchestration
dotnet run --project tools/AgentMemory.Cli -- perf --label graphrag \
  --scenarios PERF-R-08 --iterations 3

# Measures one whole-session extraction over 50 pre-seeded messages
dotnet run --project tools/AgentMemory.Cli -- perf --label session-extraction \
  --scenarios PERF-W-05 --iterations 3


# Isolates resolution, learned-memory embeddings, persistence, provenance, and owner isolation
dotnet run --project tools/AgentMemory.Cli -- perf --label frozen-persistence \
  --scenarios PERF-W-08 --iterations 10

# Compares the shipped four-call extractor with one typed unified extraction call
dotnet run --project tools/AgentMemory.Cli -- perf --label unified-extraction \
  --scenarios PERF-W-07,PERF-W-09 --latency remote --iterations 10

# Measures ten complete owner-isolated cold-build units at 1, 5, and 10 workers
dotnet run --project tools/AgentMemory.Cli -- perf --label cold-build-concurrency \
  --scenarios PERF-W-10-C01,PERF-W-10-C05,PERF-W-10-C10 \
  --latency remote --iterations 3

# Restores the reusable 250k-node Scale-M dataset, then runs the same guarded scenario
dotnet run --project tools/AgentMemory.Cli -- perf --label scale-m \
  --scale M --scenarios PERF-R-04 --iterations 1

# Five fresh-process cold samples plus a separate three-warm-up reference
dotnet run --project tools/AgentMemory.Cli -- perf cold --label cold-r04 \
  --scenarios PERF-R-04 --samples 5 --warmup 3

# Opt-in concurrent correctness + local saturation (fixed 16-connection product pool)
dotnet run --project tools/AgentMemory.Cli -- perf concurrency --label concurrency \
  --levels 1,10,100 --pool-size 16

# Compare two in-process recall configurations, with quality in the same report
dotnet run --project tools/AgentMemory.Cli -- perf ab \
  --control default \
  --candidate Recall.MaxEntities=2 \
  --iterations 30

# Check one completed run against the committed counter + quality baseline
dotnet run --project tools/AgentMemory.Cli -- perf gate \
  --baseline eng/perf/baselines/hermetic-S.json \
  --report <path-to-summary.json>

# Deliberately refresh the reviewable baseline from a completed run
dotnet run --project tools/AgentMemory.Cli -- perf baseline --update \
  --report <path-to-summary.json>
```

Each run writes a dated directory containing a manifest with the full environment fingerprint, an
append-only trace log, per-iteration samples, a machine-readable summary, and a rendered report. The
report includes exact round trips grouped by safe query fingerprint. The quality gate is on by default;
`--quality-gate=false` is available for diagnostic runs, and the report
marks those scores as report-only.

Run it at both latency settings. A change that improves only the `remote` shape is an ordering or
overlap win; one that improves both removed work.

### Trustworthy A/B comparisons

`perf ab` counterbalances execution order and crosses each configuration over two equivalent,
owner-isolated fixture copies in the same database. Six consecutive paired iterations form one
bootstrap unit, preserving the Docker/driver timing correlation instead of treating adjacent samples
as independent. `--iterations` must therefore be a multiple of six and at least 12; 30 is the default.

The initial configuration grammar accepts `default` plus `Recall.Max*` and
`Recall.MinSimilarityScore` assignments. `PERF-R-04` measures a full default recall, while
`PERF-R-01` is a greeting-only control that locks the work performed by the shipped default recall
policy. State-mutating scenarios `PERF-W-02`, `PERF-W-03`, and `PERF-W-05` are rejected because their
writes would invalidate paired-sample independence.

The rendered markdown reports exact counter ranges, retrieval and extraction quality, and the
candidate/control bootstrap interval together. Only `iteration total` is the pre-registered timing
headline; subspan intervals are exploratory and are not corrected for multiple comparisons.

### Determinism

The harness uses a deterministic embedding function and a scripted model, so counters are reproducible.
The judged quality fixtures had zero observed variance across five complete runs, which is why their
tolerance is zero rather than a guessed allowance.

All measured scenarios also **self-assert**. The full and degraded recall scenarios fail if they
retrieve fewer items than the configured limits; the degraded scenario additionally verifies that
its embedding and database waits occurred inside recorded stage spans. The GraphRAG scenario requires
its source call, two known items, configured wait, stage span, and rendered marker text while preserving
the complete memory result. The greeting scenario locks its current default-policy item shape, while
the per-turn ingestion scenarios verify message persistence and extraction outcomes. Whole-session
extraction additionally requires exactly 50 source messages and reads the graph back after the measured
turn to prove that two entities, two facts, one preference, and 250 provenance relationships were
actually stored. Fixture setup and graph verification are outside the measured scope.
`PERF-W-08` separately bypasses model extraction for one harness-only marker, then exercises the real
resolution-to-persistence product path. It requires zero model/storage/recall work inside the measured
turn, exact 2/2/1/1 learned graph output, all supported source provenance, and zero cross-owner edges.
Its deterministic embedding request count includes both semantic entity-resolution probes and
learned-memory embeddings.
`PERF-W-09` exercises the typed unified extractor directly over the same 2/2/1/1 shape as
`PERF-W-07`, requires exactly one purpose-attributed model call with zero retries, and rejects any
storage, resolution, embedding, persistence, or recall work. These self-assertions catch failures
that would otherwise be silent and produce a confident, wrong number.

### Pull-request regression gate

A dedicated GitHub Actions job runs the hermetic scale-S profile with both zero and remote-like
provider latency. Each report is compared with
[`eng/perf/baselines/hermetic-S.json`](../../eng/perf/baselines/hermetic-S.json). The gate rejects:

- any increase in Neo4j transactions or queries, embedding requests, or model calls;
- an estimated payload-byte increase above 5%; and
- any retrieval or extraction quality regression beyond the committed tolerance (currently zero).

A deliberate structural-counter increase needs both the `perf-counter-change` pull-request label and
a non-empty pull-request-body line in this exact form:

```text
Perf counter change justification: <why the extra work is intentional>
```

That acknowledgement cannot override payload or quality failures. Hermetic elapsed milliseconds are
reported for diagnosis but are deliberately excluded from the CI decision because runner timings are
not portable.

---

## Tuning starting points

Neither is a default change — both trade something. Measure before and after with the commands above.

**Phase 1 — reduce recall latency.** Lower the per-category limits in `RecallOptions`; the shipped
defaults retrieve up to 43 items per turn. Supply a task-aware `IAutomaticRecallPolicy` so turns that
need no memory skip retrieval entirely. Both trade recall quality for latency.

**Phase 2 — reduce ingestion cost.** `AgentFrameworkOptions.AutoExtractOnPersist` defaults to `true`,
so every turn runs the full extraction pipeline. Setting it to `false` and calling
`ExtractFromSessionAsync` on a schedule trades learning latency for a large reduction in model spend.
Registering fewer extractors reduces completions proportionally.

---

## A note on how these numbers are produced

The instrumentation lives in the product, not in the benchmark. Spans are emitted from the same code
paths you run, and the harness is a passive `ActivityListener` — so the measured build and the shipped
build are the same build. When no listener is attached, `ActivitySource.StartActivity` returns null and
the cost is a null check.

That also means **you can collect these same measurements from your own deployment** by subscribing to
the `AgentMemory` `ActivitySource` with OpenTelemetry. The spans are documented in
[baseline-1.3.0.md](baseline-1.3.0.md).
