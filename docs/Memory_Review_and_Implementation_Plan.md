# Memory Review & Implementation Plan

> **Author:** Architecture review (Claude) · **Date:** 2026-06-05 · **Branch:** `remediation/analysis-review-hardening`
> **Scope:** A through-and-through review of AgentMemory-for-.NET — what is done, what is pending, and where the gaps are — with a deep dive and concrete implementation plan for the headline topic: **multi-user / multi-session memory isolation** (user-scoped memory, with an *optional* shared/global scope).
>
> **Update 2026-06-06 — ✅ multi-user isolation COMPLETE (R1 + R1b + R2).** R1 core (I1–I9) plus a follow-up multi-agent review and remediation (**IC1–IC8**) closed **all** owner-scoping leaks: vector recall, non-vector lookups (subject/triple/name/type/category/location), GraphRAG (all 4 retrievers), ReasoningTrace, relationships, temporal `AsOf`, and the LLM-invokable facade tools are now owner-scoped (optional shared/global). The two R1b store-tier defects are fixed (IC6 `AsyncLocal` store context; `StoreDatabaseNaming` collision hash). The upstream ports **dedup-on-create** (PR #97) and **consolidation/hygiene** (PR #113) landed in parallel. Verified by full unit + Neo4j integration suites. The narrative below (§1, R7) was written against the *original* gap and is kept for context; **Part II §II.5 is the source of truth for completion status.** Upstream fix proposed at neo4j-labs/agent-memory#137; see [`Remaining_Work_Roadmap.md`](Remaining_Work_Roadmap.md), [`schema-parity-assessment.md`](schema-parity-assessment.md), [`neo4j-pr-howto.md`](neo4j-pr-howto.md).
>
> **Update 2026-06-06 (release prep) — Phase-2 quick wins shipped + release cut.** Two more upstream-parity items landed: **vector-index dimension validation** (`EmbeddingDimensionMismatchException`, fail-fast on embedder switch) and **`:TOUCHED` reasoning-audit edges** (`(:ReasoningStep)-[:TOUCHED]->(:Entity)`) — both in Part II §II.4. CHANGELOG was cut to `[0.1.0-preview.1]` and the `v0.1.0-preview.1` tag created **locally (not pushed)**; the PR to `main` is **not yet opened** (branch **~63 commits ahead of `main`** as of 2026-06-07 — the "release cut" predates a large amount of subsequently-landed work; see the Review Annex §III). Tests now **~2,274 unit + ~128 integration green** (incl. live MigrationRunner + Enterprise store-isolation E2E, the latter catching a real `CREATE DATABASE` quoting bug; the entity auditability/feedback "trust" surface and the `agentmemory` CLI landed — the CLI E2E catching a real meta-DI gap). Code-verified audits refreshed the pending list — **the live cleanup view is Part II §II.6; the higher-ambition "good→great" roadmap is §II.7.**

---

## 1. Summary & recommendation

AgentMemory-for-.NET is **feature-complete for v1 and well-hardened**: 11 packages + an `agentmemory` ops CLI, ~2,274 unit + 31 SK + ~128 integration + 3 performance tests green, real CI, package-boundary guard tests, Testcontainers integration, 9 samples, a 4-phase remediation + adversarial review pass all closed, a clean MAF **1.9.0** migration, and a working file-based migration runner + schema bootstrapper. Code quality is high and the architecture is clean (centralized Cypher constants, a `CypherBuilder`, role-split service interfaces).

**The one architecturally significant gap described below — multi-user isolation of long-term knowledge — has been CLOSED (2026-06-06; R1 + R1b + R2 via I1–I9 + IC1–IC8).** The two paragraphs that follow describe the *original* gap and the plan to fix it, retained for context. Every recall/lookup/GraphRAG/trace/relationship/`AsOf`/facade-tool path is now owner-scoped with an optional shared/global scope, the `MemoryScope`/`OwnerId` API surface is shipped and forward-compatible, and the per-application store tier (R1b) works. **See Part II §II.5 for the live completion status.**

~~There is **one architecturally significant gap**~~ *(now closed — see above)*: long-term knowledge *was* a single global graph with no notion of an owner. A user's extracted **entities, facts, preferences and relationships** (and reasoning traces) were stored ownerless, and every semantic recall searched *all* of them with no user filter, so in a multi-user deployment one user's recall could surface another user's facts and preferences, and a fact extracted for user A could be silently overwritten by user B. Short-term *messages* were correctly isolated by `session_id` — the proven pattern the fix extended.

**Recommendation (headline) — ✅ DONE:** user-scoped memory with an optional shared/global scope was implemented as the HIGH-priority workstream and **landed before the first public NuGet release**, so v1's API surface is forward-compatible. The fix was additive (reused the session-filtered search pattern, the migration runner, and the guard tests; no architectural rework). Remaining pending items (NuGet release — now *unblocked*; streaming-extraction DI wiring — now *registered*; CLI, GDS, BenchmarkDotNet, S9) are deferred-by-decision and non-blocking; the live phased view is in Part II §II.5.

The design below introduces a nullable `owner_id` per record (**`NULL` = shared/global**, which makes all existing data and all unscoped callers behave exactly as today — zero-data-loss, backward-compatible) plus a `MemoryScope` that reads "my records **OR** shared records," filtered into vector search via the same over-fetch-then-`WHERE` technique already used for message search. A complementary **storage tier** ([§R1b](#r1b--application--memory-store-isolation-the-storage-tier-above-the-owner)) adds an optional **application / memory-store id** *above* the owner — ideally a dedicated Neo4j **database per application** (Enterprise/AuraDB), with a shared-database + `owner_id` fallback for Community — giving the full hierarchy **store ⊃ owner ⊃ session**.

---

## 2. Index

| # | Topic | Priority |
|---|---|---|
| [R1](#r1--multi-user--multi-session-memory-isolation-headline) | Multi-user / multi-session memory isolation **(headline)** | 🟥 HIGH |
| [R1b](#r1b--application--memory-store-isolation-the-storage-tier-above-the-owner) | Application / memory-store isolation (storage tier above the owner) | 🟥 HIGH |
| [R2](#r2--reasoning-trace-isolation-secondary-scope-gap) | Reasoning-trace isolation (secondary scope gap) | 🟧 MED |
| [R3](#r3--nuget-release-sequencing) | NuGet release sequencing | 🟥 HIGH |
| [R4](#r4--streaming-extraction-doc-drift--di-wiring) | Streaming extraction (doc drift + DI wiring) | 🟧 MED |
| [R5](#r5--code-quality--overall-status) | Code quality / overall status assessment | 🟩 INFO |
| [R6](#r6--deferred-backlog) | Deferred backlog: CLI, GDS, BenchmarkDotNet, S9 | ⬜ LOW–MED |
| [R7](#r7--done-vs-pending) | Done vs pending (full ledger) | 🟩 INFO |

---

## 3. Status tracking table

| ID | Topic | Area | Priority | Status | Effort | Recommendation |
|---|---|---|---|---|---|---|
| R1 | Multi-user memory isolation (`owner_id` + `MemoryScope`, optional shared) | core / neo4j / abstractions / adapters | 🟥 HIGH | ✅ **Done** — ✅I1 abstractions · ✅I3 scoped read path · ✅I4 assembler scope · ✅I5 write path (`owner_id` persisted+read-back on Fact/Entity/Preference; Fact MERGE keyed by `owner_key`; extraction stamps owner from `ExtractionRequest.UserId`) · ✅I6 owner indexes (`fact/entity/preference/trace_owner_idx` in bootstrap) + non-backfilling migration `0002_owner_scope.cypher` (multi-statement runner; `.cypher` ships to output) · ✅I8 adapters surface identity: MAF context + chat-history providers extract `user_id`/`application_id` from the StateBag → `RecallRequest.UserId`/`ExtractionRequest.UserId` (+ optional writable store-context routing); `AgentSession.WithMemoryIdentity(...)` helper; MCP recall already scoped + add_entity/fact/preference now stamp `OwnerId`; SK `recall` gains `userId` · ✅I9 isolation tests: scope-guard unit tests (owner clause present only when scoped; over-fetch topK) + 6 Neo4j integration tests **passing** (A≠B isolation, shared visible to all, unscoped union, same-triple-different-owner = distinct nodes, same-triple-same-owner dedup, over-fetch no-starvation) — **read+write loop closed, indexed, wired through all three adapters, and verified end-to-end**. ✅ **R1 COMPLETE** — all secondary leaks closed in **IC1–IC8** (reasoning traces, relationships, GraphRAG, non-vector lookups, temporal `AsOf`, facade tools); ~2,274 unit + ~128 integration tests green (as of 2026-06-06). Full IC detail + remaining design decisions in **Part II (§II.5)**. | L | ✅ Landed before first NuGet publish |
| R1b | Application / memory-store isolation (`ApplicationId` → Neo4j database; shared-db fallback) | core / neo4j / infra | 🟥 HIGH | ✅ **Done** — ✅I2 scaffolding · ✅I7 store routing: store-aware `Neo4jSessionFactory` (per-call DB resolution from `IMemoryStoreContext`+`MemoryStoreOptions`; explicit-DB overload), `StoreDatabaseNaming` (resolve + Neo4j-legal `Sanitize` w/ collision-safe hash on truncation), `IMemoryStoreProvisioner`/`Neo4jMemoryStoreProvisioner` (`CREATE DATABASE … WAIT` on `system`, per-store bootstrap, cache, **actionable `NotSupportedException` on Community**), DI wired (singleton context+provisioner, optional `configureStore`). **`SharedDatabase` default reproduces single-store behavior exactly.** ✅ I8 surfaced `application_id` via the StateBag; ✅ IC6 fixed the captive-singleton store-routing defect (`AsyncLocal` store context). **R1b COMPLETE.** | L | `DatabasePerApplication` (Enterprise/Aura) + provisioner; `SharedDatabase`+`owner_id` on Community; §R1b |
| R2 | Reasoning-trace (+ relationship) isolation | core / neo4j | 🟧 MED | ✅ **Done** — folded into R1: **IC1** ReasoningTrace owner write+recall+delete-decision, **IC2** Relationship edge `owner_id` + `rel_owner_idx` + migration 0003 | M | Done via IC1/IC2 |
| R3 | NuGet release sequencing | packaging / ci | 🟥 HIGH | 🟢 **Cut, awaiting push (2026-06-06)** — `NUGET_API_KEY` set; CHANGELOG cut to `[0.1.0-preview.1]`; all 11 packages pack cleanly; **annotated tag `v0.1.0-preview.1` created locally (NOT pushed)**. Remaining: open PR `remediation/analysis-review-hardening`→`main` (**~63 commits ahead of `main` / 64 ahead of `origin/main` as of 2026-06-07**), then `git push origin v0.1.0-preview.1` to trigger `squad-release.yml`. ids/version freeze on first publish. | S | Push tag to publish (irreversible) — see §II.6 |
| R4 | Streaming extraction DI wiring | core | 🟧 MED | ✅ **Done (2026-06-06)** — `IStreamingExtractor` registered in `AddAgentMemoryCore` (was intentionally held back until R1 landed). It's a text→chunks→entities helper; owner stamping happens at persistence via `ExtractionRequest.UserId`. Interface name kept as `IStreamingExtractor` (final). | S | Registered; `nextsteps.md` updated |
| R5 | Code quality / overall correctness | all | 🟩 INFO | ✅ Strong | — | Reuse existing rails; no rework needed |
| R6a | CLI tool (`migrate` / `bootstrap` / `consolidate` / `decay`) | tooling | 🟧 MED | ✅ **Done (2026-06-06)** — `tools/AgentMemory.Cli` (`agentmemory`), commands migrate/bootstrap/consolidate[--apply]/decay; conn from CLI opts / `Neo4j:*` / `NEO4J_*`; 15 unit + live-Neo4j E2E. Surfaced+fixed the meta-DI `IClock`/`IIdGenerator` gap. `IsPackable=false` (excluded from the `src/*` release pack). See §II.7. | M | Done — ops convenience; not a publish blocker |
| R6b | GDS analytics package | analytics | ⬜ LOW–MED | ❌ Not started | M | Defer (post-release); must respect `owner_id` (R1 landed) |
| R6c | BenchmarkDotNet harness | testing | ⬜ LOW | ❌ Not started | M | Defer; add an owner-filtered vector-search benchmark to tune `OverFetchFactor` |
| R6d | S9 truncation-strategy refactor | core | ⬜ LOW | ❌ Not started | S | Defer (post-release); R1 assembler change already landed |
| R7 | WorkflowMemory sample | samples | ⬜ LOW | ⏸ Deferred | S | Not part of the canonical memory pattern; optional |

Legend — Effort: S ≈ ≤1 day, M ≈ 2–4 days, L ≈ 1–2 weeks. Status: ✅ done · 🟡 partial · ❌ not started · ⏸ deferred.

---

## R1 — Multi-user / multi-session memory isolation (headline)

### R1.1 The problem, in the user's words
*"If I'm a user talking to the AI and it stores the memory, I don't want another user to log in and see what we talked about. I want an ID that identifies this knowledge and assigns it to the user — and this should be optional, as maybe I want the knowledge to be general for everybody to consult."*

### R1.2 Current state — confirmed gap (model → write → read → API → adapter)

Every layer was verified against the code (and three adversarial refutation attempts failed):

1. **Model.** `Entity`, `Fact`, `Preference`, `Relationship` (`src/AgentMemory.Abstractions/Domain/LongTerm/*.cs`) declare **no** `OwnerId`/`UserId`/`TenantId`. `SchemaConstants.Properties` (`SchemaConstants.cs:120-328`) has no owner property; the only user-ish field anywhere is `session_id` on the short-term `Conversation`/`Message`. `ReasoningTrace` has `SessionId` but no user; `ReasoningStep` has neither. `UserId` *does* exist — but only on **request/short-term** types (`RecallRequest.cs:18`, `ExtractionRequest.cs:21`, `Conversation.cs:21`, `SessionInfo.cs:16`, `GraphRagContextRequest.cs:16`).
2. **Write.** No owner is ever persisted. Worse, `FactQueries.Upsert` (`FactQueries.cs:13-33`) `MERGE`s on `{subject, predicate, object}`, so the **same triple from two users collapses onto one shared node and the last writer wins** (cross-user bleed + provenance loss). `Entity`/`Preference`/`Relationship` `MERGE` on their id with no owner. `SchemaQueries.cs` defines no per-user index or ownership edge.
3. **Read.** The three semantic searches call `db.index.vector.queryNodes('{fact,entity,preference}_embedding_idx', $limit, $embedding)` and filter **only** on `score >= $minScore` (`FactQueries.cs:77-82`, `EntityQueries.cs:65-70`, `PreferenceQueries.cs:45-50`). The temporal `AsOf` variants add only time predicates (`TemporalQueries.cs:16-70`). Repository signatures (`Neo4jFactRepository.SearchByVectorAsync` `:153-177`) and service signatures (`ILongTermMemoryService.cs:28-78`) accept **only** `(embedding, limit, minScore)` — there is no parameter through which a filter could even be expressed.
4. **API / assembly.** `RecallOptions` (`RecallOptions.cs`) has no scope field. `MemoryContextAssembler.AssembleContextAsync` runs the long-term searches with no scope and forwards `request.UserId` **only** to GraphRAG (`MemoryContextAssembler.cs:262`). `MemoryExtractionPipeline.ExtractAsync` never reads `request.UserId`, so a supplied user id is **silently dropped** before any node is written.
5. **Adapter.** The MAF providers read only `session_id`/`conversation_id` from the `StateBag` and never a user id (`Neo4jMemoryContextProvider.cs:195-223`), with weak fallbacks (`agent.Id` or a random GUID). Nothing in the repo ever *writes* those `StateBag` keys, and `ISessionIdGenerator` (which can derive a session from a `userId`) is orphaned. MCP defaults `sessionId` to the shared constant `"default"`.

**Consequence:** long-term knowledge is a single global graph. Any session/user can retrieve any other user's entities, facts and **preferences** (the most personal type). This is a deliberate *omission* (the spec only ever required session isolation of short-term messages), so it cannot be patched in one place — it needs coordinated model + write-key + index + read-filter + API changes.

### R1.3 Design goals
- **Per-record ownership** with an **optional shared/global scope**.
- **Backward-compatible & lossless**: existing data and unscoped callers keep working unchanged.
- **Reuse proven rails**: the message-search session filter, the migration runner, the schema bootstrapper, the guard tests.
- **Forward-compatible API** so v1 can publish without a future breaking change.

### R1.4 Core concept — `owner_id` + `MemoryScope`
- Add **one nullable `owner_id`** per long-term/reasoning node. **`NULL` means shared/global** (visible to everyone). This makes every pre-migration node automatically global → zero-data-loss default, and lets a caller mark knowledge shared simply by not setting an owner.
- Reads accept a **`MemoryScope`** = "show me my owner's records **plus** (optionally) shared records":

```csharp
// src/AgentMemory.Abstractions/Options/MemoryScope.cs
public sealed record MemoryScope
{
    /// <summary>Owner whose records to include. Null = no owner filter (today's global behavior).</summary>
    public string? OwnerId { get; init; }
    /// <summary>Also include shared records (owner_id IS NULL). Default true.</summary>
    public bool IncludeShared { get; init; } = true;

    public static MemoryScope Global { get; } = new();                       // no filter
    public static MemoryScope For(string ownerId, bool includeShared = true)
        => new() { OwnerId = ownerId, IncludeShared = includeShared };
}
```
Semantics: `OwnerId == null` ⇒ no filter (current behavior, the safe default). `OwnerId` set ⇒ return rows where `owner_id == $ownerId` **OR** (`IncludeShared && owner_id IS NULL`).

> The owner is the **middle** tier of a three-tier scope model — **store / application ⊃ owner / user ⊃ session** — where the outermost *store* tier (an optional application / memory-store id, ideally its own Neo4j database) is specified in [§R1b](#r1b--application--memory-store-isolation-the-storage-tier-above-the-owner).

### R1.5 Implementation plan (phased, additive)

**Phase A — Abstractions (non-breaking).**
- Add `string? OwnerId { get; init; }` (default `null`) to `Entity`, `Fact`, `Preference`, `Relationship`, `ReasoningTrace`. Not `required`, so all existing initializers compile.
- Add `MemoryScope` (above) and `SchemaConstants.Properties.OwnerId = "owner_id"`.
- Add `MemoryScope? Scope { get; init; }` to `RecallOptions` (default `null` ⇒ Global).

**Phase B — Write path.**
- Thread `ExtractionRequest.UserId` into extraction: `MemoryExtractionPipeline.ExtractAsync` passes it to `PersistenceStage`, which stamps `OwnerId = request.UserId` on every entity/fact/preference/relationship before upsert. `null` ⇒ written as shared (preserves current behavior).
- **Fix the `Fact` MERGE key** (the worst bleed): include the owner in the merge identity so the same triple is distinct per owner. Because Neo4j `MERGE` treats a `NULL` pattern property specially, merge on a non-null **`owner_key = coalesce($ownerId, '*')`** stored alongside the real `owner_id`:
  ```cypher
  MERGE (f:Fact {subject:$subject, predicate:$predicate, object:$object, owner_key:$ownerKey})
  ON CREATE SET f.owner_id = $ownerId, ...
  ```
  `Entity`/`Preference`/`Relationship` keep merging on their unique id and just `SET f.owner_id`.
- Repository upsert param maps add `owner_id`/`owner_key`. MCP `MemoryAdd{Entity,Preference,Fact}` and the SK plugin gain an optional `userId` that flows to `OwnerId`.

**Phase C — Read / vector-search filtering (the Neo4j constraint).**
Neo4j's `db.index.vector.queryNodes` returns top-K by similarity and **cannot pre-filter on a property**; a naïve post-`WHERE` on a small K silently drops owned rows that fell outside K. The codebase already solves this for messages via **over-fetch + post-filter** (`MessageQueries.SearchByVector` builds `CypherBuilder.WithVectorSearch(...)` then `.And("node.session_id = $sessionId", when: hasSessionFilter)`). Apply the identical pattern to facts/entities/preferences:
```cypher
CALL db.index.vector.queryNodes('fact_embedding_idx', $fetchK, $embedding) YIELD node, score
WHERE score >= $minScore
  AND ( $ownerId IS NULL OR node.owner_id = $ownerId OR ($includeShared AND node.owner_id IS NULL) )
RETURN node, score ORDER BY score DESC LIMIT $limit
```
- **Over-fetch** to avoid starvation: `fetchK = max(limit * OverFetchFactor, limit + Floor)` (e.g. factor 5, floor 50), then `LIMIT $limit` *after* the `WHERE`. Expose `OverFetchFactor` on `LongTermMemoryOptions`.
- Add `MemoryScope? scope = null` to repository `SearchByVectorAsync`, the temporal `AsOf` queries, and `ILongTermMemoryService.Search*` (default `null` ⇒ Global ⇒ backward compatible).
- **The one line that makes `RecallRequest.UserId` finally meaningful:** in `MemoryContextAssembler`, build `scope = request.Options.Scope ?? (request.UserId is null ? null : MemoryScope.For(request.UserId))` and pass it to every long-term + reasoning search (both the live and `AsOf` paths).

**Phase D — Indexes.** Add property indexes (`fact_owner_idx`, `entity_owner_idx`, `preference_owner_idx`, `trace_owner_idx`) to `SchemaQueries.PropertyIndexes` so the owner predicate is cheap. Vector indexes are unchanged.

**Phase E — Adapters.**
- MAF: add `DefaultUserIdKey = "user_id"` to `AgentFrameworkOptions`; extend `ExtractIds` in both providers to read `userId` from the `StateBag` and set `RecallRequest.UserId`/`ExtractionRequest.UserId`. Ship a helper `AgentSession.WithMemoryIdentity(userId, sessionId)` (since nothing currently writes the StateBag) and a sample. Optionally wire the orphaned `ISessionIdGenerator` (PersistentPerUser) to derive `sessionId` from `userId`.
- MCP / SK: surface the optional `userId` (MCP tools already accept it on some paths — make it flow to scope).

**Phase F — Backward-compatible migration.** Use the existing file-based `MigrationRunner` (reads `Schema/Migrations/*.cypher`, version-tracked via `(:Migration {version})`). Add `0002_owner_scope.cypher`: create the four owner indexes `IF NOT EXISTS`; **do not backfill** (leaving `owner_id` `NULL` keeps all existing knowledge shared/global = safe, lossless). Provide a *separate, optional* operator backfill that derives `owner_id` from `Conversation.user_id` via the `EXTRACTED_FROM → Message → Conversation` provenance chain, for deployments that want to retroactively attribute existing data.

**Phase G — Tests.**
- *Unit*: `MemoryScope` predicate semantics; assembler builds scope from `RecallRequest.UserId` and `RecallOptions.Scope`; `PersistenceStage` stamps `OwnerId`; `null` user ⇒ shared.
- *Cypher snapshot*: `Search*ByVector` emits the owner OR-shared clause only when scoped, and over-fetches `fetchK` then limits.
- *Integration (Testcontainers)*: seed facts/entities/prefs for userA, userB, and shared(null); assert `recall(scope=userA)` returns **userA + shared and NEVER userB**; `recall(scope=null)` returns all (backward compat); two users store the same SPO fact without overwrite; over-fetch prevents owned-row starvation when many higher-scoring foreign rows exist.
- *Migration*: `0002` is idempotent, creates indexes, leaves existing nodes globally recallable.
- *Guard*: extend the boundary/contract guard tests to assert every long-term `SearchByVector` signature carries a `MemoryScope` parameter (prevents regression of the isolation contract).

### R1.6 Why this design
- **Optional by construction** — exactly the user's requirement: set an owner to privatize, leave it null to share globally.
- **Safe default** — unscoped callers and all existing data are unchanged (everything is "shared" until an owner is assigned).
- **Proven mechanics** — over-fetch+filter already ships for message search; we are generalizing, not inventing.
- **Forward-compatible** — the new params/fields are optional with defaults, so they are non-breaking *now* and avoid a breaking change after the NuGet API surface is frozen.

---

## R1b — Application / memory-store isolation (the storage tier above the owner)

### R1b.1 The requirement
*"I'd like an **application id** / **memory-storage id** (they're roughly the same) that could be `null` (the default storage). A concrete id creates its own memory storage — and inside that storage, the `owner_id` rules from R1 still apply (null = shared to everyone in that storage; a concrete id = private to that user). I think it makes sense for each to have its own Neo4j database."*

### R1b.2 The scope model (three tiers)
This adds the **coarsest** tier on top of R1:

```
Store / Application   (ApplicationId — coarsest; ideally a Neo4j DATABASE)   null ⇒ default store
  └─ Owner / User     (owner_id, R1 — logical, within a store)               null ⇒ shared in store
       └─ Session      (session_id — short-term conversation)                 required for messages
```

A **memory store** is an isolated memory space selected by an optional `ApplicationId`. `null` ⇒ the single default store (today's behavior). A concrete id ⇒ its own store. **Within** a store, R1's `owner_id` provides the per-user layer unchanged.

### R1b.3 Database vs. property — why per-database for this tier
The owner tier (R1) is a **property** (`owner_id`) because it only needs to filter long-term *search*. The application tier is different: it must isolate **every** node type — messages, conversations, entities, facts, preferences, **and** reasoning traces. Two ways to realize it:

| | **Per-database** (recommended) | **Per-property `application_id`** |
|---|---|---|
| Mechanism | `ApplicationId` → a Neo4j database; route per request | `application_id` on every node + predicate on every query |
| Coverage | The DB boundary isolates **all** node types automatically | Must touch **every** `MATCH`/`MERGE`/`CREATE` — large surface |
| Leak risk | Structural isolation; very low | One forgotten query = a cross-tenant leak |
| Ops upside | Per-tenant backup/restore, noisy-neighbour isolation, easy "delete a tenant" | None |
| Cost | **Requires Neo4j Enterprise / AuraDB** (Community = 1 DB) | Works on Community, but invasive + risky |

**Recommendation: per-database for the application tier.** The database boundary is the right tool for "a whole separate memory space," and the driver already supports per-session database selection (`AsyncSession(o => o.WithDatabase(db))`). The property approach is only a fallback when multi-database is unavailable and multiple apps truly must share one Community database.

> ⚠️ **Edition constraint.** Neo4j **Community Edition supports a single user database** — `CREATE DATABASE` / multi-database needs **Enterprise Edition or AuraDB**. A typical local Neo4j is Community, so `DatabasePerApplication` requires Enterprise/Aura; on local Community use `SharedDatabase` + `owner_id`, or run a separate Neo4j instance/connection per application.

### R1b.4 Recommended design — a pluggable `MemoryStorageStrategy`

```csharp
public enum MemoryStorageStrategy
{
    SharedDatabase,        // default — one DB; isolate users via owner_id (R1). Community-compatible.
    DatabasePerApplication // each ApplicationId → its own Neo4j database. Requires Enterprise / AuraDB.
}

public sealed class MemoryStoreOptions
{
    public MemoryStorageStrategy Strategy { get; set; } = MemoryStorageStrategy.SharedDatabase;
    public string DefaultDatabase { get; set; } = "neo4j";  // store used when ApplicationId is null
    public string DatabasePrefix  { get; set; } = "mem_";   // db name = DatabasePrefix + sanitize(ApplicationId)
    public bool   AutoProvision   { get; set; } = true;     // CREATE DATABASE + bootstrap on first use
}

// Ambient, per-DI-scope (the application rarely changes mid-operation, unlike the per-request owner/user).
public interface IMemoryStoreContext { string? ApplicationId { get; } }
```

**Resolution & routing.**
- `INeo4jSessionFactory.OpenSession` currently binds a single static `Neo4jOptions.Database`. Change it to resolve the database name **per scope** from `IMemoryStoreContext`:
  `db = ctx.ApplicationId is null ? opts.DefaultDatabase : opts.DatabasePrefix + Sanitize(ctx.ApplicationId)` (then `WithDatabase(db)`).
- `Sanitize` enforces Neo4j database-naming rules (lowercase, alphanumeric/dot/dash, length); reject/hash anything else.
- `SharedDatabase` strategy always returns `DefaultDatabase` (so `ApplicationId` is ignored unless you opt into the property fallback).

**Provisioning** (`IMemoryStoreProvisioner`).
- `EnsureStoreAsync(applicationId)` → on `DatabasePerApplication`: `CREATE DATABASE {db} IF NOT EXISTS` (run against the `system` database; needs admin), wait for it online, then run `SchemaBootstrapper` **against that database** (constraints + vector/fulltext/property indexes, including the R1 `owner_*` indexes). Cache provisioned store ids to avoid repeat checks.
- On Community / when multi-database is unavailable: throw a clear, actionable `NotSupportedException` ("DatabasePerApplication requires Neo4j Enterprise or AuraDB; use SharedDatabase, or run a separate instance per application").

**API & adapter surface.**
- Set the store per agent/host scope: a `services.AddAgentMemoryStore(o => o.ApplicationId = "...")` or a scoped setter; default null.
- MAF/MCP/SK: read an `application_id` (alongside `user_id`) from the MAF `StateBag` / tool args / SK parameter and populate `IMemoryStoreContext`, so a single deployment can serve multiple applications by setting the store id per request scope.

**Migration & backward compatibility.**
- Fully additive: default `SharedDatabase` + `DefaultDatabase="neo4j"` reproduces today's behavior exactly. No existing call site changes.
- For `DatabasePerApplication`, the existing file-based `MigrationRunner` + `SchemaBootstrapper` run **per database** at provision time (each store gets the same schema, incl. the R1 `0002_owner_scope` migration).

**Composition with R1.** Within any store, R1 is unchanged: `owner_id` null ⇒ shared to everyone in that store; concrete ⇒ private to that user in that store. So a recall resolves to: *this store's database* → *owner_id == me OR shared* → (for messages) *session_id*.

### R1b.5 Tests
- Session factory routes to `DefaultDatabase` when `ApplicationId` is null, and to `mem_<id>` when set; `Sanitize` rejects/encodes invalid names.
- Provisioner is idempotent (`CREATE DATABASE … IF NOT EXISTS`), bootstraps schema in the new database, and caches.
- *Integration (Enterprise/Aura image)*: data written under application A is invisible from application B (different databases); within app A, R1 owner rules still hold.
- Community path: requesting `DatabasePerApplication` without multi-database throws the actionable error.
- Backward compat: default strategy + null application id behaves exactly like today.

### R1b.6 Sequencing note
Like R1, the `IMemoryStoreContext` / `MemoryStoreOptions` / store-aware `INeo4jSessionFactory` surface should land **before the first NuGet publish** so multi-store support is forward-compatible. The `SharedDatabase` default keeps it zero-impact for single-store users; `DatabasePerApplication` is opt-in for Enterprise/Aura multi-tenant deployments.

---

## R2 — Reasoning-trace isolation (secondary scope gap)

`ReasoningTrace` is session-scoped only; `ReasoningQueries.SearchByTaskVector` applies **no** session/user filter, and the assembler calls `SearchSimilarTracesAsync` with a `null` session (`MemoryContextAssembler.cs`), so trace recall crosses sessions/users. `ReasoningStep` has no scope at all. Same class of leak as R1 but lower blast radius (traces are less personal than preferences).

**Recommendation:** fold into the R1 workstream — add `OwnerId` to `ReasoningTrace`, an owner predicate to the task-vector search (same over-fetch+filter), and thread the scope from the assembler.

---

## R3 — NuGet release sequencing

**✅ Unblocked (2026-06-06).** Release prep is ~87% (packaging metadata + tag-gated `squad-release.yml` exist; `NUGET_API_KEY` secret now set). The original gate — *"don't publish until R1's API surface lands, because NuGet IDs and the public API become permanent on first publish and adding `MemoryScope`/`OwnerId`/scoped signatures later would be breaking"* — **is satisfied: R1 + R1b + R2 are complete and the isolation API is shipped and forward-compatible.**

**What freezes on first publish (confirm before tagging):** the 11 `AgentMemory.*` package ids; the version (`0.1.0-preview.1`); per-package descriptions/tags; and the Neo4j index/database names shipped in R1/R1b (`*_owner_idx`, `rel_owner_idx`, `StoreDatabaseNaming`).

**Only procedural work left:** (1) move CHANGELOG `[Unreleased]` → `[0.1.0-preview.1]` dated today; (2) `git tag v0.1.0-preview.1 && git push origin v0.1.0-preview.1` → `squad-release.yml` packs `src/*` and pushes with `NUGET_API_KEY`.

---

## R4 — Streaming extraction (DI wiring) — ✅ DONE (2026-06-06)

A working `IStreamingExtractor` (chunking, overlap, cross-chunk dedup) existed in Core with unit tests but was **not registered in DI** — held back intentionally until R1 isolation landed. Now **registered in `AddAgentMemoryCore`** (`services.TryAddScoped<IStreamingExtractor, StreamingExtractor>()`). It is a pure text→chunks→entities helper that does not persist, so it carries no owner context itself; owner stamping (R1) happens when its output is persisted via `PersistenceStage` with `ExtractionRequest.UserId`. The shipped interface name `IStreamingExtractor` is the **final** name (the planned `IStreamingExtractionPipeline` is not adopted). `docs/nextsteps.md` updated to match.

**Recommendation:** correct the roadmap to "partially done"; register the existing implementation in `ServiceCollectionExtensions`; reconcile the interface name; and ensure its extracted nodes carry `OwnerId` once R1 lands (otherwise streaming re-introduces the isolation gap).

---

## R5 — Code quality / overall status

**Strong.** The 4-phase remediation + adversarial review are closed; there is real CI, package-boundary and abstractions-contract guard tests, centralized Cypher constants, a clean `CypherBuilder`, a file-based `MigrationRunner`, and a `SchemaBootstrapper`; ~99% Python parity. The MAF 1.9.0 migration was source-compatible. The architecture is clean and the isolation fix **rides existing rails** — no structural rework is required; the gap is missing scope plumbing, not bad design. Treat R1 as the single most important pre-release workstream; reuse the message-search pattern and migration infrastructure rather than inventing new mechanisms.

---

## R6 — Deferred backlog

| Topic | Assessment | Recommendation |
|---|---|---|
| **CLI tool** (`migrate`/`schema-check`) | `MigrationRunner` + `SchemaBootstrapper` already provide the engine; a CLI is a thin `dotnet tool` wrapper. Valuable for ops, especially once `0002_owner_scope` exists. | Build *after* R1 so `migrate` ships the owner-scope migration; keep v1 scope to `migrate` + `schema-check`. |
| **GDS analytics** (`AgentMemory.Analytics`) | Opt-in PageRank/community detection; needs the GDS plugin (not in Community Edition). Pure enhancement. | Defer; ensure analytics respect `owner_id` once R1 lands (don't surface another user's nodes). |
| **BenchmarkDotNet harness** | Backs perf claims; hardware-sensitive. The over-fetch+filter strategy adds read cost worth quantifying. | Defer; add an owner-filtered vector-search benchmark to tune `OverFetchFactor`. |
| **S9 truncation refactor** | `MemoryContextAssembler` budget/truncation logic is large and inline but correct and well-tested. | Defer; if touched, do it *after* R1's assembler change to avoid churn. |
| **WorkflowMemory sample** | Not part of the canonical MAF memory pattern (context-provider + tools) the official refs showcase. | Optional; low priority. |

---

## R7 — Done vs pending

**Done (verified).** v1 feature set complete and hardened — 11 packages; ~2,211 unit + 31 SK + 109 integration + 3 perf tests green; Testcontainers integration; 9 samples (incl. AgentWithMemory, RealAgent, MemoryToolsAgent, ChatHistoryProvider, Aspire demo); 4-phase remediation + adversarial review closed; MAF 1.1.0→1.9.0 migration; package rename; `DELETE_SESSION_DATA` gap closed; real CI + boundary/contract guard tests. Short-term message search **is** correctly session-scoped (the pattern to reuse). Fix infrastructure already exists: `MigrationRunner`, `SchemaBootstrapper`, the `CypherBuilder` over-fetch+filter vector pattern, `IIdGenerator`/`IClock`.

**Pending — deferred by decision (non-blocking).** NuGet release ~87% (unblocked — only CHANGELOG + `v*` tag left); CLI 0%; GDS 0%; BenchmarkDotNet 0%; S9 refactor 0%; backlog (conflict detection, cross-agent sharing, ONNX local embeddings, local NLP extractors, Opik, more framework integrations). *(Streaming extraction is now registered — R4 done.)*

**~~Pending — genuine gap (the focus of this plan).~~ ✅ DONE (2026-06-05).** **Multi-user / multi-session isolation** of long-term knowledge (R1) and reasoning traces (R2) is **implemented and verified** — `owner_id` + `MemoryScope` (optional shared) across every recall/lookup/GraphRAG/trace/relationship/`AsOf`/facade-tool path, plus the R1b per-application store tier. This paragraph described the original gap; see **Part II §II.5** for the completion status and the remaining documented design decisions.

---

## Part II — Post-R1 review: remaining isolation work, critical defects & upstream ports (2026-06-05)

After R1 core landed (I1–I9), a multi-agent review (35 verified findings, 4 refuted; full file-level detail in [`Remaining_Work_Roadmap.md`](Remaining_Work_Roadmap.md)) found that owner scoping was correct on the vector-recall path but bypassed on several secondary paths, plus two correctness defects in the R1b store tier. **These were subsequently CLOSED in IC1–IC8 (2026-06-05/06)** — GraphRAG, ReasoningTrace, non-vector reads, relationships, temporal `AsOf`, and the facade tools are now owner-scoped, and both R1b defects are fixed. §II.5 below is the authoritative completion status.

### II.1 Isolation completeness scorecard

| Path | Status |
|---|---|
| Vector recall — Fact / Entity / Preference | ✅ Done (reference pattern: `FactQueries.SearchByVector`) |
| Non-vector reads — Fact `GetBySubject` | ✅ Done (IC3); `FindByTriple` is internal-only (write-side dedup), not user-exposed |
| Non-vector reads — Entity `GetByName` | ✅ Done (IC3); `GetByType` = entity-resolver write-side (open question: entities may be shared); spatial = repo-only API |
| Non-vector reads — Preference `GetByCategory` | ✅ Done (IC3) |
| GraphRAG retrieval (all 4 retrievers) | ✅ Done (IC4) — `request.UserId` → owner/shared filter (over-fetch on vector/graph; seed+related filtered), verified |
| ReasoningTrace — write / vector-search | ✅ Done (IC1) — owner persisted + over-fetch owner filter, verified |
| ReasoningTrace — session-delete | ✅ Resolved as design decision (IC-delete) — `ClearSession` is **session-scoped by design** and deletes only short-term messages/conversations + traces (all keyed by `session_id`); it never touches owner-scoped long-term knowledge. Messages/conversations have no `owner_id` (session_id is their boundary), so owner-scoping only the trace delete would be inconsistent. `session_id` is the short-term isolation boundary; protecting it from cross-user access is the consumer's authorization concern (or use globally-unique session ids / the R1b per-application store for multi-tenant single-DB). |
| Relationships — write / read | ✅ Done (IC2) — `owner_id` on the RELATED_TO edge (persisted by PersistenceStage, read-back), scoped reads, `rel_owner_idx` + migration 0003, verified |
| Temporal (`AsOf`) recall | ✅ Done (IC5) — entity/fact/preference AsOf vector search now over-fetch + owner-filter; assembler threads scope into the AsOf path |
| Background embedding backfill | ⬜ Intentionally global — `GetPageWithoutEmbedding*` is an admin maintenance job that embeds all nodes regardless of owner (not a recall path); scoping it per-owner would defeat batch backfill. Documented, low risk (timing side-channel only) |
| Store tier (per-application DB) | ✅ routing works; both defects fixed (IC6 AsyncLocal store context; empty-id collision hash) |

### II.2 Isolation-completion tracking table (workstream "IC")

| ID | Scope | Status | Where |
|---|---|---|---|
| IC1 | ReasoningTrace owner end-to-end — ✅ **write+recall done** (`owner_id` persisted on `AddTrace` + read-back; `StartTraceAsync`/`AgentTraceRecorder` stamp owner; over-fetch owner filter in `SearchByTaskVector`; `MemoryScope` threaded through `SearchSimilarTracesAsync` + assembler; 4 integration tests green). `DeleteBySession` owner-scoping resolved as a design decision (see the session-delete scorecard row: `ClearSession` is session-scoped and never deletes owner-scoped long-term knowledge). | ✅ Done | `ReasoningQueries.cs`, `Neo4jReasoningTraceRepository.cs`, `ReasoningMemoryService.cs`, `MemoryContextAssembler.cs` |
| IC2 | Relationship owner end-to-end — ✅ **Done**: `owner_id` on the RELATED_TO edge (Upsert sets it once; `PersistenceStage` stamps from `ExtractionRequest.UserId`; read-back); scoped `GetByEntity`/`GetBySource`/`GetByTarget` (consts→methods); `rel_owner_idx` relationship-property index + migration `0003_relationship_owner_scope.cypher`; 3 integration tests green | ✅ Done | `RelationshipQueries.cs`, `Neo4jRelationshipRepository.cs`, `PersistenceStage.cs`, `SchemaQueries.cs`, `ILongTermMemoryService.cs` |
| IC3 | Non-vector long-term reads scoped — ✅ **Done** for the user-facing leaks: `MemoryScope` threaded through Fact `GetBySubject`, Entity `GetByName`, Preference `GetByCategory` (query consts→methods + repos + `ILongTermMemoryService` + MCP `EntityTools.MemoryGetEntity` `userId`); 4 integration tests green. Not exposed/deferred: `FindByTriple` (internal dedup), spatial (repo-only), `GetByType` (entity-resolver — see open question on shared entities) | ✅ Done (user-facing) | `{Fact,Entity,Preference}Queries.cs`, repos, `ILongTermMemoryService`, `EntityTools.cs` |
| IC8 | `IMemoryQueryFacade` explicit memory-tool surface owner-scoping — ✅ **Done**: new ambient **`IMemoryOwnerContext`** (`AsyncLocal` `UserId`, singleton, IC6-style); `MemoryQueryFacade` reads it to scope `SearchMemory`/`RecallPreferences`/`SearchKnowledge`/`FindSimilarTasks` and stamp `OwnerId` on `Remember*`. **Host must set `IWritableMemoryOwnerContext.UserId` for the agent run** — the LLM-invokable `AIFunction` tools can't carry a trusted user id, and an `AsyncLocal` set inside the MAF provider would not reach framework-invoked tools (sibling flow). 3 unit tests. (SK plugin/MCP call services directly, already `userId`-aware — not facade consumers.) | ✅ Done (host sets ambient owner) | `IMemoryOwnerContext.cs`, `DefaultMemoryOwnerContext.cs`, `MemoryQueryFacade.cs`, Core DI |
| IC4 | GraphRAG owner scoping — ✅ **Done**: `ownerId` on `IRetriever.SearchAsync`; Vector/Fulltext/Hybrid/Graph retrievers apply the owner/shared WHERE (over-fetch on the vector + graph-seed paths; graph traversal filters seed AND related); `Neo4jGraphRagContextSource` passes `request.UserId`. Unit forwarding tests + 2 Neo4j isolation integration tests green | ✅ Done | `Retrieval/IRetriever.cs`, `Retrieval/Internal/{RetrieverScope,Vector,Fulltext,Hybrid,Graph}*.cs`, `Neo4jGraphRagContextSource.cs` |
| IC5 | Temporal `AsOf` recall scoped — ✅ **Done**: `TemporalQueries.Search{Entities,Facts,Preferences}AsOf` consts→methods with over-fetch + owner filter; repos + `ILongTermMemoryService` AsOf methods thread `MemoryScope`; assembler builds + passes scope on the AsOf path. Backfill = intentionally global (admin maintenance, documented above) | ✅ Done | `TemporalQueries.cs`, repos, `ILongTermMemoryService.cs`, `MemoryContextAssembler.cs` |
| IC6 | DI captive-singleton fix — ✅ **Done**: `DefaultMemoryStoreContext.ApplicationId` is now `AsyncLocal`-backed, so the process-wide singleton is per-request-flow safe (concurrent agent runs can't corrupt each other's store routing). 3 unit tests incl. a concurrency-isolation test | ✅ Done | `MemoryStoreOptions.cs`, `ServiceCollectionExtensions.cs` |
| IC7 | Tests + docs honesty — ✅ **Done**: each IC1–IC6 shipped with Neo4j integration tests (the integration suite was 103 green at IC-completion; it has since grown to **~128** with the later hardening + good→great work); README gains a "Multi-user & multi-store isolation" section documenting the capability + honest status; `nextsteps.md` gains an R1/R1b tracking row | ✅ Done | `README.md`, `docs/nextsteps.md`, `tests/...` |

### II.3 Critical defects (R1b store tier)

- **DI captive-singleton** (`IMemoryStoreContext` Singleton, mutated per scope via MAF `ApplyStoreContext`) → concurrent multi-tenant requests can route to the wrong store. → IC6.
- **`StoreDatabaseNaming` empty-id collision** (all-punctuation/emoji ids collapsed onto `mem-`) → **FIXED 2026-06-05** (deterministic hash fallback + tests).

### II.4 Upstream ports worth doing (neo4j-labs/agent-memory, last ~2 months, through v0.4.0)

Full table in the roadmap. Sorted high→skip:

| Feature | Source | Our status | Port-worthiness |
|---|---|---|---|
| Fact/Preference **dedup-on-create** (≥0.95 cosine → bump confidence) | PR #97 (2026-04-23) | ✅ **DONE** (2026-06-06) — `LongTermMemoryOptions.DeduplicateOnCreate` (default on, 0.95); `AddFact/AddPreferenceAsync` reinforce a same-subject+predicate / same-category, same-owner near-duplicate instead of creating a node; `FindDuplicate`+`MarkDeduplicated` queries/repo methods; 4 unit + 4 integration tests | done |
| **Consolidation / hygiene** (dedupe/summarize/detect-superseded/archive; dry-run; `:ConsolidationRun`) | PR #113 v0.2.0 (2026-05-04) | ✅ **DONE** (2026-06-06) — `IConsolidationService` (dry-run default; `:ConsolidationRun` audit). Mutating: archive-expired-conversations, remove-duplicate-preferences. Detection: duplicate-entity + long-trace counts (apply deferred: entity-merge needs edge redirection, trace-summarize needs an LLM). Schema: `conversation_archived_idx` + `consolidation_run_id` constraint + migration 0004. 3 unit + 2 integration tests | done (core) |
| Adopt existing Neo4j graph as memory | PR #113 | missing | medium |
| Vector-index dimension validation at connect | PR #119 v0.3.0 (2026-05-16) | ✅ **DONE** (2026-06-06) — `SchemaBootstrapper` + `Neo4jMemoryStoreProvisioner` read existing index dims via `SchemaQueries.ShowVectorIndexDimensions` (`SHOW VECTOR INDEXES`) and throw `EmbeddingDimensionMismatchException` (lists offenders) on mismatch; `VectorIndexDimensionValidator` holds the pure comparison; opt-out `Neo4jOptions.ValidateVectorIndexDimensions` (default true). 7 unit + 2 integration tests | done |
| `:TOUCHED` reasoning-audit edges | PR #113 | ✅ **DONE** (2026-06-06) — `(:ReasoningStep)-[:TOUCHED]->(:Entity)` provenance edges via `IReasoningMemoryService.RecordTouchedEntitiesAsync`/`GetTouchedEntitiesAsync` + `IReasoningStepRepository.LinkTouchedEntitiesAsync`/`GetTouchedEntityIdsAsync`; `ReasoningQueries.RecordTouchedEntitiesByIds`/`GetTouchedEntityIds`; `SchemaConstants.RelationshipTypes.Touched`. By-id (links existing entities only — never MERGE-creates, preserving resolution/dedup); idempotent; `recorded_at` stamped on create. Upstream edge direction verified against `graph/queries.py`. No schema constraint/index needed (parity-confirmed). 4 unit + 5 integration tests | done (edges; encryption helper deferred) |
| `:User` node + UserMemory CRUD | PR #113 | partial (we use `owner_id`+`IMemoryStoreContext`) | medium |
| Entity feedback / edit-history / `bulk_add_messages` | v0.4.0 (2026-05-21) | partial | medium |
| Buffered/fire-and-forget writes; `schema_aligned_extract` repair; session reflections | PR #113/#119 | partial | low–medium |
| Pluggable LLM/embeddings; multi-tenancy flag; no-LLM; declarative schema; read-only Cypher accessor | various | **have** (Microsoft.Extensions.AI + `IMemoryStoreContext`) | **skip** |
| NAMS hosted REST backend; TypeScript client; `.env` fix | v0.4.0 PRs | n/a | **skip** |

> Sequencing: do the **upstream ports after** IC1–IC7 so dedup/consolidation Cypher inherit `owner_id` scoping from day one.

### II.5 Status — multi-user isolation workstream COMPLETE (2026-06-05)

All isolation **code-fixes are done and verified** (IC1–IC8; full unit + Neo4j integration suites green). Every recall/lookup path is owner-scoped: vector recall, non-vector lookups, GraphRAG (all 4 retrievers), reasoning traces, relationships, temporal `AsOf`, and the explicit facade tools (via the ambient owner context). The two R1b store-tier defects are fixed.

**Remaining items are documented design decisions, not open leaks:**
- **Session-delete** — `ClearSession` is session-scoped by design and never deletes owner-scoped long-term knowledge (II.1 row).
- **Entity-resolution sharing** — entities are often legitimately shared (public companies, places); whether extraction-time resolution should be owner-partitioned is an open product decision, not a leak (entities carry `owner_id` and recall is scoped).
- **Index-naming nits** (`fact_category`, `reasoning_step_timestamp` lack the `_idx` suffix) — cosmetic; renaming is a breaking drop+recreate migration, so deferred.
- **Background embedding backfill** — intentionally global admin maintenance.

**Next:** ✅ the high-value upstream ports (dedup-on-create, consolidation/hygiene) and the Phase-2 quick wins (vector-dim validation, `:TOUCHED`) all landed. The NuGet release (R3) is **cut and tagged locally, awaiting push**. The live pending list is **§II.6**.

### II.6 Post-release pending — code-verified 2026-06-06

A 5-doc × per-item code-verification audit refreshed this list. **Every item below was checked against the actual code**, not the (drifted) docs. Nothing here blocks the release; the release is the top priority.

**0 — Ship the release (do first).**
1. Open PR `remediation/analysis-review-hardening` → `main` (**~63 commits as of 2026-06-07**, none pushed; local HEAD has also diverged from the pushed `origin/remediation/...` tip — reconcile before merge).
2. `git push origin v0.1.0-preview.1` → triggers `squad-release.yml` (build → test → pack 11 → `nuget push` → GitHub release). `NUGET_API_KEY` is already set (the audit's "add the secret" item is stale — it's present). Publish is irreversible.

**Tier 1 — test coverage on the shipped, pre-1.0 isolation surface (highest value; the headline feature has thin spots).** — ⏳ **largely DONE 2026-06-06.**
- ✅ E2E store isolation + provisioner real `CREATE DATABASE` (**`StoreIsolationIntegrationTests`**, live Neo4j **Enterprise** container): per-application DB isolation proven end-to-end (write in store A invisible in store B), `EnsureStoreAsync` physically creates the DB, idempotence. **This caught a real bug** — the provisioner inlined the store DB name unquoted, so `CREATE DATABASE mem-… ` (default dash-prefix) was a Cypher syntax error; **fixed** by backtick-quoting (`Neo4jMemoryStoreProvisioner.QuoteName`). 3 integration tests + a mock unit guard. Tagged `Edition=Enterprise` (pulls the enterprise image + accepts the eval license in CI; filter `Edition!=Enterprise` to skip).
- ✅ `MigrationRunner` real-DB integration test (**`MigrationRunnerIntegrationTests`**, Community fixture): discovery → multi-statement parse → comment stripping → constraint create → version recording → idempotence. 2 tests. (`InternalsVisibleTo` extended to the integration project for the test-seam ctor.)
- ⏭️ `AgentSession.WithMemoryIdentity` + MAF `ApplyStoreContext` unit tests — **skipped**: MAF `AgentSession` has no public constructor (made via `agent.CreateSessionAsync()`), so unit-testing these 6-line StateBag adapters needs a live agent (disproportionate). The store-routing they feed is now proven by the E2E above. Revisit if a MAF test harness lands.

**Tier 2 — small correctness / parity polish.** — ✅ **DONE 2026-06-06.**
- ✅ CHANGELOG streaming claim corrected (it overstated owner-stamping; `IStreamingExtractor` is standalone and does not persist).
- ✅ MCP `memory_add_fact` now surfaces `category` + `metadata` (metadata as a JSON-object string, parsed with an actionable error on bad JSON). 2 unit tests.
- ✅ `Conversation.Archived` reads back (`Neo4jConversationRepository` mapping; archival stays a consolidation-only write) + `ConsolidationRun` added to `SchemaConstants.NodeLabels`. 1 integration test (round-trip via consolidation).

**Tier 3 — robustness / completeness.**
- ✅ **DONE 2026-06-06** — Temporal `SearchSimilarTracesAsOfAsync`: reasoning traces now appear in `AssembleContextAsOfAsync` (point-in-time recall is complete across all tiers). `IReasoningMemoryService` + `IReasoningTraceRepository` gained the AsOf method; `ReasoningQueries.SearchByTaskVectorAsOf` adds `started_at <= $asOf`. 2 unit + 1 integration test.
- ⬜ `Neo4jMemoryStoreProvisioner` — TOCTOU on the provisioned-cache (`ContainsKey`→`GetOrAdd`) + tighten the fragile "multi-DB unsupported" string match to an exact Neo4j error code. (M) — **low urgency:** the TOCTOU is benign (CREATE/bootstrap are idempotent), so this is an efficiency/robustness nicety, not a correctness bug.
- ⬜ `MigrationRunner.ParseStatements` — naive `Split(';')` + line-level `//` strip; latent for a future migration containing `;`/`//` inside a string literal. **Documented/accepted** for current schema DDL; harden if a richer migration is ever needed. (M, low)
- ⬜ Streaming extraction — provide a built-in persistence path that threads `owner_id` (today it is a standalone helper; callers must persist its output themselves). (M)

**Verified FALSE POSITIVE (do not "fix"):** `FactQueries.Upsert` "duplicates on owner change" — by design. `owner_key = coalesce(owner_id,'*')` is part of the MERGE key, so same-triple/different-owner is *intentionally* a distinct node; `ON MATCH` implies the same `owner_key`, so `owner_id` is already correct. No rebind needed.

**Defer (post-v1 enhancements):** Conversation/Message `OwnerId` (short-term is session-scoped by design); owner-scope the 5 internal non-vector reads (not in the public API — tech debt, no breach); adopt-existing-graph (PR #113); buffered/fire-and-forget writes; LLM session-reflection wiring; conflict detection; cross-agent shared namespace; ONNX local embeddings; GLiNER local NLP; CLI tool (R6a); GDS analytics (R6b); BenchmarkDotNet (R6c); S9 truncation refactor (R6d); eval harness; index-naming `_idx` nits. *(Entity feedback / edit-history / provenance — moved to ✅ DONE, see §II.7.)*

**Skip (no value / already covered):** first-class `:User` node API (our scalar `owner_id` is functionally equivalent — divergence decided, closes upstream #135); Opik (no .NET SDK); LangChain / SemanticRouter / AutoGen adapters (no demand; covered by MAF).

**Recommended very-next coding task (after the release):** ✅ Tier-1 (store-isolation hardening — found+fixed a real `CREATE DATABASE` quoting bug), ✅ Tier-2 (MCP add_fact metadata/category; `Conversation.Archived` read-back; `ConsolidationRun` label), and ✅ the headline Tier-3 item (point-in-time trace recall) are **done**. What remains in §II.6 is all low-urgency: provisioner TOCTOU/`GetOrAdd` (benign — idempotent), `MigrationRunner` quoted-`;` hardening (documented/accepted), and a built-in streaming persistence path. None block the release; pick them up opportunistically post-publish.

### II.6b Adversarial code review (2026-06-06) — verified outcomes

A 5-dimension adversarial review (find → independently verify) of the session's code. 13 raw findings; **2 headline "criticals" were false positives** (re-verified against the code): the `Convert.ToInt32` "bootstrap crash" (the driver materializes a boxed `long`, and the live Enterprise + dim-mismatch integration tests already exercise that path) and `UpsertBatch` "bypasses owner write" (it *does* set `owner_id`/`owner_key`; merge-by-id is the intended bulk primitive).

**Fixed from the review:**
- ✅ **Entity-feedback write is now owner-scoped.** `RecordEntityFeedbackAsync`/`ApplyConfidenceDeltaAsync` + `EntityQueries.ApplyConfidenceDelta` + the `memory_record_entity_feedback` MCP tool now take a `MemoryScope`/`userId`; the Cypher applies the owner/shared filter so feedback can't mutate another owner's private entity. Was the one write path added this session without R1 scoping. Cross-owner integration test added. (`ApplyConfidenceDelta` is now a method, so the Cypher count drops to 145.)
- ✅ `Convert.ToInt32` → `.As<int>()` in `VectorIndexDimensionValidator` (consistency, not a bug).
- ✅ Doc-count drift fixed (R1 row, IC7).

**Isolation-hardening backlog — progress 2026-06-06.**
- ✅ **Cross-owner delete/merge denial (the destructive, urgent set) DONE.** `DeleteAsync` (Entity/Fact/Preference) + `DeletePreferenceAsync`, `MergeEntitiesAsync`, and spatial `SearchByLocation`/`SearchInBoundingBox` now take a `MemoryScope`; scoped deletes touch only the owner's own nodes (never shared/another owner's), merge can't cross owners, spatial can't enumerate foreign locations. Owner-conditional query *methods*; cross-owner integration tests added. ~~Note: spatial **write** is still incomplete — `EntityQueries.Upsert` doesn't persist `location`.~~ **SUPERSEDED 2026-06-07:** spatial write is complete in both the single (`Neo4jEntityRepository.UpsertAsync` → `SharedFragments.SetEntityLocation`) and batch (`UpsertBatchAsync`) paths, with round-trip integration tests — see the "Spatial write gap — closed" entry below and Review Annex §III.
- ✅ **Remaining unscoped reads — triaged & hardened (2026-06-07).** A discovery pass swept **101 repo methods → 16 unscoped-read candidates** of ownable (owner_id-bearing) nodes and dispositioned each:
  - **Fixed — the one real leak (scope-required):** entity resolution. `CompositeEntityResolver.GetCandidatesAsync → IEntityRepository.GetByTypeAsync` was the lone entity read with no `MemoryScope`, and resolution ran with **no owner context even though `ExtractionRequest.UserId` was available at the pipeline boundary** — so an incoming entity could exact/fuzzy/semantic-match onto another owner's private entity and auto-merge into it (write-path leak: aliases/sources appended, then persistence re-stamped the foreign node's `owner_id`). Fix: `EntityQueries.GetByType` const→method; `MemoryScope?` threaded through `IEntityResolver.ResolveEntityAsync`/`FindPotentialDuplicatesAsync`, `IExtractionStage.ExtractAsync`, `ExtractionStage`, and `MemoryExtractionPipeline` (builds the scope from `request.UserId`). Live integration test proves user A's message resolves onto A's/shared entities, never B's.
  - **Hardened with optional scope hooks (scope-optional, default global):** `EntityQueries.SearchByNameFiltered` (+ `Neo4jEntityRepository.SearchByNameAsync`; also parenthesized the name/canonical OR so type+owner AND correctly), `EntityQueries.FindSimilarByEmbedding`, `FactQueries.FindByTriple` — now owner-conditional methods so a future user-facing wiring can confine them. (3 const→method; `ExpectedQueryCount` 137→134.)
  - **Deliberately left global (documented inline so reviews stop re-flagging):** id-scoped reads `GetByIdAsync` (Entity/Fact/Preference), `GetEntitiesFromMessageAsync`, `GetSameAsEntitiesAsync` (the id/message-id is itself an owned handle); write-by-id `MarkDeduplicatedAsync` (Fact/Preference); background back-fill `GetPageWithoutEmbeddingAsync` (Entity/Fact/Preference); admin/maintenance `GetPendingDuplicatesAsync`/`GetDeduplicationStatsAsync` and provenance `GetEntitiesByExtractorAsync` (operator/QA, keyed by extractor name, intended cross-owner). All confirmed to have **no user-facing production caller**.
  - **Non-repository surface (caught by the adversarial verify pass — the repo-only sweep structurally missed it):** the MCP resources `memory://entities` (`EntityListResource`) and `memory://preferences` (`PreferenceListResource`) read ownable nodes via raw Cypher through `IGraphQueryService`, bypassing the scoped repositories, and were registered unconditionally with **no owner parameter** — a real cross-owner read leak (preference free-text especially). Fixed: both gained an optional `userId` that injects the own-or-shared owner clause (null ⇒ unscoped, matching the MCP-tool convention). `MemoryStatusResource` returns only aggregate counts (left global, like `GetDeduplicationStats`); the raw-Cypher *tools* (`graph_query`/`memory_export_graph`/`memory_find_duplicates`) remain gated behind `EnableGraphQuery` (off by default).
- ✅ **Spatial write gap — closed (2026-06-07).** `Neo4jEntityRepository.UpsertAsync` already persisted `location` (a `SetEntityLocation` follow-up after the MERGE) and `MapToEntity` reads it back, but **`UpsertBatchAsync` dropped `Latitude`/`Longitude`** — batch-created entities silently lost their coordinates and were invisible to spatial search. Fixed by mirroring the single-path follow-up in the batch (a per-entity `SetEntityLocation` loop, only for entities with both coords; a point() can't be built from per-row nullable coords in the UNWIND without erroring). Added round-trip integration tests (single + batch: model `Latitude`/`Longitude` → `GetByIdAsync` reads them back AND `SearchByLocationAsync` finds them; no-coords entities stay null) plus batch location unit tests. This was the last open isolation backlog item — **the R1 isolation surface is now complete** (recall, primary CRUD, delete/merge/spatial, decay prune, entity resolution, MCP resources all owner-aware; remaining unscoped reads are deliberately global & documented).

**Test-hardening follow-ups:** add live AsOf integration tests for Entity/Fact/Preference repos (only ReasoningTrace has one); add a CLI config-precedence test.

**✅ DONE (2026-06-07) — wired the Neo4j decay adapter (real bug found by the coverage audit 2026-06-06).** The Core `MemoryDecayService` was a **portable no-op** (return 0 / 0.0 / pass-through) and the `DecayQueries` Cypher was never executed by any service, so "auto-prune" didn't work and `agentmemory decay` reported 0. Now: `Neo4jMemoryDecayService` runs the queries via `INeo4jTransactionRunner` and the Neo4j DI `Replace`s the Core placeholder (asserted by the shakedown). The prune is **owner-scoped** — `DecayQueries.Prune{Entities,Facts,Preferences}` are owner-conditional methods; a scoped prune deletes the owner's **own** nodes only (never another owner's, never shared/global), null scope = global/admin. Interface fixed: `PruneExpiredMemoriesAsync(string sessionId)` → `(MemoryScope? scope)` (long-term nodes have no `session_id`); CLI `decay --session <id>` → `decay [--owner <id>]`. Label-interpolating queries (`UpdateAccessTimestamp`/`GetRetentionFields`) are guarded by an `{Entity,Fact,Preference}` allowlist. **Caught live:** the prune's `daysSince` used `duration.between(...).days`, which returns only the days *component* of a normalized y/m/d duration (a 400-day span → ~1y1m, `.days` small) → nothing pruned; fixed to an epoch-millis delta (total elapsed days). 7 live integration tests (stale-vs-fresh prune, cross-owner non-deletion, access bump, score, missing-node, bad-label) + updated unit/query tests; `ExpectedQueryCount` 140→137 (3 Prune consts → methods).

### II.7 Good → great roadmap (ambition list) — code-verified 2026-06-06

§II.6 is gap-closure/cleanup. This section is the higher-ambition "what would make the library *great*" list, produced by a 10-candidate code-verification pass (each candidate checked against our code + upstream). Distinct from §II.6's low-urgency remnants.

**✅ DONE — Entity auditability & feedback (the "trust" surface; was the #1 great pick).**
- `memory_get_entity_provenance` MCP tool surfaces the already-built `EntityProvenance` (source messages + extractors).
- `Entity.UpdatedAtUtc` reads back (last-modified semantics; surfaced on `memory_get_entity`).
- Entity feedback: `ILongTermMemoryService.RecordEntityFeedbackAsync` + `memory_record_entity_feedback` (confidence nudge, clamped [0,1], `EntityQueries.ApplyConfidenceDelta`, `LongTermMemoryOptions.FeedbackConfidenceDelta`). 4 service + 2 tool + 2 provenance-tool unit tests; 5 entity integration tests.

**✅ DONE — `agentmemory` CLI** (`tools/AgentMemory.Cli`, was the #2 great pick). Commands: `migrate`, `bootstrap`, `consolidate [--apply]`, `conflicts`, `decay [--owner <id>]`; connection from CLI opts / `Neo4j:*` config / `NEO4J_*` env. Testable command handlers (8 unit) + arg parser (7 unit); verified end-to-end against live Neo4j (all four commands). **Surfaced + fixed a real DI gap:** the meta `AddNeo4jAgentMemory` never registered `IClock`/`IIdGenerator`, so consolidation/reasoning/assembler were registered-but-unresolvable (every sample worked around it by hand) — now registered as `TryAdd` defaults in `AddAgentMemoryCore`, with a DI **resolution** test (not just descriptor-presence) to guard it.

**✅ DONE — Conflict / contradiction detection** (`IConflictDetectionService`, detect-only). Fact contradictions (same subject+predicate+owner, ≥2 distinct objects), grouped per owner (R1), with a confidence gate. `ConflictQueries.DetectFactContradictions`; `agentmemory conflicts` CLI command. 2 CLI unit + 1 DI-resolution + 3 live integration tests (incl. per-owner grouping + the gate). A lead over both .NET and upstream Python. *(v2: semantic conflicts + preference contradictions + resolution.)*

**Strong "good" (ranked):** auto session reflections (~60% built — `ContextCompressor` done, auto-trigger wiring missing; M) → eval harness (now unblocked by `:TOUCHED`; test-only; M) → GDS analytics (post-1.0; M) → adopt-existing-graph (L; gate on demand) → buffered writes (situational; M) → local/offline embeddings + NLP (L/XL; post-1.0).

**Skip / already done:** bulk message ingest (already shipped — `AddBatchAsync`/`AddMessagesAsync`); first-class `:User` node (scalar `owner_id` equivalent); Opik (no .NET SDK); LangChain/SemanticRouter/AutoGen adapters.

**Suggested next sequence:** ✅ entity-trust surface, ✅ CLI tool → **conflict-detection (detect-only)** next → bundle with auto session reflections into a **v0.2.0 "session lifecycle + trust"** release, with the eval harness as its quality gate. GDS / adopt-graph / local-NLP wait for real production demand.

---

## Part III — Review Annex (in-depth audit, 2026-06-07)

> **Method.** A multi-dimensional adversarial review (6 parallel scanners — correctness, isolation/security, doc-drift, tests, API/design, release-readiness — then a skeptic verifier per finding, then synthesis). **51 raw findings → 38 verified → 37 confirmed, 1 rejected as a false positive.** Every claim below was re-checked against the live tree; file:line references are from that check. This annex is additive — it does not change any code; it records the current state objectively so fixes can be sequenced.

### III.0 Overall assessment

The remediation work (R1/R1b isolation, upstream ports, decay/consolidation wiring, CLI, packaging, the recent decay adapter + unscoped-reads hardening + spatial-write fix) is **fundamentally sound and accurately documented in the "done" direction** — the verifier specifically looked for status markers that overstate completion and **found none** (every `✅ Done` in the §I status table and §II is genuinely code-backed; see III.2). The code builds, all suites pass (2254 unit + 31 SK + 158 integration), and the 11 `src/*` packages pack cleanly.

**The dominant theme of the open findings is that multi-tenant isolation (R1) is complete on the *repository layer* and on the entity/preference MCP resources, but is NOT uniform across the rest of the public surface.** Several externally-reachable entry points were missed by the repository-centric sweeps. None is critical (all require a multi-tenant deployment that writes owned data; the documented single-tenant default is unaffected), but they qualify the "R1 isolation COMPLETE / verified end-to-end" framing used in earlier updates — that claim is accurate for the **repository + resolution + decay-prune + entity/preference-resource** layers, **not** for the MCP `context` resource, the SK text-search adapter, or the no-`UserId` extraction entry points.

### III.1 Confirmed findings (by severity)

#### 🔴 HIGH

- **[leak-1] MCP `ContextResource` (`memory://context/{session_id}`) is unscoped → cross-owner *content* leak.** `GetContext` (`src/AgentMemory.McpServer/Resources/ContextResource.cs:18-30`) builds a `RecallRequest` with `SessionId`/`Query`/`Options` only — **no `UserId`, no scope** — and serializes entity names, fact subject/predicate/object triples, and preference text. `MemoryContextAssembler.cs:60-61` treats null `UserId` as global recall, so the underlying vector reads omit the owner filter. Registered live (`McpServer/ServiceCollectionExtensions.cs:54`), not gated by `EnableGraphQuery`. Its siblings `EntityListResource`/`PreferenceListResource` *were* given `userId` params in the unscoped-reads pass — **this resource was missed** (the earlier verify pass false-negatived it as "uses scoped recall"; this deeper pass refuted that). **Impact:** any MCP client can read another owner's long-term-memory content via `memory://context/<anything>` with a chosen non-blank query. **Fix:** add a `userId` param → `RecallRequest.UserId` (mirror `memory_get_context`); pair with a live isolation test. *This is the top item to fix before any multi-tenant adopter uses the resource surface.*
- **[gap-2] MCP `EntityTools` owner-scope translation is effectively untested.** `EntityTools.cs:43,69` do `scope = string.IsNullOrEmpty(userId) ? null : MemoryScope.For(userId)`, but `EntityToolsTests` never passes a `userId` and asserts scope only with `Arg.Any<MemoryScope?>()` — swapping the translation to always-`null` would keep the suite green. The house standard is stronger elsewhere (`CliCommandsTests`, `CompositeEntityResolverTests`, `CoreMemoryToolsTests` assert `OwnerId == "..."`). **Fix:** add `Arg.Is<MemoryScope?>(s => s.OwnerId == "alice")` / `s == null` / empty-string-userId cases.
- **[rel-3] Release would ship from an un-merged, un-reviewed branch.** Verified: `main..HEAD = 63` commits (the doc said ~26 — corrected this pass), `origin/main..HEAD = 64`, tag `v0.1.0-preview.1` is local-only (not pushed), no PR open, and the local HEAD has **diverged from the pushed `origin/remediation/...` tip**. **Fix:** open + review + merge the PR to `main`, reconcile local vs remote, then move/cut the tag on the merge commit and push. (Process gate, not a code defect.)

#### 🟧 MEDIUM

- **[leak-2] SemanticKernel `Neo4jTextSearch` is structurally unscoped.** `Neo4jTextSearch.cs:65-66` builds `RecallRequest { SessionId, Query }` with no `UserId`; its ctor takes only `sessionId`, so the adapter *cannot* owner-scope. All three SK entry points route through it and emit cross-owner facts/preferences/entities. Its sibling `Neo4jMemoryPlugin` *does* accept `userId`. **Fix:** thread an owner id (ctor or `TextSearchOptions`) → `RecallRequest.UserId`, or document the adapter as single-tenant/shared-only and warn on `AddNeo4jTextSearch`.
- **[leak-3] No-`UserId` extraction entry points persist `owner_id = NULL` (shared).** `extract_and_persist` / `memory_extract_session` (`AdvancedMemoryTools.cs:146-207`), `MemoryService.ExtractFromSession/ConversationAsync` (`MemoryService.cs:211-234`), and the MAF facade `Neo4jMicrosoftMemoryFacade.PersistAfterRunAsync` (`:100-105`) all build `ExtractionRequest` with no `UserId` → `PersistenceStage` stamps `owner_id = null`. Because `MemoryScope.IncludeShared` defaults true, those records then surface to **every** scoped user on recall. The MAF facade is the sharpest case: its sibling `Neo4jMemoryContextProvider.PerformStoreAsync` *does* thread `UserId`, so privacy depends on which path persisted the memory. **Fix:** add `userId` to the extraction tools/facade (derive from the session/`Conversation.UserId` where possible), or reconsider `IncludeShared`'s default in multi-tenant mode.
- **[gap-1/gap-4] No live-Neo4j isolation test for the MCP resource surface.** `MemoryResourcesTests` asserts the entity/preference resources only via query-string `.Contains()` against a substituted `IGraphQueryService` (no DB); `ContextResource` has no test at all. The resources hand-roll their own Cypher (they don't delegate to the repositories that *do* have live owner-scope tests), so the actual multi-tenant boundary clients hit is unproven against a real graph. **Fix:** construct a real `Neo4jGraphQueryService(fixture.TransactionRunner, …)`, seed alice/bob/null, call the resource with `userId:"alice"`, assert bob absent.
- **[gap-overfetch] Over-fetch starvation is tested only for Facts — and that test never crosses the floor.** The owner-filter over-fetch (`topK >> limit`) is duplicated across Entity/Fact/Preference/ReasoningTrace repos, but the only live starvation test (`OwnerScopeIsolationIntegrationTests.cs:127-143`) seeds 52 candidates < `topK=55`, so no row is ever dropped by truncation; Entity/Trace scoped-vector tests seed only ~3-4 rows; Preference scoped vector recall has **zero** integration coverage. **Fix:** add starvation tests for Entity/Preference/ReasoningTrace that seed *more* foreign rows than `topK` (e.g. `limit=5` → 55+ foreign rows at identical score).
- **[corr-1] `EntityTools.MemoryGetEntity` description is inverted (security-relevant).** `EntityTools.cs:40` says *"Null = only shared/global entities"* — but null `userId` → null scope → **no owner filter → returns every owner's entities**. An integrator reading this believes the default is the safe/narrow option when it is the broadest read. **Fix:** reword to match the (correct) sibling resources: *"Null = all owners (unscoped/admin); set it to return only that owner's plus shared entities."*
- **[corr-2] `Neo4jMemoryPlugin.RecallAsync` (SK) repeats the same inverted claim.** `Neo4jMemoryPlugin.cs:35`: *"Null recalls only shared/global knowledge"* — same defect on a second public surface (and consumed by an LLM to choose args). **Fix:** *"Null recalls across all owners (no owner filter); set it to recall only that user's plus shared memories."*
- **[corr-3] `memory_get_entity` claims it returns relationships, but the payload has none.** `EntityTools.cs:36` description says *"Returns matching entities with their relationships"*; the implementation serializes scalar fields only and the `Entity` record has no relationships property. **Fix:** drop "with their relationships" (or actually fetch them).

#### 🟡 LOW (defense-in-depth / consistency / edge-case — acceptable as documented backlog)

- **[leak-6] Resolution can write-through to SHARED entities.** With `includeShared=true` (the resolution default), an owner's extraction can auto-merge onto a shared (`owner_id IS NULL`) entity and rewrite its aliases/description/embedding + append the user's `source_message_ids` (`CompositeEntityResolver.cs:78-110` → `UpsertAsync` MERGE-by-id). **Not** a cross-*owned*-tenant leak (that path is correctly blocked, proven by `EntityResolutionOwnerScopeIntegrationTests`). Decide intended semantics: if shared knowledge is read-only for owners, force CREATE-new when `matched.OwnerId is null`, or gate write-through behind a flag, or document it.
- **[leak-7] `CompositeEntityResolver.CreateNewEntityAsync` drops `scope.OwnerId`** (`:195-208` builds the new `Entity` with no `OwnerId`). Currently masked — `PersistenceStage.cs:56` re-stamps `OwnerId` before the authoritative upsert, so no live leak — but any future caller persisting the resolver's output directly would write `owner_id=NULL` for a private entity. **Fix:** stamp `OwnerId = scope?.OwnerId` in the resolver for self-consistency.
- **[leak-4] `memory_get_entity_provenance` is unscoped by `entityId`** (`EntityTools.cs:17-34` → `ExtractorQueries.GetEntityProvenance` has no owner predicate). An attacker who knows/guesses another owner's opaque GUID can confirm existence + learn source message IDs + extractor metadata (not name/description/content). **Fix:** add an owner-or-shared predicate, returning `found=false` out of scope (mirror feedback/delete).
- **[leak-5] `memory://status` and `memory://conversations` expose cross-owner *metadata*** (counts, session ids, message counts) — reconnaissance only, not content. Nuance: `Conversation`/`Message` carry `user_id`, not `owner_id`, so the R1 model can't be applied uniformly to the status aggregates; under `DatabasePerApplication` these are physically isolated. **Fix:** document as admin/operator metadata, or scope `ConversationListResource` via the existing `c.user_id`.
- **[decay-1] Decay by-id ops are not owner-scoped.** `CalculateRetentionScoreAsync`/`UpdateAccessTimestampAsync` (`IMemoryDecayService.cs:26,31`) take no scope (only `PruneExpiredMemoriesAsync` does). Mitigated — the only production caller bumps ids harvested from an owner-filtered recall — but the by-id API is callable directly with a foreign id. **Fix:** add a scope overload + foreign-owner no-op test, or document as deliberate admin by-id ops.
- **[decay-2] `CalculateRetentionScoreAsync` vs prune disagree on null `created_at`.** The read path reads `created_at` via the *non-nullable* helper (`Neo4jMemoryDecayService.cs:105`) which returns `UtcNow` for null → node scores fresh; the prune (`DecayQueries.cs:50`) requires `created_at IS NOT NULL` → never prunes it. Only affects malformed/externally-written nodes (all repo writes set `created_at`). **Fix:** treat null `created_at` as score 0.
- **[decay-3] No zero/negative guard on `DecayHalfLifeDays`.** `lambda = ln(2)/DecayHalfLifeDays` (`Neo4jMemoryDecayService.cs:48,140`); `MemoryDecayOptions` is registered via `Options.Create`, bypassing the `Validate().ValidateOnStart()` pipeline every other options type uses. `=0` → `Infinity`/`NaN` → inconsistent/destructive prune. Pure misconfig (default 30 is safe). **Fix:** add `Validate(o => o.DecayHalfLifeDays > 0)` or a ctor guard.
- **[test-1] Decay integration test seeds via raw Cypher `CREATE`** (`Neo4jMemoryDecayServiceIntegrationTests.cs:121-136`), so a future divergence between repository write-property-names and decay read-names would pass undetected. **Fix:** add one decay test that seeds via the real `UpsertAsync`.
- **[test-2] Resource combined `type`+`userId` and empty/whitespace-`userId` edges untested** (`MemoryResourcesTests` never passes `type:`/`category:`). Low — missing coverage, not a defect. (Verifier note: the resources' `IsNullOrEmpty` check is **identical** to the tools', so there is *no* behavioral divergence — only the whitespace-owner case is universally untested.)
- **[design-1] The read-scoping convention is split and undocumented as a unified rule.** Most reads take `MemoryScope? scope` (null = no filter); two dedup-on-create reads (`FindDuplicateAsync`) and all write-stamps take `string? ownerId` (null = the *shared* bucket). Each method documents its own null meaning, but the **read-vs-write null split is nowhere stated in one place** — and that subtlety is exactly what produced corr-1/corr-2. **Fix:** document the convention once (in `MemoryScope` remarks / design.md). Normalizing `FindDuplicateAsync` to `MemoryScope` is breaking — defer.
- **[rel-meta] Meta-package under-documented:** CHANGELOG.md:28 + the CLI csproj comment say the `AgentMemory` meta bundles 4 packages; it actually has **7** `ProjectReference`s (adds Observability, Enrichment, Extraction.AzureLanguage → transitive OpenTelemetry/Azure.AI.TextAnalytics). **Fix:** update the docs to 7, or drop the 3 to make them opt-in (API-surface decision; defer the latter).

#### ℹ️ INFO

- **[decay-4]** The C# `ComputeScore` clamps `daysSince ≥ 0` but the prune Cypher does not — divergence only ever errs toward *not* pruning (never wrongful deletion). Optional Cypher `CASE` clamp.
- **[corr-4]** `AgentSessionMemoryExtensions.cs:18` `userId` doc says "Null => shared/global knowledge" — defensible shorthand for a dual read+write param, but imprecise for the recall half. Optional polish.
- **[cleanup]** `SchemaQueries.cs:139` has a stale inline comment ("trace owner-write lands in R2") that predates R2 completion — comment lag, not a status overstatement.

### III.2 Doc-drift audit — answering "is it updated? are the 'not started' items actually done?"

**Bottom line: the doc is accurate. No "not started" item is secretly already done, and no "done" item is mislabeled.** Each `❌ Not started` / `⏸ Deferred` / `⬜` marker was verified against the code:

| Item | Doc marker | Verified reality | Should we do it? |
|------|-----------|------------------|-----------------|
| **R6b** GDS analytics | ❌ Not started | **Accurate** — no `AgentMemory.Analytics` project; zero GDS/pagerank/community-detection refs | **No / defer** post-release (must respect `owner_id` when built) |
| **R6c** BenchmarkDotNet harness | ❌ Not started | **Accurate** — no BenchmarkDotNet package or bench project | **Defer** — low value pre-release |
| **R6d** S9 truncation-strategy refactor | ❌ Not started | **Accurate** — truncation logic is still inline in `MemoryContextAssembler` (switch `:320-339`, `TruncateProportional:467`); `TruncationStrategy.cs` is still just the enum; the R1 work only added scope-threading | **Defer** — no pain point; refactor-for-its-own-sake |
| **R7** WorkflowMemory sample | ⏸ Deferred | **Accurate** — no such sample in `samples/` (8 projects, none WorkflowMemory) | **No** — not part of the canonical MAF memory pattern |
| §II.6 Provisioner TOCTOU | ⬜ pending | **Accurate** — `ContainsKey→GetOrAdd` TOCTOU + fuzzy "multi-DB unsupported" string match still present; benign (idempotent) | **Defer** — robustness nicety |
| §II.6 `MigrationRunner.ParseStatements` | ⬜ documented/accepted | **Accurate** — naive `Split(';')`/`//` strip still there, acceptable for current idempotent DDL | **No action** unless a richer migration is needed |
| §II.6 streaming built-in persistence | ⬜ pending | **Accurate** — `StreamingExtractor` is a standalone helper (no persistence/repo refs); R4 DI registration *is* done | **Defer** — genuine enhancement |
| Background embedding backfill | ⬜ intentionally global | **Accurate** — admin batch job; per-owner scoping would defeat batch backfill | **No** — by design (timing side-channel only) |
| R1/R1b/R2/R4/R6a + IC1–IC8 + §II.4 ports + §II.7 | ✅ Done | **Accurate** — spot-checked for overstatement, none found (MemoryScope, owner_id on all 5 node types, owner indexes, migrations 0002–0004, R2 trace owner write+filter, streaming DI, CLI `IsPackable=false`, conflict/consolidation/dedup ports, decay wiring all present) | — |

**Drift actually found and corrected this pass (doc-only):**
1. **Commit count** "~26 commits ahead of `main`" was stale at three locations (lines 8, 48, 370) — the real count is **63** (64 ahead of `origin/main`). Corrected with a date-stamp. The 2.4× growth since the "release cut" is itself a signal to refresh the CHANGELOG before tagging.
2. **§II.6b internal contradiction** — line 407's "spatial write is still incomplete" caveat was already superseded by the "Spatial write gap — closed (2026-06-07)" entry below it (and was imprecise even when written: single `UpsertAsync` always persisted location; only the batch path dropped it). Struck through with a pointer to the resolution.

### III.3 False positives rejected (for the record)

- **"ExpectedQueryCount is cited as both 134 and 137"** — *not* a contradiction. `137` is the end of one changelog delta (`140→137`) and the start of the next (`137→134`); the live value is **134** (`CypherQuerySnapshotTests.cs:40`). The chain `140→137→134` is consistent.
- Two findings were **down-scoped** rather than dropped: the claim that the resource `IsNullOrEmpty` check differs from the tools' (it's identical — only the whitespace case is untested), and the claim that the resolver *auto-merge* branch drops `OwnerId` (it preserves it — only `CreateNewEntityAsync` drops it, i.e. leak-7).

### III.4 Recommended fix sequence

1. **Before any multi-tenant publish:** leak-1 (scope `ContextResource`) + corr-1/corr-2 (fix the two inverted descriptions) + gap-2 (assert `EntityTools` scope translation). Small, high-value, security-relevant.
2. **Isolation completeness:** leak-2 (SK text-search), leak-3 (no-`UserId` extraction entry points + MAF facade), leak-4 (provenance), plus gap-1/gap-4 + gap-overfetch (live isolation tests for the resource surface and the missing repos).
3. **Robustness/edge:** decay-2/decay-3 (null `created_at`, options validation), leak-7 (stamp owner in `CreateNewEntityAsync`), design-1 (document the scope convention), rel-meta (CHANGELOG dep list).
4. **Release process:** open the PR to `main`, reconcile local vs `origin/remediation`, merge, then move/push the tag (rel-3).
5. **Genuinely deferrable post-preview:** R6b/R6c/R6d/R7, provisioner TOCTOU, migration parser, streaming persistence, decay-1, test-1/test-2, decay-4/corr-4/cleanup.

---

*This document supersedes the multi-user/isolation aspects of earlier plans and complements `docs/Implementation_Plan_Remediation.md` (closed), `docs/architecture.md`, and `docs/schema.md`.*
