# Joi — History

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

