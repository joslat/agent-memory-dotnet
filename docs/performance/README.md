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
is scored with Recall@K, MRR, and forbidden-result checks; extraction is scored with precision and
recall per memory kind plus false positives on six turns that should teach the system nothing.

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

### Not yet measured

Stated plainly rather than left for you to discover:

- **Cold start** — every figure here is warm. First-call cost after process start, pool expiry, or a
  cache-cold database is not yet characterised.
- **Scale** — the published baseline is a small graph (~5k memory nodes). Behaviour as the graph grows
  is not yet published.
- **Managed/hosted deployments** — no Aura or NAMS figures yet.
- **Concurrency** — single-session only; no saturation or p99-under-load numbers.

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
actually stored. Fixture setup and graph verification are outside the measured scope. Those failures
are otherwise silent and would produce a confident, wrong number.

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
