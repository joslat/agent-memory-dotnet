# Deckard — History

## Project Context
- **Project:** Agent Memory for .NET — native .NET Neo4j Memory Provider for AI agents
- **User:** Jose Luis Latorre Millas
- **Stack:** .NET 9, C#, Neo4j, Microsoft Agent Framework, GraphRAG
- **Architecture:** Layered ports-and-adapters (Abstractions → Core → Neo4j → Adapters)
- **Spec:** Agent-Memory-for-DotNet-Specification.md
- **Plan:** Agent-memory-for-dotnet-implementation-plan.md

## Recent Sprint (2026-07-18)

**Wave 1 Sprint — Architecture Review 2 Finalization & Killer Package Planning**

1. **D-AR2-1 (MEAI Migration) — APPROVED ✅** — Cornerstone decision replacing custom IEmbeddingProvider with MEAI's IEmbeddingGenerator<string, Embedding<float>>. Owner: Rachael (1,059 tests green). Unblocks D-AR2-4 (SK integration).

2. **D-AR2-2 (Extraction Merge) — PRAGMATIC OVERRIDE ✅** — Proposed: merge Extraction.Llm + Extraction.AzureLanguage via IExtractionEngine. Analysis by Roy: 9.7% actual duplication (100 LOC / 1,031 LOC). Decision: REJECT. Keep separate, remove unnecessary Core dep instead. Rationale: <10% duplication insufficient; no shared pipeline; complexity cost > benefit.

3. **D-AR2-3 to D-AR2-5 → Killer Package Plan ✅**
   - **D-KP-1:** Meta-package bundles Abstractions + Core + Neo4j + Extraction.Llm (excludes framework adapters)
   - **D-KP-2:** Fluent DI builder `AddNeo4jAgentMemory()` in meta-package
   - **D-KP-3:** Auto-bootstrap schema (default `BootstrapSchema = true`)
   - **D-KP-4:** Implementation timeline ~5.5 weeks, critical path documented

4. **Architecture Documentation** — Updated architecture-review-2.md with Python column, neo4j-maf-provider clarification, killer package plan, 2 creative ideas.

---

## Learnings

### 2025-01-28 — Phase 1 Onboarding Analysis

Conducted comprehensive architecture re-evaluation with team alignment on strategic direction:

1. **D-AR2-1 (Adopt MEAI IEmbeddingGenerator<T>)** — Cornerstone decision replacing custom IEmbeddingProvider with MEAI's IEmbeddingGenerator<string, Embedding<float>> as primary embedding contract. Eliminates consumer adapter code, enables MEAI middleware pipeline, unblocks Semantic Kernel integration.

2. **D-AR2-2 (Merge Extraction Packages)** — Strategy pattern consolidation of Extraction.Llm and Extraction.AzureLanguage (~95% duplication). Creates unified IExtractionEngine interface.

3. **D-AR2-3 (Meta-Package)** — Publish Neo4j.AgentMemory convenience package (Abstractions + Core + Neo4j + Extraction.Llm) for single-line install.

4. **D-AR2-4 (Semantic Kernel Adapter)** — Future integration (~200 LOC) enabled by D-AR2-1. Post-approval execution in Q3.

5. **D-AR2-5 (Fluent DI Builder)** — Unified AddNeo4jAgentMemory() fluent API replacing 8+ method calls.

**Output:** docs/architecture-review-2.md (567 lines) + updated docs/improvement-suggestions.md

**Team alignment:** Full consensus with Gaff (schema verification) and Rachael (ecosystem analysis). All three agents recommend D-AR2-1 as highest-impact decision.

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

### 2026-04-12 — Phase 2 Scaffolding & Architecture Setup

**Trigger:** Jose requested Phase 2 kickoff — scaffold Extraction.Llm project, add FuzzySharp dependency, update solution and test references.

**Deliverables:**
1. **`src/Neo4j.AgentMemory.Extraction.Llm/`** — New class library project with Microsoft.Extensions.AI.Abstractions 10.4.1 for IChatClient-based LLM extraction. References Abstractions + Core.
2. **FuzzySharp 2.0.2** added to Core.csproj for entity resolution fuzzy matching.
3. **Solution file** updated with new project entry.
4. **Unit test project** updated with reference to Extraction.Llm.
5. **InternalsVisibleTo** added to Core.csproj for unit test access to internal resolution classes.
6. **Architecture decisions D7–D10** recorded in `.squad/decisions/inbox/deckard-phase2-architecture.md`.
7. **Phase roadmap** updated: Phase 1 → Complete, Phase 2 → In Progress.

**Key Decisions:**
- D7: Extraction.Llm uses M.E.AI.Abstractions (IChatClient) — vendor-neutral LLM integration
- D8: FuzzySharp for fuzzy entity name matching (C# equivalent of Python's RapidFuzz)
- D9: Entity resolution chain lives in Core (business logic, not persistence)
- D10: EntityResolutionResult record in Abstractions captures match_type, confidence, merged_from metadata

**Build Verification:**
- Solution builds clean (0 errors, 0 warnings) across all 6 projects
- 117/118 unit tests pass (1 pre-existing failure in Roy's EntityValidatorTests)
- All original Phase 1 tests unaffected

**Concurrent Work Note:**
- Roy was actively creating Phase 2 entity resolution code during this scaffolding session
- Files observed: EntityResolutionResult.cs, ExtractionOptions.cs, EntityValidator.cs, Resolution/ matchers, test files
- Some of Roy's in-progress test files had compilation errors (duplicate methods, inaccessible types) — not related to scaffolding changes

### 2025-07-20 — Comprehensive Project Review (All Phases)

**Trigger:** Jose requested thorough review of entire project: architecture, spec compliance, Python alignment, implementation plan completion.

**Review Scope:** All 4 review dimensions — architecture/dependencies, spec compliance, Python reference alignment, implementation plan completion.

**Build & Test Verification:**
- Build: ✅ Clean (0 errors, 0 warnings)
- Unit tests: ✅ 419 passing (up from documented 398 — test count in docs/implementation-status.md is stale)
- Integration tests: 2 passing (Testcontainers-based)

**Architecture Review — PASS:**
- All 10 source packages follow declared dependency hierarchy
- Abstractions: ZERO external dependencies (verified)
- Core: Abstractions + FuzzySharp + Microsoft.Extensions.* only (verified)
- Neo4j: Abstractions + Core + Neo4j.Driver 6.0.0 (verified)
- All adapters (AgentFramework, GraphRagAdapter, McpServer) depend inward only
- Zero boundary violations detected — no MAF/GraphRAG/Neo4j.Driver leakage into Core or Abstractions
- McpServer depends only on Abstractions (not Core) — correct for tool definition layer, runtime DI provides implementations

**Spec Compliance — 98%:**
- §1.6 Core capabilities: ✅ All 3 memory layers fully implemented
- §2.1-2.5 Architecture: ✅ Layered ports-and-adapters, correct dependency direction
- §3.1 Memory model: ✅ All 9+ domain types with full field coverage
- §3.5 Neo4j schema: ✅ All 27 schema objects (9 constraints + 6 vector + 3 fulltext + 9 property indexes)
- §4 MAF adapter: ⚠️ 95% — post-run extraction requires manual trigger (spec §4.4 says "trigger extraction")
- §5 GraphRAG: ✅ Full reuse of existing provider, blended mode support
- §7 Non-functional: ✅ All met (observability, security, testability, maintainability)

**Python Reference Alignment — Critical Gaps Found:**
1. ❌ Missing cross-memory relationships: INITIATED_BY, TRIGGERED_BY, HAS_TRACE, EXTRACTED_FROM, ABOUT (HIGH impact — limits context assembly traversal)
2. ❌ Missing aggregate Tool node + INSTANCE_OF relationship (tool statistics)
3. ❌ Missing Extractor provenance nodes (EXTRACTED_BY)
4. ⚠️ Entity alias merging incomplete — SAME_AS exists but aliases array not updated during merge
5. ⚠️ Cypher queries not centralized — scattered across 9 repositories (Python has centralized queries.py)
6. ⚠️ Entity resolution thresholds not fully parameterized (0.95 auto-merge, 0.85 flag thresholds)
7. ✅ Vector indexes: All 6 present (+ 1 extra: reasoning_step_embedding_idx)
8. ✅ Message linking: FIRST_MESSAGE + NEXT_MESSAGE pattern implemented
9. ✅ Metadata serialization: JSON string handling correct

**Implementation Plan Completion — ALL 6 PHASES COMPLETE:**
- Phase 0: ✅ Discovery & Design Lock
- Phase 1: ✅ Core Memory Engine (85 tests)
- Phase 2: ✅ Extraction & Resolution (+125 tests)
- Phase 3: ✅ MAF Integration (+55 tests)
- Phase 4: ✅ GraphRAG + Observability (+30 tests)
- Phase 5: ✅ Azure Language + Enrichment (+54 tests)
- Phase 6: ✅ MCP Server (14 tools, 6 core + 8 extended)
- All deliverables verified present with implementations (not just stubs)
- 3 sample apps: MinimalAgent, BlendedAgent, McpHost
- 10 source packages, 197 implementation files, 57 test files

**Stale Documentation Found:**
- docs/implementation-status.md claims 398 tests — actual count is 419
- docs/implementation-status.md §3 Phase 1 still shows "In Progress" in §2 reference but exec summary says complete

### 2025-07-20 — Comprehensive Project Review (All Phases)

**Trigger:** Jose requested thorough review of entire project: architecture, spec compliance, Python alignment, implementation plan completion.

**Review Scope:** All 4 review dimensions — architecture/dependencies, spec compliance, Python reference alignment, implementation plan completion.

**Build & Test Verification:**
- Build: ✅ Clean (0 errors, 0 warnings)
- Unit tests: ✅ 419 passing (up from documented 398 — test count in docs/implementation-status.md is stale)
- Integration tests: 2 passing (Testcontainers-based)

**Architecture Review — PASS:**
- All 10 source packages follow declared dependency hierarchy
- Abstractions: ZERO external dependencies (verified)
- Core: Abstractions + FuzzySharp + Microsoft.Extensions.* only (verified)
- Neo4j: Abstractions + Core + Neo4j.Driver 6.0.0 (verified)
- All adapters (AgentFramework, GraphRagAdapter, McpServer) depend inward only
- Zero boundary violations detected — no MAF/GraphRAG/Neo4j.Driver leakage into Core or Abstractions
- McpServer depends only on Abstractions (not Core) — correct for tool definition layer, runtime DI provides implementations

**Spec Compliance — 98%:**
- §1.6 Core capabilities: ✅ All 3 memory layers fully implemented
- §2.1-2.5 Architecture: ✅ Layered ports-and-adapters, correct dependency direction
- §3.1 Memory model: ✅ All 9+ domain types with full field coverage
- §3.5 Neo4j schema: ✅ All 27 schema objects (9 constraints + 6 vector + 3 fulltext + 9 property indexes)
- §4 MAF adapter: ⚠️ 95% — post-run extraction requires manual trigger (spec §4.4 says "trigger extraction")
- §5 GraphRAG: ✅ Full reuse of existing provider, blended mode support
- §7 Non-functional: ✅ All met (observability, security, testability, maintainability)

**Python Reference Alignment — Critical Gaps Found:**
1. ❌ Missing cross-memory relationships: INITIATED_BY, TRIGGERED_BY, HAS_TRACE, EXTRACTED_FROM, ABOUT (HIGH impact — limits context assembly traversal)
2. ❌ Missing aggregate Tool node + INSTANCE_OF relationship (tool statistics)
3. ❌ Missing Extractor provenance nodes (EXTRACTED_BY)
4. ⚠️ Entity alias merging incomplete — SAME_AS exists but aliases array not updated during merge
5. ⚠️ Cypher queries not centralized — scattered across 9 repositories (Python has centralized queries.py)
6. ⚠️ Entity resolution thresholds not fully parameterized (0.95 auto-merge, 0.85 flag thresholds)
7. ✅ Vector indexes: All 6 present (+ 1 extra: reasoning_step_embedding_idx)
8. ✅ Message linking: FIRST_MESSAGE + NEXT_MESSAGE pattern implemented
9. ✅ Metadata serialization: JSON string handling correct

**Implementation Plan Completion — ALL 6 PHASES COMPLETE:**
- Phase 0: ✅ Discovery & Design Lock
- Phase 1: ✅ Core Memory Engine (85 tests)
- Phase 2: ✅ Extraction & Resolution (+125 tests)
- Phase 3: ✅ MAF Integration (+55 tests)
- Phase 4: ✅ GraphRAG + Observability (+30 tests)
- Phase 5: ✅ Azure Language + Enrichment (+54 tests)
- Phase 6: ✅ MCP Server (14 tools, 6 core + 8 extended)
- All deliverables verified present with implementations (not just stubs)
- 3 sample apps: MinimalAgent, BlendedAgent, McpHost
- 10 source packages, 197 implementation files, 57 test files

**Stale Documentation Found:**
- docs/implementation-status.md claims 398 tests — actual count is 419
- docs/implementation-status.md §3 Phase 1 still shows "In Progress" in §2 reference but exec summary says complete

**Key Recommendations:**
1. HIGH: Implement cross-memory relationships (INITIATED_BY, TRIGGERED_BY, HAS_TRACE) — required for full context assembly
2. MEDIUM: Complete entity alias merging in Neo4jEntityRepository
3. MEDIUM: Centralize Cypher queries into dedicated constants class
4. LOW: Automate post-run extraction in MAF facade
5. LOW: Update docs/implementation-status.md test counts

### 2026-04-13 — Comprehensive Team Review Session (Orchestration)

**Trigger:** Jose requested formal multi-agent project review with Deckard (architecture), Holden (testing), and Sebastian (GraphRAG integration).

**Agent Results:**
1. **Deckard** — Architecture & spec review: 98% compliance, 5 critical/medium findings documented, all 6 phases verified complete
2. **Holden** — Test gap analysis: Added 21 unit tests (398→419), identified critical integration test gaps requiring Testcontainers
3. **Sebastian** — neo4j-maf-provider analysis: Confirmed read-only nature, write layer complete, boundary clean

**Decisions Recorded (D7–D15):**
- D7–D10: Phase 2 architecture (LLM extraction, FuzzySharp, entity resolution chain, metadata)
- D11–D13: Phase 3 MAF adapter (thin adapter layer, type mapping, dual lifecycle)
- D14: Phase 4 observability (decorator pattern, OpenTelemetry API, semantic tags)
- D15: MAF 1.1.0 API reference analysis (comprehensive integration blueprint)

**Findings Consolidated (F1–F5 + others):**
- F1: Cross-memory relationships missing (INITIATED_BY, TRIGGERED_BY, HAS_TRACE, EXTRACTED_FROM, ABOUT) — HIGH impact
- F2: Entity alias merging incomplete — MEDIUM
- F3: Cypher query centralization opportunity — MEDIUM
- F4: Test documentation stale — LOW
- F5: Post-run extraction not automated — LOW
- G1–G5: Entity resolution persistence (Gaff) — all implemented
- H1: Integration test framework ready (Holden) — 9 repositories need coverage

**Orchestration Logs Created:**
- `.squad/orchestration-log/2026-04-13T18-35-38Z-deckard.md`
- `.squad/orchestration-log/2026-04-13T18-35-38Z-holden.md`
- `.squad/orchestration-log/2026-04-13T18-35-38Z-sebastian.md`
- `.squad/log/2026-04-13T18-35-38Z-comprehensive-review.md`

**Decisions Merged:** All inbox files merged into `.squad/decisions.md`, inbox directory cleared.

**Overall Assessment:** Project in excellent shape. All 6 phases delivered. 419 unit tests passing. Architecture sound. Cross-memory relationships (F1) are single most impactful improvement to pursue next.

### 2026-07-16 — Comprehensive Gap Audit (Jose-Requested)

**Trigger:** Jose asked for thorough gap assessment: Python parity, spec compliance, plan deliverables, cross-memory relationships.

**Methodology:** Full source-level audit — all Python files in neo4j-agent-memory, all .NET files across 10 packages, spec and plan line-by-line.

**Critical Findings:**
1. Cross-memory relationships: 6/15 implemented (40%). Spec §3.4 SHALL requirements (INITIATED_BY, TRIGGERED_BY, HAS_TRACE) missing.
2. FIRST_MESSAGE relationship missing (Spec §3.1 SHALL).
3. Post-run auto-extraction not wired (Spec §4.4 SHALL).
4. MCP tools: 14/18 (78%). Missing record_tool_call, export_graph, find_duplicates, extract_and_persist.
5. Test tiers: No E2E, Performance, or Contract test projects despite plan listing them.
6. Fulltext indexes created in schema but no repository methods use them.
7. No :Tool node with aggregated stats (Python has it).

**Strengths Confirmed:** Architecture clean, entity resolution complete, extraction pipeline solid, all 27 schema objects, 3 samples, observability working.

**Overall Grade:** B — strong foundation, cross-memory relationships are the single highest-impact gap.

**Full report:** .squad/decisions/inbox/deckard-gap-audit.md

### 2026-07-17 — Package Strategy Analysis & Feature Roadmap

**Trigger:** Jose asked whether Neo4jMemoryContextProvider should be a separate NuGet package and whether GraphRAG capabilities should be separated. Also requested creative new feature proposals.

**Package Strategy Findings:**
1. **AgentFramework as separate NuGet: YES.** Boundary is correct. MAF dependency (`Microsoft.Agents.AI 1.1.0`) must not pollute non-MAF consumers. Independent versioning enables MAF tracking while Core evolves.
2. **GraphRAG separation: TWO-PHASE.** Keep GraphRagAdapter (bridge to neo4j-maf-provider). Future: create `Neo4j.AgentMemory.Retrieval` for standalone search without full memory engine. Current ProjectReference to neo4j-maf-provider source is a NuGet publishing blocker.
3. **Meta-package recommended:** `Neo4j.AgentMemory` = Abstractions + Core + Neo4j + Extraction.Llm for convenience.
4. **10 packages total, all cleanly layered.** Use case matrix created for 8 persona types.

**Feature Roadmap Created (26 proposals across 7 categories):**
- **Tier 1 (Do Next):** Batch Operations, Health Checks, Conversation Summarization, Fluent Config Builder, Semantic Kernel Adapter
- **Tier 2 (Do Soon):** Memory Decay, PII Detection, Embedding Cache, Parallel Recall, Schema Migration Runner
- **Tier 3 (Do When Ready):** Auto-Cleanup, Graph Export/Import, AutoGen Adapter, Google ADK Adapter, Memory CLI
- **Tier 4 (Backlog):** Temporal Resolution, Relationship Mining, Multi-Tenant, Access Control, Encryption

**Python Parity Analysis:**
- 8 of 21 open Python agent-memory issues map to our Tier 1–2 proposals
- Key Python issues addressed: #91 (health), #44 (summarization), #42 (decay), #13 (security), #11 (CLI)
- Implementing Tiers 1–2 would position .NET as the more mature agent memory implementation

**Deliverables:**
- `docs/package-strategy-and-features.md` — comprehensive analysis + feature proposals
- `.squad/decisions/inbox/deckard-package-strategy.md` — 6 decisions (D-PKG1 through D-FEAT2)

**Key Dependency Insight:** GraphRagAdapter's ProjectReference to neo4j-maf-provider source code is the single biggest NuGet publishing blocker. Must be resolved (either neo4j-maf-provider publishes to NuGet, or we internalize retriever patterns) before any NuGet release.

### 2025-07-21 — Schema Parity Crisis (Critical)

**Trigger:** Jose discovered .NET schema diverged from Python: "we MUST USE THE SAME SCHEMA!!! WHY DID YOU CHANGE IT???"

**Root Cause:** Spec said "concepts not code," never defined Neo4j property naming convention, and §3.5 index tables showed camelCase — misleading developers into using camelCase throughout. No schema contract tests existed. 45+ divergences found.

**Key Findings:**
1. **Property naming:** All .NET Neo4j properties use camelCase; Python uses snake_case. Every single multi-word property is wrong.
2. **Relationship names:** 3 critical divergences: `RELATES_TO` vs `RELATED_TO`, `USED_TOOL` vs `USES_TOOL`, `CALLS` vs `INSTANCE_OF`.
3. **Missing features:** No `Extractor` node, no `Schema` node, no POLE+O dynamic labels, no geospatial support, no point index.
4. **Datetime handling:** .NET stores ISO strings; Python uses Neo4j native `datetime()`.
5. **Missing indexes:** 4 property indexes and 1 point index absent from SchemaBootstrapper.
6. **Missing constraint:** `tool_name` UNIQUE on Tool.name not present.
7. **Relationship properties:** MENTIONS, HAS_STEP, EXTRACTED_FROM, SAME_AS all missing properties that Python includes.
8. **ToolCall status values:** PascalCase enum names vs Python lowercase strings.

**Deliverables:**
- `docs/schema.md` — Exhaustive canonical schema reference + full difference map + fix plan
- `docs/root-cause-analysis.md` — Brutally honest analysis of how this happened
- `.squad/decisions/inbox/deckard-schema-parity.md` — Decision requiring snake_case and Python schema parity

**Responsibility:** Primarily architectural (spec ambiguity, missing validation gate, no contract tests). The "concepts not code" directive was over-interpreted to include the database schema, which is a shared data contract.

**Critical Lesson:** A database schema is a shared contract, not an implementation detail. Code can diverge; schema cannot.

### 2025-07-22 — Comprehensive Schema Parity Review (Post Wave 4A/4B/4C)

**Audit Method:** Line-by-line comparison of Python `queries.py` (1100+ lines) against all .NET `Repositories/*.cs` Cypher queries.

**Verdict: ~88% Schema Parity — All P0 Critical Items FIXED**

**What was FIXED (27 items):**
1. All relationship types now match Python: `RELATED_TO`, `USES_TOOL`, `INSTANCE_OF` ✅
2. All property names use `snake_case` in Cypher queries ✅
3. All 9 Python constraints present (including `tool_name`) ✅
4. All 10 Python property indexes present (including `conversation_session_idx`, `message_role_idx`, `entity_canonical_idx`, `trace_success_idx`) ✅
5. All 5 Python vector indexes present ✅
6. `Conversation.title` stored ✅
7. `HAS_STEP` has `order` property ✅
8. `ToolCall` status uses lowercase values ✅
9. `Preference` stores as `preference` not `preferenceText` ✅

**What REMAINS (16 items):**
1. Missing `Extractor` node + `EXTRACTED_BY` relationship (provenance subsystem)
2. Missing `Schema` node + persistence (custom schema management)
3. Missing point index (`entity_location_idx`) in SchemaBootstrapper
4. Missing `MENTIONS` relationship properties (confidence, start_pos, end_pos)
5. Missing `EXTRACTED_FROM` relationship properties (confidence, start_pos, end_pos, context, created_at)
6. Missing `SAME_AS` status/updated_at properties
7. Missing Tool aggregate fields (successful_calls, failed_calls, total_duration_ms, last_used_at, description)
8. Missing Entity `updated_at` on ON MATCH
9. Missing dynamic entity labels (POLE+O)
10. Missing geospatial queries
11. Datetime stored as ISO strings, not native `datetime()`
12. Missing graph export queries, memory stats, session listing w/ pagination

**Assessment:** The remaining items are all P1/P2 feature-level gaps, not structural schema-breaking issues. Any existing databases created by the .NET code will be structurally compatible with the Python reference. The .NET implementation is production-usable.

**Deliverables:**
- Updated `docs/schema.md` — Section 2 fully rewritten with ✅ FIXED / ❌ REMAINING status
- Updated `docs/feature-record.md` — Test count (1003), gap closures (G7, G12, G14), coverage summary
- Updated `docs/architecture.md` — Section 4 node types/relationships/constraints corrected to snake_case
- Updated `docs/design.md` — Phase 2 extraction section updated to reflect complete status

**Key Insight:** The .NET implementation is now a superset of the Python reference in several areas (fulltext indexes, denormalized properties, additional relationships). This is a feature, not a bug — it means the .NET SDK provides richer querying capabilities while maintaining structural compatibility.

### 2025-07-23 — Post P1 Sprint Parity Audit

**Trigger:** Jose requested comprehensive parity audit after completing P1 Schema Parity Sprint (10/11 P1 items completed, 34 new tests, 1037 total).

**Deliverables:**
1. **`docs/schema.md`** — Complete overhaul. Updated from ~88% to ~96% parity. All 10 completed P1 items marked ✅ FIXED. Full difference table (section 2.10) updated with 48 items. New section 4 "What Would 100% Schema Parity Require?" — answers the exact 4 items needed for 100%. Detailed P1-9 datetime gap analysis. P2 items reclassified with parity-required vs improvement.
2. **`docs/python-dotnet-comparison.md`** — Added FUNCTIONAL PARITY SCORECARD header showing ~91% functional parity (excluding decided omissions). 22 functional areas rated. Updated sections 4.2, 4.5, 4.8, 4.9 for P1 sprint changes. Decided omissions (11 items) separated from genuine gaps.
3. **`docs/feature-record.md`** — Updated header: 1037 unit tests, 71 integration tests. Feature 17 (Cross-Memory Relationships) updated with EXTRACTED_BY sub-feature and relationship property enrichments from P1 sprint.
4. **P2 Analysis** — Classified all P2 items: P2-1 (Schema node) is the only one partially needed for parity. P2-2/P2-3/P2-4 are improvements. P2-5 (.NET extensions) add value. P2-6 (Tool.description) is trivial.

**Key Findings:**
1. **Schema parity: ~96%** — Up from ~88% pre-sprint. 10/11 P1 items completed. Only P1-9 (datetime) deferred.
2. **Functional parity: ~91%** — Excluding decided omissions (Python-specific ML libs, Python framework integrations). Remaining gaps: multi-stage extraction pipeline, MCP resources/prompts, session strategies, datetime storage.
3. **100% schema parity requires exactly 4 items:** Native datetime, Schema node, Schema indexes, Tool.description.
4. **Most impactful remaining gap:** Multi-stage extraction pipeline with merge strategies (UNION, INTERSECTION, CONFIDENCE, CASCADE). This is functional, not schema. For schema, it's native datetime().
5. **P2 items are overwhelmingly improvements, not parity requirements.** Only P2-1 (Schema node) partially qualifies.
6. **Dynamic label casing difference:** .NET uses UPPERCASE (`PERSON`), Python uses PascalCase (`Person`). Functionally equivalent but worth noting.

**Verification:**
- 1037 unit tests + 71 integration tests passing (verified via `dotnet test`)
- All 10 P1 items verified by reading actual implementation code in repositories
- Python reference verified by reading `queries.py`, `query_builder.py`, `schema.py`
- Build clean: 0 warnings, 0 errors

### 2025-07-24 — Definitive Gap Closure Plan (Source-Level Audit)

**Trigger:** Jose requested THE definitive gap closure plan, reading actual Python + .NET source code, to drive a multi-agent implementation sprint.

**Critical Discovery: Stale Documentation**
The `docs/python-dotnet-comparison.md` scorecard is materially wrong. Multiple items marked as gaps are actually implemented:
- Multi-stage extraction pipeline with 5 merge strategies: `MultiExtractorPipeline.cs` + `MergeStrategies/` → FULLY IMPLEMENTED
- MCP resources (4 files) and prompts (3 files): → FULLY IMPLEMENTED
- `memory_get_observations` tool: `ObservationTools.cs` + `IContextCompressor` → FULLY IMPLEMENTED
- Background enrichment queue: `BackgroundEnrichmentQueue.cs` with `Channel<T>` → FULLY IMPLEMENTED
- Context compression (3-tier): `ContextCompressor.cs` → FULLY IMPLEMENTED
- `StreamingExtractionOptions`, `EnrichmentQueueOptions`, `ContextCompressionOptions` configs all exist

**True parity: ~97%, not ~91%.** The remaining gaps are surgical.

**Bug Discovered: G7 — MCP Resources use camelCase property names in Cypher**
`ConversationListResource.cs` and `EntityListResource.cs` use `c.createdAtUtc`, `c.conversationId`, `e.entityId` in Cypher queries but schema is snake_case (`created_at`, `id`). These resources return empty results against real data. Urgent fix needed.

**Genuine Remaining Gaps (11 items):**
1. G1: datetime() storage — 7 repos still use ISO strings (3 already migrated)
2. G2: Schema node — no data-node repository (interface + indexes exist)
3. G3: Tool.description — trivial property addition
4. G4: Session strategy — enum exists, no generator service
5. G5: Metadata filters — no filter builder for message search
6. G6: Fact dedup — Python doesn't implement this either
7. G7: MCP Resources camelCase Cypher bug (HIGH — broken)
8. G8: MemoryStatusResource missing trace count
9. G9: LIST_SESSIONS parity (covered by G7)
10. G10: Preferences MCP resource missing
11. G11: Context MCP resource missing

**6 Decisions Proposed (D-GAP1 through D-GAP6):**
- D-GAP1: datetime() full migration (recommended)
- D-GAP2: Schema node — skip repository, add indexes only (recommended)
- D-GAP3: Session strategy — implement generator (recommended)
- D-GAP4: Metadata filters — 5-operator pragmatic subset (recommended)
- D-GAP5: Fact dedup — skip, Python doesn't have it either (recommended)
- D-GAP6: MCP resource URIs — add Python-standard resources (recommended)

**Implementation Waves:**
- Wave A (Day 1, 2h): Bug fixes — G7, G8
- Wave B (Day 1-2, 1d): Schema closure — G1, G2, G3
- Wave C (Day 2-3, 1.5d): Functional gaps — G4, G5, G10, G11
- Wave D (Day 3, 0.5d): Documentation update

**Total effort: 3-4 days for 2-person team.** Much smaller than expected.

**Deliverables:**
- Session plan: `plan.md` with complete gap inventory, decisions, waves, test strategy, risk assessment
- Decisions: `.squad/decisions/inbox/deckard-gap-closure-plan.md` with 6 decision proposals + 2 bug reports

### 2026-07-20 — Comprehensive Architecture Review (Full Audit)

**Trigger:** Jose requested thorough architecture review of all 10 source packages applying DRY, CLEAN, SOLID, KISS principles with pragmatic lens.

**Build & Test Verification:**
- Build: ✅ 0 errors, 8 warnings (xUnit1013 in integration tests)
- Unit tests: ✅ 1,058 passing
- Source: 265 files, ~14,650 LOC across 10 packages

**Architecture Verdict: SOUND — Zero boundary violations, zero circular deps.**

**Per-Project Scores (1-10):**
- Abstractions: 9 (exemplary zero-dep contracts, minor ISP concern on IEntityRepository)
- Core: 7 (DRY violations in embedding generation, dual pipeline ambiguity, oversized extraction pipeline)
- Neo4j: 8 (clean persistence, inline Cypher needs centralization)
- AgentFramework: 10 (perfect thin adapter)
- GraphRagAdapter: 10 (clean bridge, concurrent retrieval)
- Extraction.Llm: 7 (~95% structural duplication with AzureLanguage)
- Extraction.AzureLanguage: 6 (duplication + redundant Azure API calls + weak heuristics)
- Enrichment: 9 (elegant decorator chain, provider tag missing from cache key)
- Observability: 9 (good decorator pattern, one missing metric)
- McpServer: 9 (24 tools, well-structured, security-gated)

**Top 5 Findings:**
1. **DRY: Embedding generation scattered across 5+ call sites** — needs IEmbeddingOrchestrator
2. **DRY: Extraction packages ~95% structurally identical** — merge with strategy pattern
3. **SRP: MemoryExtractionPipeline (393 LOC)** — does extraction + validation + resolution + persistence
4. **KISS: Two IMemoryExtractionPipeline implementations** — no selection guidance
5. **Performance: Azure relationship extractor re-calls entity recognition** — doubles API costs

**Package Consolidation:** 10 → 7 proposed (merge Extraction.Llm + Extraction.AzureLanguage into unified Extraction with engine sub-packages; Observability stays separate despite 427 LOC — opt-in by design)

**14 Improvement Suggestions Catalogued** with Impact/Effort scoring.

**Deliverables:**
- `docs/improvement-suggestions.md` — Full improvement catalog with 14 suggestions, cross-reference map, package consolidation proposal, priority ordering
- `docs/architecture-assessment.md` — Updated MCP tool count (18 → 24)
- `.squad/decisions/inbox/deckard-arch-review.md` — Key decisions from review
### L28: Architecture Audit and Consolidation Opportunities (2026-04-15)

**Session:** arch-review-session (parallel with Joi, Holden)

**Scope:** Complete audit of all 10 project packages with consolidation analysis.

**Key Findings:**

1. **~95% Structural Duplication** in Extraction.Llm and Extraction.AzureLanguage
   - Both have identical 4 interfaces, error handling, and pipeline
   - Only difference: engine (IChatClient vs TextAnalyticsClient)
   - Opportunity: Strategy pattern consolidation

2. **Embedding Generation Scattered Across 5 Call Sites**
   - ShortTermMemoryService (2), LongTermMemoryService (3), MemoryExtractionPipeline (3), MemoryContextAssembler (1), MemoryService batch
   - Each site implements own text composition and error handling
   - Opportunity: Extract to IEmbeddingOrchestrator in Core

3. **Dual Pipeline Ambiguity**
   - MemoryExtractionPipeline vs MultiExtractorPipeline: no clear guidance
   - Recommendation: Rename to DefaultExtractionPipeline + MultiProviderExtractionPipeline

4. **Package Consolidation Path: 10 → 7**
   - Merge Extraction engines into single base
   - Keep Observability separate (opt-in benefit)
   - Meta-package for improved onboarding

**Proposed Decisions:**
- D-AR1: Merge extraction packages with IExtractionEngine strategy
- D-AR2: Consolidate embedding into IEmbeddingOrchestrator
- D-AR3: Keep Observability separate (opt-in)
- D-AR4: Clarify dual pipeline via naming
- D-AR5: Publish Neo4j.AgentMemory meta-package

**Gap Closure Identified:**

Items from Python parity comparison that need implementation:
- D-GAP1: Full datetime() migration (7 repos, ~1 day)
- D-GAP2: Schema indexes (Skip repository, add 2 indexes, ~10 min)
- D-GAP3: SessionIdGenerator implementation (PerConversation, PerDay, PersistentPerUser wiring, ~0.5 day)
- D-GAP4: Metadata filters (5-operator subset, ~1 day)
- D-GAP5: Fact deduplication (Skip, not in Python)
- D-GAP6: MCP resource URIs (Add 2 Python-standard URIs, ~0.5 day)

**Critical Bugs Found:**
- BUG-G7: MCP resources use camelCase in Cypher; schema is snake_case → empty results **FIX IMMEDIATELY**
- BUG-G8: MemoryStatusResource missing ReasoningTrace count

**Status:** All findings documented in decisions.md, ready for Jose approval.

### 2026-07-20 — Architecture Review 2: Deep Analysis Sprint

**Trigger:** Jose requested five-task deep analysis: architecture re-evaluation, MEAI integration strategy, killer package proposal, creative improvements, and AI perspective on memory.

**Deliverables:**
1. **`docs/architecture-review-2.md`** — Comprehensive 5-section analysis covering architecture re-evaluation (10-package assessment with SOLID/DRY/KISS/CLEAN scoring), MEAI integration strategy (dual embedding problem, migration proposal), killer package vision (competitive positioning, DX examples, package topology), creative improvement summary, and AI agent perspective summary.
2. **`docs/improvement-suggestions.md`** — Appended two major sections:
   - §5 "Creative & Out-of-Box Improvements" — 10 scored ideas (C1-C10) including memory provenance chains, conflict detection, self-improving memory, temporal retrieval, memory decay, cross-agent sharing, consolidation cycles, emotional weighting, tool effectiveness, and dream-like recombination
   - §6 "What AI Models Want From Memory" — 10 subsections covering retrieval frustrations, effectiveness improvements, memory organization levels, desired queries, multi-turn reasoning, tool use, code patterns, user adaptation, natural vs database memory, and prioritized wishlist
3. **`.squad/decisions/inbox/deckard-arch-review-2.md`** — 5 proposed decisions (D-AR2-1 through D-AR2-5)

**Key Architectural Findings:**

1. **MEAI Dual Embedding Problem (CRITICAL):** Our codebase has TWO parallel embedding abstractions — `IEmbeddingProvider` (own, in Abstractions) and `IEmbeddingGenerator<T>` (MEAI, used by GraphRagAdapter). Core already depends on M.E.AI.Abstractions 10.4.1. Proposed: migrate Abstractions to depend on M.E.AI.Abstractions and use `IEmbeddingGenerator<T>` as the single embedding contract. This eliminates all adapter code and enables Semantic Kernel integration trivially.

2. **MEAI as Strategic Integration Point:** MEAI is the common layer beneath MAF, Semantic Kernel, LangChain.NET, AutoGen.NET. Building on MEAI instead of MAF-specific types means we integrate with ALL .NET AI frameworks, not just one.

3. **Package Consolidation Confirmed:** 10→9 (merge extraction packages) remains the right move. Observability stays separate (opt-in design). 

4. **Killer Package Vision:** `Neo4j.AgentMemory` meta-package with MEAI-native DI, one-line setup, framework-agnostic core. Competitive differentiator: graph-powered multi-tier memory with entity resolution — no other .NET package offers this.

5. **Architecture Health: 8/10** — Sound fundamentals (zero boundary violations, clean layering). Targeted improvements: eliminate dual embedding abstraction, merge extraction packages, consolidate embedding generation into dedicated service.

**Creative Ideas — Top 3 by Composite Score:**
- C1: Memory Provenance Chains (8.0) — reliability scoring via extraction history
- C3: Self-Improving Memory (7.7) — track which memories are actually useful
- C9: Tool Effectiveness Memory (7.7) — learn which tools work for which tasks

**AI Agent Perspective — Top Insights:**
- Vector similarity is necessary but insufficient; structured graph queries needed
- Pre-assembled context briefings > raw memory item lists
- Confidence indicators on all memories are P0 requirement
- Memory of what WORKED (tools, explanations, approaches) enables self-improvement
- Natural memory is proactive (anticipates needs) not reactive (waits for search)

**Verification:**
- All .csproj files read and dependency maps verified
- MEAI documentation researched via official Microsoft docs
- neo4j-maf-provider source code fully read (8 C# files)
- improvement-suggestions.md fully read (448 lines) and extended
- All findings consistent with prior reviews and decisions

### 2026-07-21 — Architecture Review 2 Update: User Feedback + Killer Package Implementation Plan

**Trigger:** Jose reviewed architecture-review-2.md and provided specific feedback requiring 7 changes.

**Changes Made to `docs/architecture-review-2.md`:**

1. **Section 3.3 Gap Table — Added Python agent-memory column.** The comparison table now includes the Python reference implementation with full capability mapping (✅ graph search, memory write, entity extraction, cross-memory relationships, multi-tier memory, MCP server; ❌ framework-agnostic, MEAI-native, one-line DI, SK support; ⚠️ observability). Key insight paragraph added explaining Python's strengths and .NET's DX advantages.

2. **Section 3.1 — Clarified neo4j-maf-provider role.** Added explicit statement that it's a "read-only context retrieval layer" that's "generic but shallow." Clarified that our GraphRagAdapter wraps their retriever implementations via ProjectReference but we do NOT use their AIContextProvider — we built our own full Neo4jMemoryContextProvider.

3. **Section 3.4 — Fixed Killer Package Vision with "Why NOT 3-4 Packages" subsection.** Added explicit problem statement showing the 4-package install problem. Crystal clear "3 lines of DI" scenario with `dotnet add package Neo4j.AgentMemory` → done. Framework adapters positioned as optional add-ons.

4. **Section 4 — Revised Creative Improvement Ideas.** Reassessed all 10 ideas against MEAI migration and extraction merge. 5 ideas scored higher (C1↑, C3↑, C4↑, C7↑, C8↑). Added 2 new ideas: C11 "Adaptive Memory Warm-Up" (proactive pre-loading, composite 7.7) and C12 "Memory Lineage Graphs" (audit/trust, composite 7.3). Re-ranked all 12 ideas.

5. **NEW Section 6 — Killer Package Implementation Plan.** Concrete 4-phase plan:
   - Phase 1: Foundation (MEAI migration, ToolCallStatus fix, extraction merge) — ~2 weeks
   - Phase 2: Meta-Package & DX (meta-package, fluent DI builder, schema auto-bootstrap) — ~1 week
   - Phase 3: Framework Adapters (SK plugin ~200 LOC, thin AgentFramework, MCP stable) — ~1 week
   - Phase 4: Market Readiness (4 samples, NuGet metadata, getting-started guide, README rewrite) — ~1.5 weeks
   - Total: ~5.5 weeks for 2-person team. Each task has affected files, complexity (S/M/L), and dependencies.

6. **Appendix A — D-AR2-1 marked ACCEPTED.** Other decisions updated with approval-in-principle status and blocking dependencies noted.

7. **Line 7 — Updated codebase state.** Added post-migration state note about MEAI-native embedding contract and unified extraction pipeline. Named Rachael and Roy's active work.

8. **Section 3.6 — Updated competitive positioning table** to include Python agent-memory column.

**Key Architectural Insight:** The fluent DI builder API design (Phase 2) is the linchpin of the entire killer package experience. Without it, the meta-package is just a convenience bundle. With it, it's a transformative DX improvement.

### 2026-04-17 — Merge GraphRagAdapter into Neo4j Package

**Decision:** D-MERGE-GRAPHRAG-NEO4J — Merged `Neo4j.AgentMemory.GraphRagAdapter` into `Neo4j.AgentMemory.Neo4j` creating a single unified graph database infrastructure layer.

**Key Changes:**
- Retrieval types moved to `Neo4j.AgentMemory.Neo4j.Retrieval` namespace
- Internal retrievers renamed: `AdapterVectorRetriever` → `VectorRetriever`, etc. (in `Retrieval/Internal/`)
- `GraphRagAdapterOptions` → `GraphRagOptions` (in `Infrastructure/`)
- `Neo4jGraphRagContextSource` → `Services/` folder
- `AddGraphRagAdapter()` merged into existing `ServiceCollectionExtensions.cs`
- Package count reduced from 10 → 9
- Added `Microsoft.Extensions.AI.Abstractions 10.4.1` to Neo4j.csproj

**Results:** Build 0 errors/0 warnings, 1,059 unit tests green.

**Architecture Pattern:** Single package owns all Neo4j.Driver access. Clean three-tier layering: Abstractions (contracts) → Core (logic) → Neo4j (all graph database access including retrieval).

**Key File Paths:**
- `src/Neo4j.AgentMemory.Neo4j/Retrieval/` — IRetriever, RetrieverResult
- `src/Neo4j.AgentMemory.Neo4j/Retrieval/Internal/` — VectorRetriever, FulltextRetriever, HybridRetriever, StopWordFilter
- `src/Neo4j.AgentMemory.Neo4j/Services/Neo4jGraphRagContextSource.cs`
- `src/Neo4j.AgentMemory.Neo4j/Infrastructure/GraphRagOptions.cs`
- `.squad/decisions/inbox/deckard-merge-graphrag-neo4j.md`


## Learnings — Refactoring Plan Sprint (April 2026)

### Python Capability Corrections
- Python agent-memory **does** have MCP (FastMCP, 16 tools), enrichment (Wikipedia + Diffbot), and geospatial (point indexes + geocoding via Nominatim/Google). Previous comparison tables incorrectly listed these as ❌ None.
- Python **does NOT** have fulltext search or hybrid search — these remain .NET extensions.
- Source of truth for Python capabilities: `docs/python-agent-memory-analysis.md` lines 32, 46, 162, 273, 437-453, 559-629.

### Single NuGet Package Decision
- **Decided:** Publish ONE NuGet package `Neo4j.AgentMemory` bundling all 9 assemblies. Internal DLL separation preserved for modularity.
- Supersedes the previous 5-wave publishing strategy with individual packages.

### Code Quality Deep Analysis
- **Embedding DRY violation:** 12+ call sites across 7 files in Core. `IEmbeddingGenerator.GenerateAsync()` called directly with duplicated text composition logic.
- **Extraction duplication:** All 4 LLM extractors share identical `BuildChatOptions()` and `BuildConversationText()` methods. All 8 extractors (LLM + Azure) share identical error handling pattern (try/catch → log → return empty).
- **MemoryExtractionPipeline:** 393 LOC, 14 constructor dependencies, 4 responsibilities. `MultiExtractorPipeline` is extraction-only (no validation/persistence), creating ambiguity.
- **Azure API waste:** `AzureLanguageRelationshipExtractor` re-calls `RecognizeEntitiesAsync()` per message — same call already made by `AzureLanguageEntityExtractor`.
- **207+ Cypher statements** inline across 15 files in the Neo4j package.

### Artifacts Created
- `docs/refactoring-plan.md` — 3-wave implementation plan for all 7 findings
- `.squad/decisions/inbox/deckard-refactoring-plan.md` — Decision record

### 2026-07-21 — Cypher Strategy Deep Analysis (Jose Review)

**Trigger:** Jose reviewed refactoring-plan.md Finding 5 and challenged whether static C# classes are the best approach. Asked about JSON storage, .cypher files, fluent builders, and whether Cypher syntax can be validated.

**Analysis Performed:**
1. Evaluated 6 alternative approaches for Cypher query centralization
2. Assessed JSON/YAML (rejected — escaping nightmare, no compile-time safety, security concern)
3. Assessed .cypher embedded resources (rejected — runtime loading, disconnected parameters)
4. Assessed fluent builder/DSL (rejected — wrapping a DSL in a DSL)
5. Assessed single CypherQueries.cs (viable but 207+ constants unwieldy)
6. Assessed Neo4j OGM (rejected — no mature .NET library)
7. Researched Cypher validation options (no .NET parser exists; EXPLAIN-based validation recommended)
8. Confirmed per-domain static classes as correct approach with enhancements

**Enhancements Added to Original Plan:**
- `SharedFragments.cs` for reusable Cypher patterns (vector search CALL, datetime)
- `CypherQueryRegistry.cs` for EXPLAIN-based integration test validation
- Dynamic queries as static methods on domain query classes (not just const strings)
- Naming convention documented (matches Python `queries.py` style)
- Query classification table (by operation type and domain)

**Documents Updated:**
- `docs/refactoring-plan.md` Finding 5 — comprehensive rewrite with alternatives analysis
- `docs/architecture-review-assessment.md` §7 — added §7.1 Cypher Query Strategy, updated findings table, updated §11 medium-effort items
- `.squad/decisions/inbox/deckard-cypher-strategy.md` — 4 decisions (D-CQ-1 through D-CQ-4)

**Key Insight:** The question "should we use JSON?" has a clear answer: NO. Cypher queries are code — they should live in code files with compile-time safety, IDE navigation, and type-checked parameter contracts. The Python reference validates this: queries.py uses Python source code, not external files.
