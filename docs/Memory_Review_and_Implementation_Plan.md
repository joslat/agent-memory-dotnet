# Memory Review & Implementation Plan

> **Author:** Architecture review (Claude) · **Date:** 2026-06-05 · **Branch:** `remediation/analysis-review-hardening`
> **Scope:** A through-and-through review of AgentMemory-for-.NET — what is done, what is pending, and where the gaps are — with a deep dive and concrete implementation plan for the headline topic: **multi-user / multi-session memory isolation** (user-scoped memory, with an *optional* shared/global scope).
>
> **Update 2026-06-06 — ✅ multi-user isolation COMPLETE (R1 + R1b + R2).** R1 core (I1–I9) plus a follow-up multi-agent review and remediation (**IC1–IC8**) closed **all** owner-scoping leaks: vector recall, non-vector lookups (subject/triple/name/type/category/location), GraphRAG (all 4 retrievers), ReasoningTrace, relationships, temporal `AsOf`, and the LLM-invokable facade tools are now owner-scoped (optional shared/global). The two R1b store-tier defects are fixed (IC6 `AsyncLocal` store context; `StoreDatabaseNaming` collision hash). The upstream ports **dedup-on-create** (PR #97) and **consolidation/hygiene** (PR #113) landed in parallel. Verified by full unit + Neo4j integration suites. The narrative below (§1, R7) was written against the *original* gap and is kept for context; **Part II §II.5 is the source of truth for completion status.** Upstream fix proposed at neo4j-labs/agent-memory#137; see [`Remaining_Work_Roadmap.md`](Remaining_Work_Roadmap.md), [`schema-parity-assessment.md`](schema-parity-assessment.md), [`neo4j-pr-howto.md`](neo4j-pr-howto.md).

---

## 1. Summary & recommendation

AgentMemory-for-.NET is **feature-complete for v1 and well-hardened**: 11 packages, ~2,211 unit + 31 SK + 109 integration + 3 performance tests green, real CI, package-boundary guard tests, Testcontainers integration, 9 samples, a 4-phase remediation + adversarial review pass all closed, a clean MAF **1.9.0** migration, and a working file-based migration runner + schema bootstrapper. Code quality is high and the architecture is clean (centralized Cypher constants, a `CypherBuilder`, role-split service interfaces).

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
| R1 | Multi-user memory isolation (`owner_id` + `MemoryScope`, optional shared) | core / neo4j / abstractions / adapters | 🟥 HIGH | ✅ **Done** — ✅I1 abstractions · ✅I3 scoped read path · ✅I4 assembler scope · ✅I5 write path (`owner_id` persisted+read-back on Fact/Entity/Preference; Fact MERGE keyed by `owner_key`; extraction stamps owner from `ExtractionRequest.UserId`) · ✅I6 owner indexes (`fact/entity/preference/trace_owner_idx` in bootstrap) + non-backfilling migration `0002_owner_scope.cypher` (multi-statement runner; `.cypher` ships to output) · ✅I8 adapters surface identity: MAF context + chat-history providers extract `user_id`/`application_id` from the StateBag → `RecallRequest.UserId`/`ExtractionRequest.UserId` (+ optional writable store-context routing); `AgentSession.WithMemoryIdentity(...)` helper; MCP recall already scoped + add_entity/fact/preference now stamp `OwnerId`; SK `recall` gains `userId` · ✅I9 isolation tests: scope-guard unit tests (owner clause present only when scoped; over-fetch topK) + 6 Neo4j integration tests **passing** (A≠B isolation, shared visible to all, unscoped union, same-triple-different-owner = distinct nodes, same-triple-same-owner dedup, over-fetch no-starvation) — **read+write loop closed, indexed, wired through all three adapters, and verified end-to-end**. ✅ **R1 COMPLETE** — all secondary leaks closed in **IC1–IC8** (reasoning traces, relationships, GraphRAG, non-vector lookups, temporal `AsOf`, facade tools); 2211 unit + 109 integration tests green. Full IC detail + remaining design decisions in **Part II (§II.5)**. | L | ✅ Landed before first NuGet publish |
| R1b | Application / memory-store isolation (`ApplicationId` → Neo4j database; shared-db fallback) | core / neo4j / infra | 🟥 HIGH | ✅ **Done** — ✅I2 scaffolding · ✅I7 store routing: store-aware `Neo4jSessionFactory` (per-call DB resolution from `IMemoryStoreContext`+`MemoryStoreOptions`; explicit-DB overload), `StoreDatabaseNaming` (resolve + Neo4j-legal `Sanitize` w/ collision-safe hash on truncation), `IMemoryStoreProvisioner`/`Neo4jMemoryStoreProvisioner` (`CREATE DATABASE … WAIT` on `system`, per-store bootstrap, cache, **actionable `NotSupportedException` on Community**), DI wired (singleton context+provisioner, optional `configureStore`). **`SharedDatabase` default reproduces single-store behavior exactly.** ✅ I8 surfaced `application_id` via the StateBag; ✅ IC6 fixed the captive-singleton store-routing defect (`AsyncLocal` store context). **R1b COMPLETE.** | L | `DatabasePerApplication` (Enterprise/Aura) + provisioner; `SharedDatabase`+`owner_id` on Community; §R1b |
| R2 | Reasoning-trace (+ relationship) isolation | core / neo4j | 🟧 MED | ✅ **Done** — folded into R1: **IC1** ReasoningTrace owner write+recall+delete-decision, **IC2** Relationship edge `owner_id` + `rel_owner_idx` + migration 0003 | M | Done via IC1/IC2 |
| R3 | NuGet release sequencing | packaging / ci | 🟥 HIGH | 🟢 **Unblocked** — R1 API surface landed; packaging + tag-gated `squad-release.yml` done; `NUGET_API_KEY` set. **Only CHANGELOG versioning + a `v*` tag left.** | S | Publish v0.1.0-preview.1 via tag; ids/version freeze on first publish |
| R4 | Streaming extraction DI wiring | core | 🟧 MED | ✅ **Done (2026-06-06)** — `IStreamingExtractor` registered in `AddAgentMemoryCore` (was intentionally held back until R1 landed). It's a text→chunks→entities helper; owner stamping happens at persistence via `ExtractionRequest.UserId`. Interface name kept as `IStreamingExtractor` (final). | S | Registered; `nextsteps.md` updated |
| R5 | Code quality / overall correctness | all | 🟩 INFO | ✅ Strong | — | Reuse existing rails; no rework needed |
| R6a | CLI tool (`migrate` / `schema-check`) | tooling | 🟧 MED | ❌ Not started | M | Post-release ops convenience (R1 migrations landed); not a publish blocker |
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
| IC7 | Tests + docs honesty — ✅ **Done**: each IC1–IC6 shipped with Neo4j integration tests (full suite **103 green**); README gains a "Multi-user & multi-store isolation" section documenting the capability + honest status; `nextsteps.md` gains an R1/R1b tracking row | ✅ Done | `README.md`, `docs/nextsteps.md`, `tests/...` |

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

**Next (optional, feature work — not isolation):** the high-value upstream ports in II.4 (fact/preference dedup-on-create; consolidation/hygiene), then NuGet release (R3).

---

*This document supersedes the multi-user/isolation aspects of earlier plans and complements `docs/Implementation_Plan_Remediation.md` (closed), `docs/architecture.md`, and `docs/schema.md`.*
