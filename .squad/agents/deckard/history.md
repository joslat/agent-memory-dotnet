# Deckard — History

## Project Context
- **Project:** Agent Memory for .NET — native .NET Neo4j Memory Provider for AI agents
- **User:** Jose Luis Latorre Millas
- **Stack:** .NET 9, C#, Neo4j, Microsoft Agent Framework, GraphRAG
- **Architecture:** Layered ports-and-adapters (Abstractions → Core → Neo4j → Adapters)
- **Spec:** Agent-Memory-for-DotNet-Specification.md
- **Plan:** Agent-memory-for-dotnet-implementation-plan.md

## Learnings

### 2025-01-28 — Phase 1 Onboarding Analysis

**Key Architecture Decisions Identified:**
1. Framework-agnostic core: Memory engine must not depend on MAF types
2. Adapters on top: MAF and GraphRAG are separate adapter layers
3. GraphRAG is required but separate: Not the memory provider itself, but required for interoperability
4. No Python runtime: Use LLM-based structured extraction first
5. Ports-and-adapters: Strict layering with dependency inversion
6. Neo4j persistence: Direct driver usage, no ORM
7. MCP excluded from Phase 1: External access layer built later

**Package Boundaries (Phase 1):**
- `Neo4j.AgentMemory.Abstractions` — domain contracts only, zero external dependencies
- `Neo4j.AgentMemory.Core` — orchestration and domain logic, depends only on Abstractions
- `Neo4j.AgentMemory.Neo4j` — repositories and Cypher, depends on Abstractions + Core + Neo4j.Driver

**Critical Patterns:**
- Testcontainers for Neo4j integration tests
- Contracts-first design to enforce dependency direction
- Stubbing strategy: `IEmbeddingProvider` and `IExtractionService` stubbed in Phase 1
- Schema bootstrap and migration runner as first-class concerns
- Three distinct memory layers with separate repositories

**Risks Identified:**
1. Embedding provider integration complexity (deferred to Phase 2)
2. Extraction pipeline complexity (deferred to Phase 2)
3. Graph schema evolution and migration strategy
4. Context assembly token budget enforcement
5. Transaction boundary definition across memory operations

**Dependencies:**
- Neo4j.Driver (official .NET driver)
- xUnit, FluentAssertions (testing)
- Testcontainers for .NET
- Docker + Neo4j image

**Bootstrap Order:**
1. Abstractions → contracts
2. Neo4j infrastructure → driver factory, schema installer, transaction runner
3. Short-term → messages, conversations
4. Long-term → entities, facts, preferences, relationships
5. Reasoning → traces, steps, tool calls
6. Context assembler → memory recall and assembly

**Phase 1 Exit Criteria:**
- All repositories implemented with Neo4j persistence
- All services unit tested
- All repositories integration tested with real Neo4j
- Context assembler functional with configurable budgets
- No MAF or GraphRAG dependencies in Core or Abstractions
- Schema bootstrap creates all constraints and indexes
- Docker Compose harness functional

### 2025-01-28 — Formal Architecture Review (Gate)

**Trigger:** Jose requested formal review before Phase 1 continuation. Five explicit directives received.

**Review Outcome:** APPROVED WITH FINDINGS — 10/10 alignment checklist passes.

**Key Findings:**
1. **Package boundaries verified clean.** Abstractions: zero deps. Core: Abstractions + M.E.* only. Neo4j: Abstractions + Core + Neo4j.Driver + M.E.* only.
2. **No MAF/GraphRAG leakage.** Grep confirms zero `Microsoft.Agents.*` or `Microsoft.Extensions.AI` references in `src/`.
3. **No speculative features.** Every type traces to a spec section.
4. **GraphRag domain types correctly placed in Abstractions** — pure contracts, zero SDK imports. DI principle correctly applied.
5. **Roy's domain design (v1) is spec-compliant.** All 5 open questions answered with architectural decisions.

**neo4j-maf-provider Analysis:**
- Retriever layer (`IRetriever`, `VectorRetriever`, `FulltextRetriever`, `HybridRetriever`) is well-designed and production-quality.
- Cypher patterns (`db.index.vector.queryNodes`, `db.index.fulltext.queryNodes`) are the primary reuse target.
- **Decision: ADAPT patterns, don't fork or wrap.** Copy Cypher query structures into our typed repositories. The existing package remains separate — referenced only by the future GraphRAG adapter.
- `Neo4jContextProvider : AIContextProvider` is MAF-specific — NOT reusable in core. Built for MAF 0.3; MAF is now 1.1.0.

**Roy's Open Questions — Decisions Made:**
1. No `SessionRepository` in Phase 1 — sessions are implicit via SessionId.
2. No embedding dimension validation in interfaces — implementation concern.
3. Batch limits are implementation-specific, not in Abstractions.
4. No `IMemoryMergeService` — `IEntityResolver` covers Phase 1 needs.
5. GraphRAG types stay in Abstractions — already correct per D6.7.

**ADR-7 Published:** Package dependency graph, boundary enforcement rules, adapter strategy, test strategy, Phase 1 scope, deferred features.

**Standing Directives (Jose, reinforced):**
- MAF stays separate adapter — no MAF types in core packages, ever.
- GraphRAG stays separate adapter — no GraphRAG SDK in core packages, ever.
- Tests first (TDD) — every repo/service gets tests before or during implementation.
- No feature creep — only what the spec requires.
- neo4j-maf-provider is reference material, not a dependency for core.

### 2025-07-12 — Consolidated Architecture Documentation

**Trigger:** Jose requested proper project-level documentation that consolidates scattered Squad-internal architecture knowledge into shareable, spec-traceable documents.

**Deliverables Created:**
1. **`docs/architecture.md`** — Architecture overview covering vision, layered package diagram, package responsibilities with exact dependencies, Neo4j graph model (nodes, relationships, constraints, indexes), boundary enforcement rules (B1–B8), relationship to neo4j-maf-provider (adapt-not-fork strategy), test strategy, and phase roadmap with current status.
2. **`docs/design.md`** — Software design document covering the full domain model (31 types across 3 memory layers), context assembly flow, extraction pipeline design, service interface catalog (15 interfaces), repository interface catalog (10 interfaces), configuration model hierarchy, and design decision rationale.
3. **`docs/neo4j-maf-provider-analysis.md`** — Dedicated reuse strategy for the existing neo4j-maf-provider codebase: file-by-file code inventory (10 files), specific Cypher patterns to adapt (vector, fulltext, hybrid merge, read routing, parameterization), components we don't reuse (AIContextProvider, RetrieverResult, IEmbeddingGenerator), Phase 4 GraphRAG adapter integration plan, and MAF version gap analysis (0.3 → 1.1.0).

**Verification:**
- All dependency claims verified against actual .csproj files
- All boundary rules verified via grep across src/
- All type counts verified against filesystem
- All Cypher patterns verified against SchemaBootstrapper.cs and existing retriever source
- Documents reference specific spec sections for traceability

### 2025-07-12 — Python agent-memory Reference Analysis

**Trigger:** Jose requested comprehensive analysis mapping the Python `neo4j-labs/agent-memory` reference implementation to our .NET solution.

**Deliverable Created:**
- **`docs/python-agent-memory-analysis.md`** — 13-section reference analysis covering architecture comparison, module-by-module mapping (15 Python modules → .NET equivalents with ADAPT/SKIP/DEFER/REFERENCE strategy), Neo4j graph model comparison (node types, relationships, constraints, indexes), Cypher pattern catalog (60+ queries mapped to .NET repositories), extraction pipeline deep dive (3-stage pipeline, merge strategies, LLM prompts), entity resolution deep dive (4-strategy chain with type-strict filtering), configuration comparison, test strategy comparison, dependency analysis, gain/skip/differ summary table, phase mapping (Phases 1–6), and risk assessment.

**Key Findings:**
1. **CRITICAL GAP:** Our SchemaBootstrapper is missing 5 vector indexes and 9 property indexes that Python creates. Vector indexes are required for embedding search to work.
2. **Entity resolution is more complex than our stub suggests.** Python chains Exact → Fuzzy → Semantic with type-strict filtering and dedup thresholds (0.95 auto-merge, 0.85 flag, below = new entity).
3. **Cross-memory relationships are missing.** Python links traces to messages (`INITIATED_BY`), tool calls to messages (`TRIGGERED_BY`), conversations to traces (`HAS_TRACE`).
4. **Python's centralized query pattern** (`graph/queries.py` with 60+ constants) is worth adopting — our queries are inline in repositories.
5. **Metadata serialization** needed: Python serializes `dict` metadata as JSON strings because Neo4j doesn't support Map properties.
6. **Message linking pattern** (FIRST_MESSAGE + NEXT_MESSAGE) gives O(1) latest-message access vs ORDER BY timestamp.

**Verification:**
- 7/7 source code claims verified against actual .cs and .py files
- All Python module paths verified to exist
- All .NET type references verified against actual source
- Cypher patterns verified against queries.py and schema.py

### 2025-07-13 — Document Alignment Review & Status Tracker

**Trigger:** Jose requested comprehensive document alignment review and a status tracker he can consult.

**Deliverables:**
1. **`docs/implementation-status.md`** — The single status reference document. Contains executive summary, implementation plan reference, Phase 1 epic status table with commit hashes, detailed progress for all 9 epics, document inventory with alignment status, known gaps from Python analysis, phase roadmap, decisions log reference, and build/test instructions.
2. **`docs/architecture.md` updates** — Fixed 3 stale sections:
   - §4.5: Updated from "Vector Indexes (Phase 1 — Pending)" to document the 5 vector indexes now in SchemaBootstrapper with actual Cypher
   - Added §4.6: Property Indexes section documenting 9 property indexes
   - §8: Updated Phase 1 Status Detail table — schema constraints + indexes now marked complete
   - Updated unit test count from 20 to 21

**Alignment Review Findings:**
- **5 staleness issues found** — 3 in architecture.md (fixed), 2 in python-agent-memory-analysis.md (documented but not fixed as it's a point-in-time analysis)
- **0 contradictions** between spec, impl plan, and docs/ files
- **0 spec-level gaps** requiring specification changes
- **1 schema gap identified** — missing `task_embedding_idx` for `ReasoningTrace.taskEmbedding`, needed for `SearchByTaskVectorAsync`
- **8 implementation-level known gaps** documented from Python analysis (entity resolution, cross-memory relationships, metadata serialization, message linking, etc.)

**Key Status Determination:**
- Phase 1 is approximately 50% complete — all foundation done, all implementation pending
- 5 of 9 epics complete (Epics 1, 2, 3, 8, 9)
- 4 epics pending (Epics 4, 5, 6, 7 — the actual repository and service implementations)
- 21 unit tests + 2 integration tests passing, build clean (0 warnings, 0 errors)
- No deviations from spec or impl plan

### 2025-07-13 — Specification & Implementation Plan Living Document Update

**Trigger:** Jose requested that spec and impl plan be updated with findings from Python analysis, architecture docs, and implementation status tracker. "If there is something which is invalid or not up to date in the spec/impl-plan docs, and a detailed analysis clarifies that, then we should update the impl plan and specs to have it become a better source of truth."

**Specification Updates (Agent-Memory-for-DotNet-Specification.md):**
1. **§3.1 Short-term memory** — Added message linking pattern note (FIRST_MESSAGE + NEXT_MESSAGE for O(1) latest-message access)
2. **§3.1 Long-term memory — Fact fields** — Added `Category` optional field (from Python reference, already has property index in SchemaBootstrapper)
3. **§3.1 Long-term memory — behaviors** — Added entity resolution complexity note (4-strategy chain: exact → fuzzy → semantic → type-aware)
4. **§3.3 Storage principles** — Added metadata serialization note (Metadata dictionaries serialized as JSON strings in Neo4j)
5. **§3.4 Provenance principle** — Expanded with cross-memory relationships (INITIATED_BY, TRIGGERED_BY, HAS_TRACE)
6. **§3.5 Neo4j schema requirements** — NEW section documenting all 6 vector indexes, 9 property indexes, and 3 fulltext indexes with tables

**Implementation Plan Updates (Agent-memory-for-dotnet-implementation-plan.md):**
1. **§15 Phase 0** — Marked as COMPLETE with ✅ status on all deliverables and tasks
2. **§15 Phase 1** — Marked as IN PROGRESS (~50%) with ✅/⏳ status indicators on all deliverables and tasks
3. **§9.3 Constraints and indexes** — Complete rewrite: now documents all 27 schema objects (9 constraints + 3 fulltext + 6 vector + 9 property) with exact index names and implementation status
4. **§15 Phase 2** — Added task #9 (entity resolution chain) and entity resolution complexity note documenting 4-strategy chain from Python analysis
5. **Phase 1 Build and test commands** — Added verified commands section (dotnet build, dotnet test, integration test requirements)
6. **Phase 1 Runtime requirements** — Added Neo4j 5.11+ requirement and Docker requirement
7. **Phase 1 Package versions** — Added verified package version table (Neo4j.Driver 6.0.0, M.E.* 10.0.5)

**Verification:**
- Both documents read coherently with no contradictions
- All added information is sourced from verified analysis (python-agent-memory-analysis.md, implementation-status.md, SchemaBootstrapper.cs, .csproj files)
- 34 unit tests confirmed passing, build confirmed clean (0 warnings, 0 errors)
- Existing content preserved — all changes are additions/amendments, not rewrites
