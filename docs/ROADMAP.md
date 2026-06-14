# Agent Memory for .NET — Roadmap & Status

> **The single overarching plan and current status.** For granular per-task tracking see
> [`nextsteps.md`](nextsteps.md); for the detailed historical implementation plan see
> [`Memory_Review_and_Implementation_Plan.md`](Memory_Review_and_Implementation_Plan.md); for completed
> review records see [`reviews/`](reviews/). Last updated: **2026-06-14**.

---

## Status at a glance

| | |
|---|---|
| **Version** | `0.1.0-preview.3` — published to NuGet 2026-06-14 (12 packages: `AgentMemory` + `AgentMemory.*`) |
| **Maturity** | Feature-complete; in public preview, stabilizing toward `1.0` |
| **Tests** | 2476 unit + Semantic Kernel + live-Neo4j integration — all green; CI (build-test) on every PR |
| **Hardening** | 6 adversarial review cycles + a cross-cutting capstone, all merged (see [`reviews/`](reviews/)) |
| **Open work** | One half-done item (`schema-check` CLI) + three intentionally-deferred items — see [Pending](#pending-work) |

**What it is:** a native .NET 9 implementation of graph-native persistent memory for AI agents, backed by
Neo4j, with GraphRAG interop and first-class adapters for the Microsoft Agent Framework, Semantic Kernel,
and the Model Context Protocol. It is the .NET counterpart to the Python `neo4j-labs/agent-memory`, with a
documented superset schema.

---

## Shipped capabilities ✅

| Area | What's delivered |
|------|------------------|
| **Three-tier memory** | Short-term (conversations/messages), long-term (entities/facts/preferences/relationships), reasoning (traces/steps/tool-calls) |
| **Extraction pipeline** | `ExtractionStage` → `PersistenceStage`; LLM extractors (`Extraction.Llm`) + Azure AI Language (`Extraction.AzureLanguage`); entity resolution chain (Exact → Fuzzy → Semantic → CreateNew); streaming/chunked extraction |
| **Bitemporal + decay (D1–D7)** | Recency re-ranker, structural hop-decay (`γ^hops`), query-intent presets, non-destructive decay-by-default, `invalidated_at` transaction clock, two-clock `RecallAsOfAsync(validAsOf, systemAsOf)`, contradiction→supersession (no `DETACH DELETE`) |
| **Multi-tenant isolation** | **R1** owner_id + `MemoryScope` (optional shared) across all recall/CRUD/GraphRAG/trace paths; **R1b** per-application store tier (`SharedDatabase` default, opt-in `DatabasePerApplication` with auto-provisioning); **R2** owner-scoped list reads; `BeginOwnerScope` host helper |
| **GraphRAG retrieval** | Vector, Fulltext (BM25, Lucene-escaped), Hybrid (scale-free Reciprocal Rank Fusion), and multi-hop Graph traversal |
| **Analytics (optional)** | `AgentMemory.Analytics` — GDS PageRank + Louvain community detection over an owner-scoped projection; graceful no-op without the GDS plugin |
| **Adapters** | **MAF** (context + chat-history providers, `MemoryToolFactory`, facade, trace recorder); **SK** (`Neo4jMemoryPlugin` + text search); **MCP** (25 tools, 6 resources, 3 prompts; stdio + HTTP) |
| **CLI** | `agentmemory`: `migrate`, `bootstrap`, `consolidate`, `decay`, `conflicts`, `schema-parity`, `invalidate`, `supersede` |
| **Cross-cutting** | Observability (OpenTelemetry decorators), Enrichment (Nominatim geocoding + Wikimedia/Diffbot, rate-limited + cached), schema-parity compatibility kit, consolidation/hygiene, conflict detection |

---

## Quality & hardening

The library has been adversarially reviewed end-to-end — **vertically** (per-area) and **horizontally**
(cross-cutting). The candidate→confirmed trend converged cleanly to zero, signalling a solid surface:

| Review | Scope | Confirmed | Record |
|---|---|---|---|
| Cycle 3 | core / extraction / adapters | 6 | [`reviews/review-2026-06-13-cycle3.md`](reviews/review-2026-06-13-cycle3.md) |
| Cycle 4 | CLI / SK / Observability | 6 | [`reviews/review-2026-06-13-cycle4.md`](reviews/review-2026-06-13-cycle4.md) |
| Cycle 5 | GraphRAG / MCP / assembler | 6 | [`reviews/review-2026-06-13-cycle5.md`](reviews/review-2026-06-13-cycle5.md) |
| Cycle 6 | Enrichment / samples | 4 | [`reviews/review-2026-06-13-cycle6.md`](reviews/review-2026-06-13-cycle6.md) |
| Capstone | concurrency / lifetime / async (whole tree) | 0 | [`reviews/review-2026-06-13-capstone.md`](reviews/review-2026-06-13-capstone.md) |

---

## Pending work

Nothing here is a bug or regression — it is scoped/deferred work, tracked accurately in
[`nextsteps.md`](nextsteps.md).

| Item | State | Notes |
|------|-------|-------|
| **`schema-check` CLI command** | 🟡 half-done / next | The v1 CLI spec called for `migrate` + `schema-check`. The CLI shipped 8 commands but **not** `schema-check` — a *runtime* check that the live database's indexes/constraints match what the bootstrapper creates. (`schema-parity` is a different, **static** upstream-compatibility check.) Small (~half day): open a session, `SHOW INDEXES`/`SHOW CONSTRAINTS`, assert conformance, exit 0/1. |
| **Schema-node CRUD repository (G4)** | ⏸️ deferred (P2) | Persisting custom entity schemas as `:Schema` nodes. .NET uses fixed schema types instead; a conscious omission (see [`schema.md`](schema.md)). |
| **BenchmarkDotNet harness** | ⏸️ deferred (post-v1) | Perf benchmarks (batch upsert, vector search, decay, hybrid). Hardware-sensitive; intentionally out of CI gating. |
| **S9 — truncation-strategy refactor** | ⏸️ deferred | Extract `ITruncationStrategy` out of `MemoryContextAssembler`. Pure cleanup; truncation works — no active pain point. |

---

## Roadmap to `1.0`

1. **`schema-check` CLI** *(in progress)* — close the one half-done v1 item.
2. **Real-world feedback on `0.1.0-preview.3`** — validate the install/usage path; iterate on ergonomics.
3. **Deferred items as demand warrants** — pick up G4 / benchmarks / S9 only if a concrete need surfaces.
4. **API stabilization → `1.0`** — lock the public surface under SemVer once the preview has soaked.

---

## Where the docs live

| Folder / file | Purpose |
|---|---|
| [`getting-started.md`](getting-started.md) | Install, configure, first memory store, multi-tenant setup |
| [`architecture.md`](architecture.md) · [`design.md`](design.md) · [`schema.md`](schema.md) | Current reference — layers/boundaries, type/interface catalogs, Neo4j graph model |
| [`specification.md`](specification.md) | Consolidated baseline specification |
| [`nextsteps.md`](nextsteps.md) · [`Improvement-Ideas-Backlog.md`](Improvement-Ideas-Backlog.md) | Granular task tracking · deferred-ideas backlog |
| [`Memory_Review_and_Implementation_Plan.md`](Memory_Review_and_Implementation_Plan.md) | The detailed historical implementation plan (isolation deep-dive); kept as deep reference |
| [`reviews/`](reviews/) | Completed adversarial-review records (point-in-time) |
| [`reference/`](reference/) | Upstream / parity reference (Python schema snapshots, PR how-to, MAF migration guides) |
| [`archive/`](archive/) | Superseded plans and now-implemented design discussions (read-only history) |
