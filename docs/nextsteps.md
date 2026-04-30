## Implementation Tracking

> **State column legend:** empty = available to be taken · `S` = Started (claimed by Ralph or a human, in progress) · `F` = Finished (100% done and reviewed). Ralph picks the **topmost row whose State is empty** and whose dependencies (per Notes) are all `F`.

| Priority | State | Task Name | Description | % Done | Reviewed | Plan File | Notes |
|---|---|---|---|---|---|---|---|
| 1 – HIGH | F | Package Rename | Rename all packages AgentMemory.* → AgentMemory.* | 100% | ✅ Deckard | rename-plan.md | Executed by Roy (acef3ef). All 8 review gates passed. Approved by Deckard 2026-04-30. |
| 2 – HIGH | F | DELETE_SESSION_DATA Gap | Extend DeleteSessionAsync to delete Conversation + ReasoningTrace nodes | 100% | ✅ Deckard | delete-session-gap-plan.md | PR: https://github.com/joslat/agent-memory-dotnet/pull/1 |
| 3 – HIGH |  | Aspire Demo | .NET Aspire AppHost + Neo4j + seeded DB + agent client demo app | 0% | — | — | Depends on Package Rename (#1) |
| 4 – HIGH |  | NuGet Release Prep | CHANGELOG, CONTRIBUTING, semver, .csproj metadata, CI publish workflow | 0% | — | — | Depends on #1 and #3 |
| 5 – MED |  | Streaming Extraction | IStreamingExtractionPipeline with chunk/overlap/deduplication | 0% | — | — | — |
| 6 – MED |  | CLI Tool | `dotnet tool` with `migrate` + `schema-check` commands | 0% | — | — | v1 scope only |
| 7 – MED |  | GDS Support | Optional AgentMemory.Analytics package, GDS PageRank + community detection | 0% | — | — | Depends on #3 (Aspire Demo validates GDS optionality) |
| 8 – MED |  | BenchmarkDotNet Harness | Perf benchmarks for batch ops, vector search, decay, hybrid retrieval | 0% | — | — | — |
| 9 – MED |  | S9 Truncation Refactor | Extract truncation strategies from MemoryContextAssembler | 0% | — | — | Low priority architectural cleanup |

# Agent Memory for .NET — Next Steps

**Author:** Deckard (Lead / Solution Architect)  
**Date:** 2026-04-30  
**Status:** Active forward-looking document. Supersedes prior planning docs (archived under `docs/archive/`).

---

## Proposal Priority Matrix

Scored as of 2026-04-30. **Cost/Effort** and **Value** are 1–10 (1 = trivial/marginal, 10 = months/game-changing). **Priority** = Value ÷ Cost: HIGH > 1.5, MED 0.8–1.5, LOW < 0.8. Sorted HIGH → MED → LOW, then by Value descending within tier.

| # | Proposal | Pros | Cons | Cost/Effort | Value | Priority |
|---|----------|------|------|-------------|-------|----------|
| 1 | **Package Rename (AgentMemory.* → AgentMemory.*)** — Rename all 11 packages, all C# namespaces, all class prefixes carrying `Neo4j` as the root. New root namespace: `AgentMemory.*`. Neo4j stays only as an adapter qualifier (`AgentMemory.Neo4j`, `AgentMemory.Extraction.AzureLanguage`, etc.). Affects all .csproj files, all `using` statements, all namespace declarations, all public type names, all documentation. Must complete before anything is published or demoed externally — NuGet IDs are permanent. | Removes implied Neo4j endorsement; avoids trademark confusion with `Neo4j.Driver` (official); correct package naming convention (product first, adapter second); reversible before publish, irreversible after | Wide mechanical change (~11 packages, all source files, all docs); high PR diff noise; any external forks/refs break (none exist yet — pre-v1) | 3 | 9 | HIGH (9/3 = 3.0 — highest ratio) |
| 2 | **DELETE_SESSION_DATA Gap Closure** — extend `ConversationRepository.DeleteSessionAsync` to also delete Conversation + ReasoningTrace nodes | Closes only remaining genuine parity gap; prevents user surprise migrating from Python; fix is trivially small | Cascading delete risk if Cypher scope is too broad; delete semantics must be documented to avoid audit-trail surprises | 1 | 5 | HIGH |
| 3 | **Aspire Demo Application** — `.NET Aspire AppHost` wiring a Neo4j container (ports 7474/7687), a seeded database, and an agent client console app. Either scripted interaction mode or `--interactive` open chat. Users can inspect the memory graph in Neo4j Browser at `http://localhost:7474`. | Makes the library tangible to evaluating developers; self-contained runnable demo with real Neo4j; showcases MAF + SK integration in one place; Neo4j Browser (port 7474) gives free graph visualization | Requires Docker + .NET Aspire tooling; not strictly a library feature; needs ongoing maintenance as APIs evolve | 4 | 9 | HIGH |
| 4 | **NuGet Release Preparation** — CHANGELOG, CONTRIBUTING, semantic versions, .csproj metadata, GitHub Actions publish workflow | Unlocks community discovery and real-world feedback; CI publish removes manual friction permanently | SemVer stability commitment from v1.0; poor package metadata is permanent; attracts support burden | 2 | 9 | HIGH |
| 5 | **Streaming Extraction** — `IStreamingExtractionPipeline` in Abstractions + Core impl with chunk/overlap/deduplication | Closes highest-value functional gap; eliminates caller burden for long docs; matches Python production behaviour | Cross-chunk entity merge is semantically hard; chunk-boundary artefacts can degrade quality; new public API surface locked under SemVer | 5 | 7 | MED |
| 6 | **CLI Tool (`dotnet tool`)** — `migrate` and `schema-check` commands only. Scoped to v1: `migrate` + `schema-check` commands only. Richer inspection/export features in backlog. | Production ops need a CLI to run migrations; complements `MigrationRunner`; standard `dotnet tool` distribution | Narrow scope at v1; richer features deferred to backlog | 2 | 4 | MED |
| 7 | **GDS Support (optional analytics package)** — New optional package `AgentMemory.Analytics` wrapping Neo4j Graph Data Science (GDS) procedures. Requires GDS plugin installed in Neo4j (separate install, not bundled with Community Edition). Provides: `AddGdsMemoryAnalytics()` DI extension; `MemoryPageRankService` (surfaces highly-connected memories); `MemoryCommunityService` (Louvain topic clustering with `communityId` tag). Graceful degradation: if GDS not installed, extension is a no-op and retrieval falls back to standard scoring. | PageRank and community detection improve memory context quality with zero schema changes; opt-in means zero impact for users without GDS; GDS is Cypher-callable so no new driver needed; real quality uplift for power users | Requires separate GDS plugin install (extra ops step); GDS is NOT bundled with Neo4j Community Edition; adds a new NuGet package to maintain; first version may need tuning for memory-specific graph shapes | 3 | 5 | MED (5/3 = 1.67) |
| 8 | **BenchmarkDotNet Harness** — batch UNWIND, vector search, decay pruning, hybrid retrieval benchmarks | Backs infrastructure depth claims with numbers; catches perf regressions in CI | Hardware-sensitive — dev numbers ≠ production; CI infrastructure complexity for Neo4j benchmarks; stale results worse than none | 3 | 4 | MED |
| 9 | **S9 — Extract Truncation Strategies from `MemoryContextAssembler`** | Cleaner architecture; easier extension of context assembly; low risk refactor | Minimal user-visible benefit; no active pain point | 2 | 3 | MED |

> **Note:** §4 (Recommended Next Sequence) applies strategic execution dependencies, not just raw score. Package Rename is #1 because NuGet IDs are permanent — publishing with incorrect package names is an irreversible mistake. DELETE_SESSION_DATA is #2 because it is trivial and can be done in the same sprint as the rename review. Aspire Demo is #3 to validate the renamed library end-to-end before release. NuGet Release Prep is deliberately #4 — it is gated on both the rename being complete and the demo giving a green light. The matrix scores individual proposals in isolation; §4 explains the sequencing rationale.

> **Deferred to `docs/Improvement-Ideas-Backlog.md`:** Memory Conflict Detection + Provenance Scoring, Cross-Agent Memory Sharing, Local Embedding Adapter (ONNX), Local NLP Extractors, AutoGen.NET/LangChain.NET integrations, Opik observability, and full CLI tool feature set. These are well-reasoned future investments — see the backlog for expanded descriptions and implementation sketches.

---

## 1. Where We Are

All six implementation phases are complete. The gap-closure sprint (Waves A–C) brought functional parity with the Python reference to ~99%. The documentation has been reorganised and corrected. The solution ships eleven source packages (including a convenience meta-package), an extensive test suite, and a fully functional MCP server.

Concretely:

- **Packages:** `AgentMemory.Abstractions`, `.Core`, `.Neo4j`, `.AgentFramework`, `.Extraction.Llm`, `.Extraction.AzureLanguage`, `.Enrichment`, `.Observability`, `.McpServer`, `.SemanticKernel`, plus the `AgentMemory` meta-package.
- **Architecture:** Strict ports-and-adapters layering, zero boundary violations, zero circular dependencies.
- **Persistence:** Native Neo4j `datetime()` for all timestamps, 145+ centralised Cypher constants, MigrationRunner with versioned `.cypher` files.
- **Search:** Vector (5 indexes + reasoning-step index), fulltext BM25 (3 indexes), hybrid (vector + BM25), and graph multi-hop traversal.
- **Memory features:** Temporal point-in-time recall (`RecallAsOfAsync`), exponential memory decay (`MemoryDecayService`), multi-extractor merge strategies (five modes), batch UNWIND upserts.
- **Agent integrations:** Microsoft Agent Framework, Semantic Kernel, MCP server (21 tools, 6 resources, 3 prompts).
- **Observability:** OpenTelemetry ActivitySource + Meter, instrumented decorators for all extraction and enrichment services.

What is **not** done yet: NuGet release artifacts (CHANGELOG, CONTRIBUTING, package versioning), streaming extraction for long documents, an Aspire demo application for developer onboarding, and an optional GDS analytics package. Package rename is complete (merged 2026-04-30).

---

## 2. .NET vs Python: The Honest Assessment

**Overall verdict: Mixed — infrastructurally ahead, ecosystem-breadth behind.**

This is not a single-dimension result. The two codebases are competitive on different axes. Understanding which axis matters more for a given use case determines how the comparison should be read.

### 2a. Where .NET is Better

| Area | What .NET does that Python does not |
|------|-------------------------------------|
| **Memory decay** | Exponential scoring (`confidence × exp(−λ×days) + boost×access`) with `MemoryDecayService`. Python has no equivalent. |
| **Point-in-time recall** | `RecallAsOfAsync` with `TemporalQueries` — retrieve the exact memory state at any historical moment. Python has no equivalent. |
| **Fulltext / BM25 search** | Three fulltext indexes (message content, entity name, fact content) with `FulltextRetriever`. Python has no fulltext indexing. |
| **Hybrid retrieval** | `HybridRetriever` combines vector and BM25 scores in a single ranked result. Python has vector only. |
| **GraphRAG multi-hop expansion** | `RELATED_TO*1..2` traversal in `Neo4jGraphRagContextSource`. Python has no graph expansion. |
| **Batch upsert** | UNWIND-based bulk entity and fact upsert. Python has no batch API. |
| **Migration versioning** | `MigrationRunner` with versioned `.cypher` files tracks schema evolution. Python has no migration system. |
| **Multi-extractor merge strategies** | Five modes (Union, Intersection, Confidence, Cascade, FirstSuccess). Python is single-extractor only. |
| **Schema richness** | 14 extra node properties, three extra relationship types, three fulltext indexes, one extra vector index beyond Python's schema. |
| **Architecture rigour** | Strict ports-and-adapters across eleven packages, verified zero-violation dependency graph. Python is monolithic (one package, fifteen modules, no enforced boundaries). |
| **Cypher centralisation** | 145 typed constants in thirteen domain files. Python has 99 in a single `queries.py`. |

### 2b. Where .NET is at Parity

These dimensions are functionally equivalent, even if the implementation language and style differ:

- Core domain model (all nine node types, full relationship model)
- Vector similarity search across all memory layers
- Entity resolution chain (Exact → Fuzzy → Semantic → CreateNew)
- LLM-based extraction (entity, fact, preference, relationship)
- MCP server (both serve the same protocol; .NET ships more documented tools)
- Wikipedia/Wikimedia entity enrichment
- Nominatim geocoding
- OpenTelemetry observability
- Microsoft Agent Framework integration
- Semantic Kernel integration (Python has no SK equivalent — .NET is actually ahead here)

### 2c. Where .NET is Behind

| Area | The gap | Why it exists |
|------|---------|---------------|
| **Framework ecosystem breadth** | Python: nine integrations (CrewAI, LangChain, LlamaIndex, Pydantic AI, Google ADK, OpenAI Agents, Strands, AgentCore, Microsoft Agents). .NET: three (MAF, SK, MCP). | The Python AI agent ecosystem is larger. .NET has fewer competing frameworks, but AutoGen and LangChain.NET exist and are not yet targeted. |
| **Local NLP extractors** | Python ships GLiNER (zero-shot NER) and spaCy. .NET requires a cloud API (Azure Text Analytics) or a hosted LLM. | No .NET-native GLiNER binding exists. ONNX Runtime with a fine-tuned NER model is the viable path but has not been built. |
| **Streaming extraction** | Python has `streaming.py`: chunked processing, overlap handling, async generators, cross-chunk deduplication. .NET processes each input in one call. | Not in the original spec. Added to Python as a production quality-of-life feature for long documents. |
| **Local embedding adapter** | Python ships four concrete embedding backends including sentence-transformers (fully local). .NET uses the MEAI `IEmbeddingGenerator<string, Embedding<float>>` abstraction — the right design — but ships no concrete local adapter. | MEAI is the correct abstraction. A sentence-transformers adapter via ONNX or Semantic Kernel's local executor has not been built. |
| **CLI tool** | Python ships a `neo4j-agent-memory` CLI (click-based, ~800 lines) for extract, schema list, and stats. .NET has the McpHost sample app only. | Out of scope for the enterprise .NET target market; a `dotnet tool` could fill this if needed. |
| **GDS integration** | Python's `gds.py` adds Neo4j Graph Data Science (PageRank, community detection). .NET has no GDS integration. | Not in the original spec. GDS is powerful but optional. |
| **Opik observability** | Python ships LLM-focused tracing (hallucination detection, token tracking, feedback scores) via Opik. .NET has OTel only. | No .NET Opik SDK exists. The OTel layer covers the infrastructure observability need. |
| **Benchmarks** | Python has three benchmark files. .NET has none. | Has never been in sprint scope. |

### 2d. One-Line Summary

> .NET wins on infrastructure depth (decay, temporal recall, fulltext, hybrid, migrations, GraphRAG, architecture rigour). Python wins on ecosystem breadth (nine framework integrations, local NLP, streaming, local embeddings, CLI, GDS, Opik). For enterprise .NET deployments against Neo4j, the .NET implementation is the stronger foundation.

---

## 3. Remaining Open Items from Prior Docs

The following items from `improvement-suggestions.md` and `parity-assessment.md` remain relevant after the cleanup:

| Item | Source | Priority | Notes |
|------|---------|----------|-------|
| `DELETE_SESSION_DATA` partial gap (#51) | parity-assessment §2.4 | Low | .NET deletes session messages; Python also deletes conversations + traces. One Cypher change in `ConversationRepository`. |
| S7: Split `IEntityRepository` (ISP) | improvement-suggestions §4 | Not recommended | Interface has grown to 17+ methods but remains cohesive — all methods operate on Entity domain objects. Breaking change with minimal benefit. Leave as-is. |
| S9: Extract truncation strategies from `MemoryContextAssembler` | improvement-suggestions §4 | Deferred | Low risk, medium value. Worth picking up when `MemoryContextAssembler` needs extension. |
| Creative ideas C1–C9 | improvement-suggestions §5 | Future | Memory provenance chains (C1), conflict detection (C2), self-improving memory (C3) are high-novelty features. Not urgent but worth revisiting post-release. |

---

## 4. Recommended Next Sequence

Priority is ordered by strategic execution dependencies. Publishing with incorrect package names is an irreversible mistake; that constraint drives the ordering more than raw scores do.

**Ordering logic in one paragraph:** Step 1 (Package Rename) has the highest value/cost ratio in the matrix and a one-way door — NuGet IDs are permanent. It must precede any external demo or release. Step 2 (DELETE_SESSION_DATA) is trivial and can run in the same sprint as the rename review with no dependency. Step 3 (Aspire Demo) validates that the renamed library works end-to-end and is the "wow effect" signal that the library is ready to ship. Step 4 (NuGet Release Prep) is deliberately gated on demo success — only after the demo gives a green light should we publish. Step 5 (Streaming Extraction) is the first post-release functional feature. Steps 6 and 7 (CLI Tool + GDS Support) are parallel additive work; neither blocks the other or any release.

### Step 1 — Package Rename (AgentMemory.* → AgentMemory.*)

**What:** Rename all 11 packages, all C# namespaces, all class prefixes carrying `Neo4j` as the root. New root namespace: `AgentMemory.*`. Neo4j stays only as an adapter qualifier: `AgentMemory.Neo4j`, `AgentMemory.Extraction.AzureLanguage`, etc. Affects all `.csproj` files, all `using` statements, all namespace declarations, all public type names, all documentation.

**Why first:** NuGet package IDs are permanent once published. Publishing under `AgentMemory.*` creates trademark ambiguity with `Neo4j.Driver` (the official Neo4j .NET driver) and implies Neo4j endorsement this project does not have. The rename is mechanical and wide, but the window to do it cleanly is now — pre-v1, before any external demo uses the wrong names, before any fork or NuGet consumer exists.

**Benefit:** Correct public package names (product first, adapter second) from day one. No trademark confusion. No retroactive breaking change after publish. Demo uses the final public API surface.

**Cons / Tradeoffs:**
- Wide mechanical change: ~11 packages, all source files, all docs. High PR diff noise.
- Any external forks or references break — but none exist yet (pre-v1).

**Effort:** Medium (3). Roy + Holden validate tests still pass after rename.

---

### Step 2 — `DELETE_SESSION_DATA` Gap Closure

**What:** Extend `ConversationRepository.DeleteSessionAsync` (or equivalent) to also delete associated Conversation nodes and ReasoningTrace nodes, matching Python's `DELETE_SESSION_DATA` semantics.

**Why second:** Trivial (Cost 1). Can be done in the same sprint as the rename review — no dependencies between the two. Worth closing before any release to prevent surprises for users migrating from the Python implementation.

**Benefit:** Users migrating from Python will not encounter subtle data-retention differences. The session deletion contract becomes complete and predictable.

**Cons / Tradeoffs:**
- Deleting Conversation and ReasoningTrace nodes **may be destructive in unexpected ways** if the caller expects them to survive session deletion for audit/replay purposes. The deletion semantics should be clearly documented.
- A careless Cypher query could cascade deletes too broadly. The query must be scoped carefully to avoid cross-session data loss.

**Effort:** Very low (1). One Cypher query change, one repository update, one test.

---

### Step 3 — Aspire Demo Application

**What:** `.NET Aspire AppHost` wiring a Neo4j container (ports 7474/7687), a seeded database, and an agent client console app. Either scripted interaction mode or `--interactive` open chat. Users can inspect the memory graph in Neo4j Browser at `http://localhost:7474`.

**Why third:** Validates that the renamed library works end-to-end. This is the "wow effect" signal — a self-contained runnable demo is the fastest path to adoption and removes all setup friction for first-time users. It also gates the NuGet release: if the demo reveals issues, they must be fixed before publishing.

**Benefit:** Proves renamed packages function correctly. Reduces time-to-first-run for evaluators. Showcases MAF + SK integration together. Neo4j Browser gives immediate graph visualisation at no extra tooling cost.

**Cons / Tradeoffs:**
- Requires Docker + .NET Aspire tooling — adds infrastructure prerequisites.
- Not strictly a library feature; needs ongoing maintenance as APIs evolve.

**Effort:** Medium (4). New AppHost project, seeding script, console agent client.

---

### Step 4 — NuGet Release Preparation

**What:** Create `CHANGELOG.md`, `CONTRIBUTING.md`, assign initial semantic versions to all packages, verify package metadata (`.csproj` `PackageId`, `Description`, `Authors`, `RepositoryUrl`, `PackageTags`), produce a release CI workflow (GitHub Actions → NuGet.org push).

**Why fourth:** Only valid after the rename is complete (package IDs are permanent) and after the demo confirms the library is shippable. Publishing before either of those conditions is met would be an irreversible mistake. Once gated conditions are met, this is the highest-leverage unlock — it makes the library discoverable and generates real feedback.

**Benefit:** Library becomes publicly installable and discoverable. External feedback on API design becomes possible. CI publish workflow removes manual release friction permanently.

**Cons / Tradeoffs:**
- Publishing a v1.0 creates a **SemVer stability commitment** — breaking changes require a v2.x bump and a deprecation cycle.
- Metadata quality matters permanently: a poorly chosen `PackageId` or mismatched tags will be visible to all NuGet users.
- CI secrets (NuGet API key) must be managed.

**Effort:** Low (2). No code changes required; purely release scaffolding.

---

### Step 5 — Streaming Extraction

**What:** Add `IStreamingExtractionPipeline` to Abstractions with chunked input support. Implement in Core with configurable chunk size, overlap tokens, and cross-chunk entity deduplication. Python's `streaming.py` is a clear reference for the design.

**Why fifth:** First post-release functional feature. This is the most significant functional gap for production use — long documents currently require the caller to chunk manually. Can be developed in parallel with or immediately after the NuGet release.

**Benefit:** Long-document extraction becomes a first-class concern with no burden on the caller. Cross-chunk entity deduplication prevents duplicate graph nodes. Matches Python's production behaviour on real workloads.

**Cons / Tradeoffs:**
- Cross-chunk entity merge is **semantically hard**: two mentions of the same entity in adjacent chunks may differ in phrasing.
- The new `IStreamingExtractionPipeline` interface expands the public API surface under SemVer.

**Effort:** Medium (5). New interface + Core implementation + tests. No breaking changes to existing contracts.

---

### Step 6 — CLI Tool (scoped v1: `migrate` + `schema-check`) + GDS Support (parallel)

These two items are additive and independent. Neither blocks the other or any release. They can be worked in parallel by different engineers.

**CLI Tool:** `dotnet tool` providing `migrate` (runs pending schema migrations via `MigrationRunner`) and `schema-check` (validates current schema against expected baseline). Effort: Low (2).

**GDS Support:** Optional package `AgentMemory.Analytics` wrapping Neo4j Graph Data Science procedures. `MemoryPageRankService` and `MemoryCommunityService`. Graceful degradation if GDS not installed. Depends on Aspire demo success. Does not block v1 release. Effort: Medium (3).

---

### Deferred / Future (post-launch backlog)

See `docs/Improvement-Ideas-Backlog.md` for expanded descriptions and implementation sketches. Notable deferred items:

- **Memory conflict detection (C2) and provenance reliability scoring (C1):** High-novelty features worth building eventually, but non-trivial and best scheduled after the library has real-world users providing feedback.
- **BenchmarkDotNet Harness:** Backs architectural performance claims. Useful before publicising perf results but not blocking release.
- **Richer CLI tool features:** Inspect, export, stats commands. Deferred beyond v1 scope.

---

## 5. What Not to Do

- **Do not break existing package contracts** for any of the above. Every item above is additive — new interfaces, new packages, new optional parameters. Existing consumers of `v1.x` should not be broken by any of these.
- **Do not hard-code test counts or tool counts in new docs.** They drift. Use durable wording (see decision D-DOC-2).
- **Do not create a separate `GraphRagAdapter` package again.** That concern correctly lives inside `AgentMemory.Neo4j`. The merge was the right decision.
- **Do not invert dependency direction.** If an integration package needs to reference a framework SDK, it references Abstractions (and optionally Core), not Neo4j or any other adapter.

---

## 6. Document Relationships

| Document | Role | Status |
|----------|------|--------|
| `Agent-Memory-for-DotNet-Specification.md` | Canonical functional specification | Active — source of truth for any ambiguity |
| `Agent-memory-for-dotnet-implementation-plan.md` | Historical phased build guide | Historical (all phases complete; banner added) |
| `docs/architecture.md` | Package topology, graph model, boundaries | Active |
| `docs/design.md` | Domain model, context assembly, extraction pipeline | Active |
| `docs/schema.md` | Neo4j schema reference | Active |
| `docs/parity-assessment.md` | Python vs .NET parity analysis | Active (post gap-closure sprint) |
| `docs/implementation-status.md` | Phase completion tracker | Active (all phases done) |
| `docs/improvement-suggestions.md` | Architecture audit and improvement backlog | Active (most items complete; S7 not recommended, S9 deferred) |
| `docs/package-strategy.md` | NuGet packaging options analysis | Active (Option C chosen) |
| `docs/nextsteps.md` | **This document** — forward-looking priorities | Active |
| `docs/archive/` | Completed planning and analysis documents | Read-only history |
| `docs/reference/` | External reference material | Read-only reference |
