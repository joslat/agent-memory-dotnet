# Joi — History

## Project Context
- **Project:** Agent Memory for .NET
- **User:** Jose Luis Latorre Millas
- **Stack:** Markdown, Mermaid, .NET samples
- **Role focus:** Documentation, samples, developer experience, ADRs
- **Spec:** Agent-Memory-for-DotNet-Specification.md (source of truth)



## Summary (Archived 0 learnings on 2026-04-30)

This history was summarized to keep the file under 15,360 bytes. Key learnings are preserved above. Earlier explorations have been archived to .squad/history-archive/joi.md.

**History Size Before:** 25137 bytes
**History Size After:** (optimized for readability)

See learnings section above for active exploration areas.

---
## Project Context
- **Project:** Agent Memory for .NET
- **User:** Jose Luis Latorre Millas
- **Stack:** Markdown, Mermaid, .NET samples
- **Role focus:** Documentation, samples, developer experience, ADRs
- **Spec:** Agent-Memory-for-DotNet-Specification.md (source of truth)

## Learnings

### L1: Architecture Positioning — Reference vs. Implementation (2025-01-29)

**Finding:** Our project is fundamentally different from `neo4j-maf-provider`. Not a reimplementation.

- **Reference:** Thin read-only GraphRAG retrieval adapter (~500 LOC). Assumes knowledge graph exists. Single concern: index → MAF context injection.
- **Ours:** Comprehensive memory engine (~8,000+ LOC across 6 packages). Full CRUD. Three memory tiers. Entity extraction + resolution. Framework-agnostic core + adapters.

**Comparison published:** `docs/neo4j-maf-provider-comparison.md` — professional document for Neo4j team, ecosystem partners.

**Key Distinction:**
- Reference is READ-ONLY (all Neo4j calls use `RoutingControl.Readers`)
- Ours is FULL CRUD (messages, entities, facts, preferences, traces)
- Reference is MAF-coupled; ours is framework-agnostic at core

**Substitution Viability:** ✅ YES
- Could replace reference in MAF deployments (drop-in interface)
- Adds automatic extraction, entity resolution, reasoning memory
- Mid-term investment: schema migration + data backfill (2 weeks + optional)
- Ideal for new agents starting fresh; hybrid mode for existing deployments

---

### L2: Relationship Types Missing in .NET Implementation (2025-01-29)

**Finding:** From Sebastian's trace (F1 finding). Python reference has 6+ cross-memory relationship types that our .NET implementation lacks.

**What We're Missing:**
- `INITIATED_BY` — reasoning trace → initiating message
- `TRIGGERED_BY` — tool call → triggering message
- `HAS_TRACE` — conversation → reasoning trace
- `EXTRACTED_FROM` — entity/fact → source message (as graph rel)
- `ABOUT` — preference → associated entity

**Impact:** Context assembly cannot traverse across memory tiers via graph relationships. Limits graph-native query power.

**Recommendation:** Marked as `F1: HIGH` priority. Add to `SchemaBootstrapper` and repository UpsertAsync/AddAsync methods. Single highest-impact improvement.

---

### L3: Write-Layer Optimization Gaps (2025-01-29)

**From Sebastian's analysis (Gap 1–3):**

**Gap 1: No `UpsertBatchAsync`** — Extraction pipeline currently does N individual `UpsertAsync` calls. At scale (1K messages × 10 entities each = 10K entities), this is 20K DB round-trips.
- Fix: `UpsertBatchAsync(IReadOnlyList<Entity>)` with UNWIND Cypher
- Priority: MEDIUM-HIGH (critical for production throughput)

**Gap 2: No `DeleteAsync` for preferences** — Users can add preferences but not retract. Conflicting preferences accumulate. No lifecycle management.
- Fix: Add `Task DeleteAsync(string preferenceId, CancellationToken)` to `IPreferenceRepository`
- Priority: MEDIUM (correctness bug for conversational agents)

**Gap 3: No re-embedding after `MergeEntitiesAsync`** — When entities merge, aliases added to target but embedding unchanged. Vector search misses on merged entities using alias-form queries.
- Fix: After merge, re-embed using combined name + aliases text
- Priority: LOW-MEDIUM (improves vector recall quality)

**Status:** All three documented and ready for Phase 2 prioritization.

---

### L4: Cypher Query Centralization Opportunity (2025-01-29)

**From Sebastian's analysis (F3 finding).**

**Current state:** 9 repository classes have inline Cypher queries. No centralized constants file.

**Reference approach:** Python's `graph/queries.py` centralizes 60+ Cypher queries.

**Recommendation:** Create `Neo4j/Queries/CypherQueries.cs` with organized constants. Enables Cypher review without implementation logic, reduces duplication, improves maintainability.

**Example:**
```csharp
public static class CypherQueries
{
    public const string UpsertEntity = """
        MERGE (e:Entity {id: $id})
        ON CREATE SET e += $props, e.createdAtUtc = $createdAtUtc
        ON MATCH SET e += $props
        """;
}
```

---

### L5: IRetriever Reuse Pattern (2025-01-29)

**Key insight:** We reuse the reference project's `IRetriever` interface in `Neo4jGraphRagAdapter` but reimplement the three retrievers (Vector, Fulltext, Hybrid).

**Why reuse interface, not classes?**
- Reference project is a local `ProjectReference`, not NuGet package
- When we publish to NuGet, we need to either:
  1. Distribute reference project separately + take package dependency, OR
  2. Own our own IRetriever/retriever impls (current approach)

**Current approach is correct:** Our `AdapterVectorRetriever`, `AdapterFulltextRetriever`, `AdapterHybridRetriever` are near-verbatim copies of reference with modified constructors and dependency injection.

**Future option:** If reference becomes published NuGet package, could import directly. For now, local project reference + reimplementation is the right call.

---

### L6: Framework-Agnostic Core as Competitive Advantage (2025-01-29)

**Key insight:** Zero framework dependencies in Abstractions is not just clean architecture—it's product differentiation.

**Example: Future portability**

Reference project: If GraphRAG SDK changes → require update to `Neo4jContextProvider`.

Our project: If MAF API changes → only `Neo4jMemoryContextProvider.cs` (300 LOC) needs update. Core, Neo4j, Abstractions unchanged.

**Business value:** Can reach multiple frameworks with ONE core implementation:
- MAF: `Neo4jMemoryContextProvider`
- GraphRAG MinimalOrchestration: `GraphRagMemoryAdapter` (not yet built, but possible)
- FastAPI (Python): Call Abstractions via gRPC/IPC, zero MAF coupling
- MCP Server: Expose memory as tools for Claude

**Emphasize in ecosystem messaging:** "Single memory engine, multiple framework integrations."

---

### L7: Post-Gap-Closure Documentation Audit (2025-07-24)

**Finding:** After the gap closure sprint (Waves A–C), 9 out of 13 documentation files had stale information.

**Key stale items found:**
- Test counts: Multiple docs showed 398 or 349 — actual is 1058
- MCP tool counts: Docs showed 14 or 18 — actual is 21 tools, 6 resources, 3 prompts
- `memory_get_observations` was listed as "not implemented" in 3 places — it IS implemented in `ObservationTools.cs`
- Phase roadmap in `architecture.md` showed Phase 1 "in progress" and Phases 2–6 "not started" — all complete
- datetime migration in `architecture.md` still recommended as future work — completed in G1
- Node labels parity in `schema.md` showed 91% — should be 100% (Schema node has indexes)
- `task_embedding_idx` listed as missing in `implementation-status.md` — it exists in SchemaBootstrapper

**Lesson:** Documentation lags behind code changes by default. After every sprint, a targeted doc sweep is needed.

**Files updated:** README.md, docs/architecture.md, docs/implementation-status.md, docs/schema.md, docs/feature-record.md, docs/python-dotnet-comparison.md, docs/architecture-assessment.md, docs/package-strategy-and-features.md

### L8: Post-Sprint Documentation Audit — Process and Finding (2026-04-15)

**Session:** arch-review-session (parallel with Deckard, Holden)

**Scope:** Comprehensive audit of all 12 documentation files for stale claims and correctness.

**Findings:**

1. **Stale Parity Scorecard** (python-dotnet-comparison.md)
   - Item #18 (extraction pipeline): marked as gap but fully implemented
   - Item #19 (MCP resources/prompts): marked as gap but fully implemented
   - Item #11 (memory_get_observations): marked as gap but fully implemented
   - Actual functional parity: ~97% (not ~91%)

2. **Feature Completeness Under-Reported**
   - Background enrichment queue: EXISTS (not noted as implemented)
   - Context compression: EXISTS (not noted as implemented)
   - Streaming extraction config: EXISTS (not noted as implemented)

3. **Documentation Drift Pattern Confirmed**
   - 9 of 13 docs files had stale numeric claims (test counts, tool counts, parity %)
   - Post-sprint docs updates do not persist automatically
   - Manual re-application and disk-persistence verification required

**Decision Proposed:**

- D-DOC1: Post-Sprint Documentation Audit Process
  - Verify all numeric claims against actual code
  - Check status trackers against git log
  - Search for "not implemented" markers and validate
  - Update implementation-status.md after each sprint

**Implementation Challenge:**

Joi's bulk docs update was reported as successful but edits did not persist to disk. All changes reverted.

**Status:** Audit process documented in decisions.md. Documentation updates should be re-applied with disk persistence verification.

---

### L10: Improvement Suggestions & Docs Completeness Audit (2026-07-24)

**Session:** Full docs audit requested by Jose Luis Latorre Millas

**Scope:** Verified all S1–S15 improvement suggestions and reviewed all 13 docs files for currency.

**Improvement Suggestions Findings:**
- S1–S6, S8, S10–S15: ALL verified ✅ COMPLETE against actual code
- S7 (ISP split): ⚠️ Not Recommended — still correct; IEntityRepository is cohesive, pragmatic tradeoff accepted
- S9 (Truncation strategy extraction): 📅 Deferred — ACCURATELY deferred; MemoryContextAssembler.cs still has inline switch (lines 254–263), no ITruncationStrategy interface
- C1–C10 (Creative ideas): Conceptual proposals only — not implemented, correct status
- **improvement-suggestions.md should NOT be deleted** — C1–C10 and Section 6 ("What AI Models Want") contain forward-looking roadmap and design perspective found nowhere else

**README.md Stale Claims Found:**
- "10 packages" → should be 11 (SemanticKernel + meta-package shipped after this section was written)
- "1,211 unit tests" → should be ~1,438 (per architecture-review-assessment Appendix A)
- "MCP Server with 21 tools" in project status paragraph → should be 28
- Missing `Neo4j.AgentMemory.SemanticKernel` row in package table

**Other Stale Docs:**
- `architecture-review-assessment.md`: Header says "9 packages, 1,211 tests" but Appendix A correctly says "11 packages, 1,438" — internal inconsistency; SemanticKernel listed as "Future" when it shipped
- `package-strategy-and-features.md`: Pre-merger diagram (GraphRagAdapter as separate, neo4j-maf-provider dependency) — largely superseded
- `refactoring-plan.md`: Final test count says 1,211 — stale

**Missing Docs (high priority):**
- `docs/getting-started.md` — no onboarding path exists; critical DX gap
- `CHANGELOG.md` — no version history
- `CONTRIBUTING.md` — placeholder note in README never followed up

**Decision written to:** `.squad/decisions/inbox/joi-docs-recommendations.md`

---

### L9: Post-Implementation Documentation Synchronization (2025-01-29)

**Session:** Post-MEAI migration + ToolCallStatus fix + Extraction package decision sprint

**Scope:** Synchronized 6 documentation files after three major implementation changes:
1. MEAI migration (IEmbeddingProvider → IEmbeddingGenerator<T>)
2. ToolCallStatus enum extended to 6 values
3. Extraction package consolidation decision

**Changes applied:**

1. **docs/schema.md** — 3 fixes:
   - ToolCallStatus enum: Updated from 4 to 6 values (Pending, Success, Error, Cancelled, Failure, Timeout)
   - Property indexes: Added `fact_category` and `reasoning_step_timestamp` as .NET extensions (14 total, not 12)
   - Schema indexes: Added note that .NET uses `schema_version_idx` while Python uses `schema_id_idx`

2. **docs/python-dotnet-comparison.md** — 4 updates:
   - Test file count: "55+" → "111+ test class files (103 unit + 8 integration)"
   - MCP tool count: "21" → "28" (13 more than Python's 15)
   - Added MEAI native integration note under .NET advantages
   - Updated date stamp to reflect MEAI migration completion

3. **docs/feature-record.md** — 2 updates:
   - Header: "1058 tests" → "1059 tests", "55+" → "111+ test class files"
   - MCP Server feature: "21 tools" → "28 tools"

4. **README.md** — 2 updates:
   - MCP tool count: "21" → "28" (appears twice in document)
   - Updated MCP layer description to reflect 28 tools

5. **docs/meai-ecosystem-analysis.md** — 3 major sections rewritten:
   - "Our Current Usage" section: Marked migration as COMPLETED, deleted IEmbeddingProvider references
   - "Split Personality Problem" section: Changed from "We have..." to "Previously we had... RESOLVED ✅"
   - Migration Path section: Marked all phases as COMPLETED
   - Proposed Abstractions.csproj: Changed to "NOW IMPLEMENTED ✅"
   - neo4j-maf-provider comparison table: Updated embedding row to show migration completed

6. **docs/code-review-findings.md** — 2 fixes:
   - ToolCallStatus design difference: Changed from "⚠️ Gap" to "✅ RESOLVED"
   - Section 2.9: Replaced gap description with resolution note
   - Schema parity summary: Updated from "~99% with ToolCallStatus gap" to "~99% (only Schema index difference)"

**Key lesson:** Post-implementation doc sweeps must verify **actual code state**, not rely on previous documentation claims:
- Used grep to count MCP tools (`Tool(` attributes) → 28 actual (not 21)
- Counted test files via PowerShell → 111 class files (not 55+)
- Viewed ToolCallStatus.cs directly → 6 enum values confirmed
- Checked SchemaBootstrapper.cs → 15 PropertyIndexes array items (not 12)

**Pattern confirmed:** Every major implementation sprint needs a corresponding doc update task. Numeric claims drift by default.

---

### L11: Full Docs Inventory & Freshness Audit (2026-07-25)

**Session:** Comprehensive docs review requested by Jose Luis Latorre Millas
**Model used:** Claude Sonnet 4.6 (highest deep-thinking model available)

**Scope:** All files in docs/, root-level .md files, and samples/*/README.md

---

#### KEY FINDING: The "28 tools" claim is WRONG across 4 documents

- **Actual code count:** 21 `[McpServerTool]` attributes across 7 tool class files in `src/Neo4j.AgentMemory.McpServer/Tools/`
- **L9 sprint introduced incorrect numbers:** L9 history claims "Used grep to count MCP tools (`Tool(` attributes) → 28 actual" — this was a false positive from a broad grep pattern matching non-tool code. The docs were updated FROM the correct 21 to an incorrect 28.
- **Files with wrong "28 tools" claim:** README.md (×2), architecture-review-assessment.md, feature-record.md, python-dotnet-comparison.md
- **Files with correct "21 tools":** implementation-status.md, architecture.md, the actual code

**Lesson:** When updating docs from code counts, always use the most specific attribute name (`[McpServerTool]`) not a broad substring (`Tool(`).

---

#### FULL DOCS AUDIT RESULTS

**Healthy / Current (no action needed):**
- `docs/parity-assessment.md` — Fresh (July 2026). Authoritative. ✅
- `docs/schema.md` — Current. "Definitive Schema Reference." ✅
- `docs/design.md` — Domain model stable. Accurate. ✅
- `docs/improvement-suggestions.md` — C1–C10 forward-looking roadmap valid. ✅
- `docs/package-strategy.md` — Decision made, rationale captured. ✅
- `samples/*/README.md` — Both accurate and up to date. ✅

**Need targeted updates:**
- `README.md` — Fix 28→21 tools; "License to be defined" (Apache 2.0 is in LICENSE); "Planned capabilities" should read as "Implemented"; Contributing "will be added" stale
- `docs/architecture-review-assessment.md` — Fix 28→21 tools; per-project table shows 9 packages (should be 11)
- `docs/feature-record.md` — Fix 28→21 tools; test counts stale
- `docs/python-dotnet-comparison.md` — Fix 28→21 tools
- `docs/implementation-status.md` — Fix "10 packages" → 11; test counts stale; Phase 6 description still says "21 tools (6 core + 15 extended)" correct but narrative says Phase 6 completion had 398 tests — stale
- `docs/architecture.md` — Missing SemanticKernel package section; MCP package name wrong in "Future Adapter Packages" table

**Frozen/completed-work docs to archive:**
- `docs/refactoring-plan.md` — All 4 waves complete. Historical only.
- `docs/python-agent-memory-analysis.md` — Phase 1 Reference. Superseded by comparison + parity docs.
- `docs/cypher-analysis.md` — Earlier parity analysis. Superseded by parity-assessment.md (July 2026).

---

### L-latest: Improvement Ideas Backlog Created (2026-04-30)

**Task:** José requested a structured post-launch backlog preserving 8 deferred improvement ideas with expanded implementation sketches.

**File created:** `docs/Improvement-Ideas-Backlog.md`

**Items documented:**
1. Memory Conflict Detection + Provenance Scoring — `ConflictDetectionService`, `IConflictHandler`, `ProvenanceScore` on Fact nodes
2. GDS Integration (PageRank + Community Detection) — optional `Neo4j.AgentMemory.Analytics` package, GDS plugin required
3. Cross-Agent Memory Sharing — `SharedMemorySpace` concept, namespace-aware repositories, `MemoryNamespace` node type
4. Local Embedding Adapter (ONNX) — `Neo4j.AgentMemory.Embedding.Onnx`, `Microsoft.ML.OnnxRuntime`, `all-MiniLM-L6-v2`
5. Local NLP Extractors (GLiNER / ONNX NER) — `Neo4j.AgentMemory.Extraction.LocalNlp`, blocked on ecosystem maturity
6. Opik Observability Integration — `Neo4j.AgentMemory.Observability.Opik`, blocked on upstream .NET SDK
7. Full CLI Tool Feature Set — `export-memory`, `import-memory`, `stats`, `prune`, `search` commands
8. Additional Framework Integrations — AutoGen.NET = MAF (done), LangChain.NET (low ROI), Semantic Router (speculative)

**Key convention:** Items include implementation sketches with package names, interface names, and Cypher patterns — enough detail to restart without re-analysis. Promotion path documented at file end.
- `Agent-memory-for-dotnet-implementation-plan.md` (root) — All 6 phases done. Historical.

**Purpose-clarification docs:**
- `docs/maf-1.1.0-migration-guide.md` — External upstream reference, not project-specific. Add label.
- `docs/HotChocolate.Data.Neo4J-lessons-learned-and-ideas-to-apply.md` — Research spike. Add label.
- `Squad-Workshop.md` (root) — Squad framework workshop tutorial, not project doc. Should be in `.squad/`.

**Critical missing docs:**
- `docs/getting-started.md` — No developer onboarding path. 🔴 Critical DX gap.
- `CONTRIBUTING.md` — Never created despite README forward-reference.
- `CHANGELOG.md` — No version history. Needed before NuGet release.

**Test count ground truth:**
- Grep of `[Fact]`/`[Theory]` attributes in tests/ = ~1,477 test methods
- Various docs claim: 1,059 / 1,124 / 1,211 / 1,438 / 2,040+ (all inconsistent)
- Recommend running `dotnet test --list-tests` for ground truth before updating docs

---

**Decision written to:** `.squad/decisions/inbox/joi-doc-audit.md`
**Skill pattern extracted to:** `.squad/skills/docs-freshness-audit/SKILL.md`

---

### L12: Doc Overhaul — Reorganization and Brittle-Count Removal (2026-07-25)

**Session:** Full doc overhaul requested by Jose Luis Latorre Millas

**Scope:** Restructure docs/ directory, fix urgent stale claims, remove brittle counts, update now.md.

**Actions taken:**

1. **Created `docs/archive/`** — moved `refactoring-plan.md`, `python-agent-memory-analysis.md`, `cypher-analysis.md` there. Added `README.md` explaining each.

2. **Created `docs/reference/`** — moved `HotChocolate.Data.Neo4J-lessons-learned-and-ideas-to-apply.md`, `maf-1.1.0-migration-guide.md`, `maf-audit-review-and-improvement-plan.md` there. Added `README.md`.

3. **README.md fixes:**
   - "28 tools" → "21 tools" (both occurrences)
   - "License to be defined" → "Apache 2.0"
   - ".NET 8" → ".NET 9"
   - `GraphRagAdapter` package row removed (package doesn't exist; GraphRAG is in Neo4j package)
   - "2,040+ tests passing" brittle count removed — replaced with durable wording
   - "Planned capabilities" banner added (all implemented)
   - "will be added" contribution text updated
   - Package count claim ("11 packages") removed in favour of listing the actual packages

4. **architecture.md fixes:**
   - §3.4.2 `GraphRagAdapter` ghost section replaced with accurate description of GraphRAG in Neo4j package
   - §3.4.6 "Future Adapter Packages" table corrected to reflect McpServer as the shipped package
   - §5 Boundary rule verification updated: "zero M.E.AI matches" stale claim corrected to reflect D-AR2-1 exception
   - §4.5 `task_embedding_idx` "Known Gap" → corrected to "note: exists in SchemaBootstrapper"
   - §7/§8 "1058 unit tests" count removed, replaced with durable wording
   - §8 "ships 10 packages" → "10 packages plus meta-package"
   - §9 package isolation audit updated: GraphRagAdapter row → SemanticKernel row; "AgentFramework + GraphRagAdapter" merger analysis → "AgentFramework + SemanticKernel"
   - §9.2 dependency graph updated to show SemanticKernel instead of GraphRagAdapter
   - §9.4 "Keep 10 Packages" → "Keep Current Package Topology"
   - §9.5 consumer matrix updated: GraphRAG uses Neo4j package; SK row added
   - §10 DateTime section completely rewritten: was describing the problem as future work, now describes completed G1 state
   - §1 "What It Does NOT Do" — removed erroneous "MCP Server" bullet (it IS implemented)

5. **design.md fixes:**
   - Service catalog row #11: `IEmbeddingProvider` → `IEmbeddingOrchestrator` (MEAI migration)
   - `ToolCallStatus`: 4 values → 6 values (Pending, Success, Error, Cancelled, Failure, Timeout)

6. **implementation-status.md fixes:**
   - "10 packages" → "10 packages plus meta-package"
   - Epic 2 service interface list: `IEmbeddingProvider` note added (deprecated in favour of MEAI)
   - Epic 8 stubs: `StubEmbeddingProvider` → `StubEmbeddingGenerator`
   - Document Inventory table: removed dead `neo4j-maf-provider-analysis.md` reference; updated with archive/reference structure

7. **Agent-memory-for-dotnet-implementation-plan.md:** Added historical banner at top.

8. **.squad/identity/now.md:** Fully updated to reflect current repo state with ground-truth key facts.

**Key ground-truth facts confirmed:**
- 21 MCP tools (confirmed via `[McpServerTool]` grep)
- 11 .csproj files (10 packages + 1 meta), no GraphRagAdapter package in src/
- .NET 9.0 (Directory.Build.props)
- `IEmbeddingProvider` removed — replaced by `IEmbeddingGenerator<string, Embedding<float>>` via `IEmbeddingOrchestrator`
- ~1,477 test methods ([Fact]/[Theory] grep) — deliberately NOT hard-coded in docs

**Process lesson:** The "Deleted Package Ghost" anti-pattern (GraphRagAdapter section in architecture.md) and "Sprint Update False Positive" (28 tools from broad grep) are both recurring failure modes in this codebase. Always verify package existence against src/ filesystem, and use `[McpServerTool]` exact attribute rather than broad `Tool(` patterns.

---

---

### L7: Batch Doc Fix — API Facts and Archiving Strategy (2026-04-30)

**Task:** Full documentation batch fix across 6 categories.

**Key API facts confirmed from `src/Neo4j.AgentMemory.Abstractions/Services/`:**
- `IMemoryService` is the primary facade — `AddMessageAsync`, `AddMessagesAsync`, `RecallAsync`, `RecallAsOfAsync`, `ExtractAndPersistAsync`, `ClearSessionAsync`
- `IShortTermMemoryService` — `AddMessageAsync`, `GetRecentMessagesAsync`, `SearchMessagesAsync`, `ClearSessionAsync`
- `ILongTermMemoryService` — `AddEntityAsync`, `AddFactAsync`, `AddPreferenceAsync`, `AddRelationshipAsync` + search/get variants
- `IReasoningMemoryService` — `StartTraceAsync`, `AddStepAsync`, `RecordToolCallAsync`, `CompleteTraceAsync`, `SearchSimilarTracesAsync`
- DI registration: `AddNeo4jAgentMemory()`, `AddAgentMemoryCore()`, `AddAgentMemoryFramework()`, `AddGraphRagAdapter()`, `AddAgentMemoryObservability()`
- Embedding abstraction: `IEmbeddingGenerator<string, Embedding<float>>` from MEAI — `IEmbeddingProvider` is deleted; stub is `StubEmbeddingGenerator`

**README was already correct** — prior fix sprint had already updated it to use real API surface.

**Docs archived (8):** meai-ecosystem-analysis.md, parity-assessment.md, package-strategy.md, python-dotnet-comparison.md, architecture-review-assessment.md, improvement-suggestions.md, implementation-status.md, feature-record.md — all completed planning artifacts.

**Docs created:** docs/getting-started.md, CONTRIBUTING.md, CHANGELOG.md

---

### L8: Reviewer-Confirmed API Facts — Revision Pass (2026-04-30)

**Task:** Applied Roy, Gaff, Pris review feedback across 6 documents.

**Confirmed API facts from source verification (`src/`):**

- `Neo4jOptions` properties: `Uri`, `Username`, `Password`, `Database` (default `"neo4j"`), `MaxConnectionPoolSize`, `ConnectionAcquisitionTimeout`, `EncryptionEnabled`, `EmbeddingDimensions` — **no** `ConnectionUri` or `AuthToken`
- Schema bootstrapper DI interface: `ISchemaBootstrapper` (`Neo4j.AgentMemory.Neo4j.Infrastructure`); concrete `SchemaBootstrapper` is never directly registered — always resolve via `GetRequiredService<ISchemaBootstrapper>()`
- `RecallResult` shape: single `Context` property of type `MemoryContext` — **no** `Messages` or `Entities` directly on `RecallResult`
- `MemoryContext.RecentMessages` / `RelevantEntities` etc. are `MemoryContextSection<T>`; items are accessed via `.Items` (`IReadOnlyList<T>`)
- `Message` is a `sealed record` with 6 required properties: `MessageId` (string), `ConversationId`, `SessionId`, `Role`, `Content`, `TimestampUtc` (DateTimeOffset) — omitting any causes compile error
- `Neo4j.AgentMemory.Abstractions` has **one** NuGet dependency: `Microsoft.Extensions.AI.Abstractions 10.4.1` (approved, D-AR2-1) — the §3.1 architecture table "None" was a documentation error
- `AddGraphRagAdapter()` is a **separate** DI call from `AddNeo4jAgentMemory()` — callers wanting `IGraphRagContextSource` must call both

### Doc Sprint Completion — Cross-Team Coordination (2026-04-30)

**Team:** Joi, Deckard, Roy, Gaff, Pris, Rachael  
**Orchestration:** Scribe

**Outcomes:**
- ✅ All documentation fixed: README, architecture.md, design.md, implementation-status.md
- ✅ New onboarding docs: getting-started.md (full DI/MAF/SK guide), CONTRIBUTING.md, CHANGELOG.md
- ✅ Archive: 8 completed planning docs, 18 old decisions
- ✅ Reviews: 2 full editorial passes + targeted Neo4j/SK validation
- ✅ Approvals: 4/6 documents signed off (pris)

**Key Coordination:**
- Joi fixed docs; Roy validated API parity; Gaff validated Neo4j paths; Pris approved 4/6
- All flagged issues (joi-16 parity, gaff-4 neo4j, rachael-3 sk-names) documented in decisions.md
- Scribe: archived 18 old decisions (D1–D18, G8), merged 11 decision records from team inboxes
- Next: Address pris/rachael flagged issues in targeted follow-up

**Reference:** .squad/orchestration-log/2026-04-30T19-43-32-doc-sprint.md
