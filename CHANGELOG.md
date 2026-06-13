# Changelog

All notable changes to this project will be documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0-preview.2] - 2026-06-13

### Added

- **Schema-parity compatibility kit (TCK) — reusable component + CLI self-check + regression test.** A versioned, drop-in verifier that proves the .NET schema stays compatible with upstream `neo4j-agent-memory`. The frozen upstream `schema.json` snapshots ship as embedded resources (`UpstreamSchemaRegistry`, keyed by version — currently v0.5.0); `SchemaParityVerifier` reflects the live `SchemaConstants` (`DotNetSchema`) and compares labels, relationship types, and property names against a snapshot under a documented divergence `SchemaParityPolicy`, returning a `SchemaParityReport` (breaks vs. intentional divergences). Three surfaces over one engine: (1) a **CLI self-verification** — `agentmemory schema-parity [--upstream-version <v>]` (no Neo4j needed; exit 1 on a break; CI-friendly); (2) a **reusable library component** (`AgentMemory.Neo4j.Schema.Parity`); (3) a **regression test** that asserts current compatibility *and* that the verifier catches each drift class (dropped label, renamed property, undocumented .NET-only type, upstream catching up to a .NET superset). Adding a new upstream version is a drop-in: embed its `schema.json` and register a policy. v0.5.0 result: COMPATIBLE with 8 documented divergences (the `owner_id`/`owner_key`/`invalidated_at` supersets, the `HAS_FACT`/`HAS_PREFERENCE`/`IN_SESSION` extensions, and the `User`/`MemoryReadAudit` omissions).
- **R2 — owner-scoped `ListTracesAsync`/`ListBySessionAsync` (last R1 read gap).** A `session_id` is not a private random handle (it can be shared or guessable), so listing a session's reasoning traces is now owner-scopeable: `IReasoningMemoryService.ListTracesAsync` and `IReasoningTraceRepository.ListBySessionAsync` take an optional `MemoryScope` and, when scoped, return only the owner's own (and optionally shared) traces — never another owner's — mirroring the Fact/Entity/Preference/trace-search R1 pattern. Verified by live-Neo4j tests (alice and bob with traces in the *same* session; alice's scoped list excludes bob's). Relationship and ReasoningTrace owner-*writes* were already complete; this closes the remaining list-level read leak. ReasoningStep/ToolCall reads stay by-parent-handle (reachable only via a random trace/step id obtained through a scoped search — the same exemption as every `GetByIdAsync`), now documented as a deliberate decision.
- **Contradiction → supersession + non-destructive consolidation (D7).** New supersession writers `IFactRepository.SupersedeAsync(loser, winner, scope)` and `IPreferenceRepository.SupersedeAsync(loser, winner, scope)` close a loser **non-destructively** — stamp `invalidated_at` (and, for facts, `valid_until`) so it drops from live recall but is kept and stays visible to as-of recall before supersession — and link `(loser)-[:SUPERSEDED_BY]->(winner)` (new `SchemaConstants.RelationshipTypes.SupersededBy`; mirrors upstream `supersede_preference`). The detect-only `IConflictDetectionService` gains an **opt-in** `ResolveFactContradictionsAsync` that resolves each contradiction group by keeping the highest-confidence assertion and superseding the rest (R1 owner-scoped; detection stays the non-mutating default). The duplicate-preference collapse (`ConsolidationQueries.RemoveDuplicatePreferences`, used by `agentmemory consolidate`) is now **non-destructive** — older duplicates are soft-invalidated and linked `:SUPERSEDED_BY` to the survivor instead of `DETACH DELETE`d, and the pass is idempotent (already-invalidated rows are excluded from grouping). All supersession is owner-scoped (both endpoints must belong to the owner) and idempotent (`coalesce` + `MERGE`). This closes the second destructive path flagged in `docs/bitemporal-memory-assessment.md` — forgetting is now fully reversible. Verified by unit tests (Supersede/dedup Cypher shape, owner scoping) + live-Neo4j integration (supersede drops loser from live recall but keeps it as-of-before and links the winner; conflict resolution keeps the highest-confidence fact and respects owner isolation; non-destructive dedup is idempotent). Design: `docs/bitemporal-memory-assessment.md §8`, plan `docs/Memory_Review_and_Implementation_Plan.md §II.8`.
- **Bitemporal two-clock recall (D6).** Point-in-time recall now spans **two independent clocks**: the **valid-time** clock (`validAsOf` — "what was true in the world", bounding a fact's `valid_from`/`valid_until`) and the **transaction-time** clock (`systemAsOf` — "what the system had recorded", bounding every record's `created_at`/`invalidated_at`). New overloads `IMemoryRecall.RecallAsOfAsync(request, validAsOf, systemAsOf, …)` and `IMemoryContextAssembler.AssembleContextAsOfAsync(request, validAsOf, systemAsOf, …)` let you ask *"what was true at T1, as we believed it at T2"* — reproducing a past decision or auditing a belief before a later correction. The existing single-`asOf` overloads **delegate with both clocks equal**, so existing callers are byte-for-byte unchanged. Clock mapping: facts observe both clocks; messages, entities, preferences, and reasoning traces (no valid-time window) observe only `systemAsOf`. Builds on the D5 `invalidated_at` writer; `IFactRepository.SearchByVectorAsOfAsync` already carried `systemAsOf` — D6 surfaces it through the service + assembler. Verified by unit tests (both clocks propagate distinctly; assembler clock mapping) + a live-Neo4j test pinning all four timestamps and proving each clock filters independently of the other. Design: `docs/bitemporal-memory-assessment.md §8`, plan `docs/Memory_Review_and_Implementation_Plan.md §II.8`.
- **Per-request query-intent presets (D3).** `RecallOptions.Intent` (`RankingIntent.Default`/`Latest`/`Analog`) re-weights a single recall over the configured `MemoryRankingOptions`: `Latest` raises recency (favour fresh), `Analog` zeroes recency so structurally/semantically similar — and possibly *old* — precedents surface (case-based retrieval), `Default` is unchanged. Threaded via a new ambient `IMemoryRankingContext` (AsyncLocal, mirroring the owner/store contexts) that the context assembler publishes per-recall and the long-term repositories read — **no change to `ILongTermMemoryService` or the repository interfaces**. Verified by unit tests (`ForIntent` math, assembler publish-then-reset) + a live-Neo4j test (Latest promotes a fresh memory; Analog keeps the most-similar one on top even over a recency-heavy config).
- **Non-destructive decay + transaction-time clock (D5 + D4) — forgetting is now reversible by default.** New `invalidated_at` transaction-time axis (`SchemaConstants.Properties.InvalidatedAt`): live recall (`SearchByVector` for Fact/Entity/Preference) now excludes soft-invalidated nodes (a no-op for existing data — nothing has it set), while as-of recall keeps them for times *before* invalidation. New owner-scoped `InvalidateAsync` writers on the Fact/Entity/Preference repositories (idempotent `coalesce`; R1-scoped). **Decay pruning is now non-destructive by default** (`MemoryDecayOptions.NonDestructive = true`): low-score nodes are soft-invalidated (kept, recoverable, auditable, dropped from live recall) instead of `DETACH DELETE`d — set `NonDestructive=false` for an explicit hard purge (storage reclamation / GDPR). The non-destructive prune is idempotent (skips already-invalidated nodes). Verified by unit tests + live-Neo4j integration (invalidate hides from live recall but stays as-of-recallable; prune soft-invalidates by default, hard-deletes when opted in; both owner-scoped). This removes the irreversible-deletion behavior flagged as the highest-risk item in `docs/bitemporal-memory-assessment.md`.
- **Retrieval ranking — recency re-ranker (D1) + structural hop-decay (D2), opt-in and schema-neutral.** New `MemoryRankingOptions` (`MemoryOptions.Ranking`) with a `MemoryProfile` capability tier (`Parity` → `Enhanced` → `Bitemporal`) — a "start at parity, dial up" switch. **D1:** when `RecencyWeight > 0`, long-term vector recall (`Fact`/`Entity`/`Preference` `SearchByVector`) blends the already-computed ACT-R retention score (`confidence·e^(−λ·daysSinceAccess) + boost·access`, clamped to [0,1]) into ranking: `(1−w)·vectorScore + w·retentionScore`. **D2:** when `StructuralDecayGamma < 1`, GraphRAG `Graph`-mode traversal scores a neighbour at `h` hops as `seedScore·γ^h` (the previously-discarded hop distance). Both default **off** (`MemoryProfile.Parity` ⇒ weight 0 / γ 1.0) ⇒ byte-for-byte today's semantic-only ranking, and add **no** node property, label, index, or migration — so a profile can be raised over an existing (or upstream-parity-seeded) graph with no schema change. Verified by unit tests (query shape, profile/clamp) and live-Neo4j integration tests (recency reorders a stale top-similarity hit below a fresh one; γ halves a 1-hop neighbour's score). Design: `docs/decay-improvement-proposal.md §11`, plan `docs/Memory_Review_and_Implementation_Plan.md §II.8`.

## [0.1.0-preview.1] - 2026-06-06

First public preview release. This is a pre-release; public APIs may still change before 1.0.
NuGet package IDs are permanent once published.

### Added

#### Packages

- **`AgentMemory.Abstractions`** — Domain models (31 types across 3 memory tiers), service interfaces (`IMemoryService`, `IShortTermMemoryService`, `ILongTermMemoryService`, `IReasoningMemoryService`, `IMemoryContextAssembler`, `IMemoryExtractionPipeline`, `IEntityResolver`, and more), repository interfaces, and configuration options. Zero external dependencies except `Microsoft.Extensions.AI.Abstractions`.
- **`AgentMemory.Core`** — Memory service implementations, extraction pipeline (`ExtractionStage` → `PersistenceStage`), entity resolution chain (Exact → Fuzzy → Semantic → CreateNew), context assembler with token-budget enforcement, memory decay service (`MemoryDecayService` with configurable half-life), stub implementations for testing.
- **`AgentMemory.Neo4j`** — Neo4j repository implementations for all 9 domain repositories, centralised Cypher constants (145 in 14 domain files), schema bootstrapper and migration runner with versioned `.cypher` files, GraphRAG retrieval layer (Vector, Fulltext, Hybrid, Graph) internalized from `neo4j-maf-provider`. DI: `AddNeo4jAgentMemory()`.
- **`AgentMemory.Extraction.Llm`** — LLM-driven entity, fact, preference, and relationship extractors using `IChatClient` from `Microsoft.Extensions.AI`. DI: `AddLlmExtraction()`.
- **`AgentMemory.Extraction.AzureLanguage`** — Azure Text Analytics extractors for named entity recognition, fact extraction, and PII detection. DI: `AddAzureLanguageExtraction()`.
- **`AgentMemory.AgentFramework`** — Microsoft Agent Framework adapter: `Neo4jMemoryContextProvider` (`IContextProvider`), `Neo4jChatMessageStore`, `Neo4jMicrosoftMemoryFacade`, `MemoryToolFactory` (6 `AIFunction` tools), `AgentTraceRecorder`. DI: `AddAgentMemoryFramework()`.
- **`AgentMemory.SemanticKernel`** — Semantic Kernel adapter: memory plugin, text search, native SK DI integration. DI: `AddAgentMemorySemanticKernel()`.
- **`AgentMemory.Enrichment`** — Nominatim geocoding service and Wikimedia entity enrichment, both with caching and rate limiting. DI: `AddEnrichment()`.
- **`AgentMemory.Observability`** — OpenTelemetry decorator pattern wrapping `IMemoryService` and `IGraphRagContextSource` with distributed tracing spans and metrics. DI: `AddAgentMemoryObservability()`.
- **`AgentMemory.McpServer`** — MCP server with 21 tools, 6 resources (`memory://conversations`, `memory://entities`, `memory://preferences`, `memory://context/{sessionId}`, `memory://status`, `memory://schema`), and 3 prompts. Supports stdio and HTTP transports. DI: `AddAgentMemoryMcpTools()`.
- **`AgentMemory`** — Convenience meta-package bundling `Abstractions` + `Core` + `Neo4j` + `Extraction.Llm` + `Observability` + `Enrichment` + `Extraction.AzureLanguage` (7 project references; pulls their transitive deps, e.g. OpenTelemetry and Azure.AI.TextAnalytics). Single install for the most common use case.

#### Memory capabilities

- **Short-term memory** — session-scoped conversation history with participant tracking, recent message recall, semantic vector search, batch add
- **Long-term memory** — entities with canonical names, aliases, and dynamic labels; facts as SPO triples with confidence and validity periods; preferences by category; relationships between entities; all backed by vector and fulltext search
- **Reasoning memory** — reasoning traces from agent chains, steps (thought/action/observation), tool call recording with status and outcomes, similar-trace retrieval
- **Memory decay (scoring + pruning)** — exponential decay-score formula (`confidence × exp(−λ×days) + boost×access`) with configurable half-life, access-tracking, and server-side prune. The Neo4j adapter (`Neo4jMemoryDecayService`) runs the decay Cypher and is wired by default (it `Replace`s the portable Core no-op). Pruning is **owner-scoped**: `PruneExpiredMemoriesAsync(MemoryScope? scope)` deletes the owner's own low-score nodes only (never another owner's, never shared/global); a null scope prunes globally (admin). Exposed via `agentmemory decay [--owner <id>]`.
- **Temporal recall** — `RecallAsOfAsync` and point-in-time snapshot queries across all memory tiers using native Neo4j `datetime()` comparisons
- **Context assembly** — multi-tier recall with configurable token budget, truncation strategies, and blending modes
- **Metadata filtering** — `MetadataFilterBuilder` with `$eq`, `$ne`, `$contains`, `$in`, `$exists` operators
- **Session ID strategies** — `PerConversation`, `PerDay`, and `PersistentPerUser` via `ISessionIdGenerator`

#### Multi-user & multi-store isolation

- **Multi-user memory isolation (R1).** Memories now carry an optional `owner_id` so a single store can
  hold per-user memory alongside shared/global memory. Reads are scoped through **`MemoryScope`**
  (`{ string? OwnerId, bool IncludeShared = true }`): `owner_id = $ownerId OR (includeShared AND owner_id
  IS NULL)`. A `null` owner means shared/global (the prior behaviour), so existing single-tenant callers
  are unaffected. Facts additionally carry an `owner_key = coalesce(owner_id, '*')` sentinel that is part
  of the Fact MERGE key, so the same SPO triple asserted by different owners stays distinct. Vector-index
  reads over-fetch (topK × 5, floor 50) and post-filter by owner to avoid per-owner starvation. Every
  read/write surface — short-term, long-term (entities, facts, preferences, relationships), reasoning
  traces/steps, temporal recall, and all four retrievers — is owner-aware.
- **Ambient owner context (`IMemoryOwnerContext` / `IWritableMemoryOwnerContext`).** `AsyncLocal`-backed
  `DefaultMemoryOwnerContext` lets adapters set the current user once per request/agent flow so the
  LLM-invokable facade tools scope by owner **without trusting the model** to pass an id. Safe to register
  as a singleton (the value flows per async context, not process-wide).
- **Multi-store / application isolation (R1b).** `MemoryStorageStrategy` (`SharedDatabase` |
  `DatabasePerApplication`) plus `IMemoryStoreContext` / `IMemoryStoreProvisioner` map an `ApplicationId`
  to a Neo4j database, with an `AsyncLocal`-backed `DefaultMemoryStoreContext` ambient. Defaults to
  `SharedDatabase` inheriting `Neo4jOptions.Database`, so existing deployments are unchanged.

#### Consolidation, dedup & extraction

- **Dedup-on-create for facts and preferences.** New `LongTermMemoryOptions`
  (`DeduplicateOnCreate = true`, `DeduplicationSimilarityThreshold = 0.95`, `DeduplicationConfidenceBump
  = 0.05`): on add, a near-duplicate (same subject/predicate/owner for facts; same category/owner for
  preferences, above the similarity threshold) is updated in place with a confidence bump instead of
  creating a second node.
- **Consolidation / hygiene service (`IConsolidationService`).** Opt-in maintenance pass
  (`ConsolidationOptions`, `DryRun = true` by default) that archives expired conversations, removes
  duplicate preferences, and reports duplicate entities and over-long reasoning traces, emitting a
  `ConsolidationReport` and recording each run. Backed by migration `0004_consolidation.cypher`.
- **Conflict / contradiction detection (`IConflictDetectionService`).** Detect-only (never mutates):
  finds fact contradictions — same subject + predicate within an owner scope asserting ≥2 distinct
  objects — grouped per owner so it respects R1 isolation, with an optional confidence gate. Pairs with
  the consolidation service for the memory-hygiene story. Exposed via the `agentmemory conflicts` CLI command.
- **Streaming (chunked) extraction is DI-registered** (`IStreamingExtractor`). It is a standalone
  text → chunks → entities helper and does **not** persist; callers persist its output through their
  own ingestion path (where owner stamping applies). A built-in streaming persistence path is not
  yet wired.
- **`Conversation.Archived` reads back.** The consolidation pass sets `archived` on expired
  conversations; the domain model and `Neo4jConversationRepository` mapping now surface it (archival
  remains a consolidation-only write, not an upsert). `ConsolidationRun` is now declared in
  `SchemaConstants.NodeLabels` for parity with the Cypher that creates it.
- **MCP `memory_add_fact` accepts `category` and `metadata`.** The tool now surfaces the `Fact.Category`
  and `Fact.Metadata` fields (metadata as a JSON-object string) that were previously dropped.

#### Operational safety

- **Vector-index dimension validation at bootstrap.** Schema bootstrap (and per-application store
  provisioning) verifies every existing Neo4j vector index was created with the configured
  `Neo4jOptions.EmbeddingDimensions`, throwing `EmbeddingDimensionMismatchException` (which lists each
  offending index) when they differ. Because `CREATE VECTOR INDEX ... IF NOT EXISTS` never alters an
  existing index, switching embedding models would otherwise produce an opaque query-time failure; this
  is a fail-fast guard. Opt out via `Neo4jOptions.ValidateVectorIndexDimensions = false` (default `true`).

#### Reasoning provenance

- **`:TOUCHED` reasoning-audit edges.** `IReasoningMemoryService.RecordTouchedEntitiesAsync` /
  `GetTouchedEntitiesAsync` record and read which entities a reasoning step read or acted upon, as
  `(:ReasoningStep)-[:TOUCHED]->(:Entity)` edges (a `recorded_at` timestamp is stamped on create).
  Linking is by entity id to **existing** entities (it never creates entities, preserving the
  resolution/dedup pipeline), is idempotent, and silently skips ids that do not resolve. Ports the
  upstream `(:ReasoningStep)-[:TOUCHED]->(:Entity)` provenance edge (neo4j-labs/agent-memory PR #113).
- **Point-in-time reasoning-trace recall.** `IReasoningMemoryService.SearchSimilarTracesAsOfAsync` and
  `IReasoningTraceRepository.SearchByTaskVectorAsOfAsync` restrict task-vector search to traces that had
  started at or before the as-of instant, and `MemoryContextAssembler.AssembleContextAsOfAsync` now
  includes reasoning traces (previously omitted) — completing temporal recall across all memory tiers.

#### Entity auditability & feedback

- **`memory_get_entity_provenance` MCP tool.** Surfaces the (already-implemented) `EntityProvenance` —
  the source messages an entity was extracted from (with span/confidence) and the extractors that
  produced it — for auditability.
- **`Entity.UpdatedAtUtc` reads back.** Entity nodes already stamped `updated_at` on modification; the
  domain model and `Neo4jEntityRepository` mapping now surface it (last-modified semantics — null until
  first update), exposed on `memory_get_entity`.
- **Entity feedback.** `ILongTermMemoryService.RecordEntityFeedbackAsync` (and the
  `memory_record_entity_feedback` MCP tool) nudge an entity's confidence — positive reinforces, negative
  penalizes — clamped to [0,1], with the magnitude configurable via
  `LongTermMemoryOptions.FeedbackConfidenceDelta` (default 0.1). Owner-scoped (R1): with a `userId`/scope
  it only affects the user's own or shared entities, never another user's private entity.

#### Operational tooling

- **`agentmemory` CLI** (`tools/AgentMemory.Cli`) — an operations command-line front end over the
  shipped maintenance services: `migrate` (apply Cypher migrations), `bootstrap` (create schema
  constraints/indexes), `consolidate [--apply]` (memory-hygiene pass, dry-run by default),
  `conflicts` (detect fact contradictions), and `decay [--owner <id>]` (prune decayed memories;
  owner-scoped, or global when omitted). Connection resolves from CLI options, `Neo4j:*`
  config, or `NEO4J_*` env vars. Built for CI/CD migrations, K8s init containers, and scheduled
  pruning. (Not a published NuGet package.)

#### Search and retrieval

- Vector similarity search across all memory layers (5 indexes + reasoning-step index)
- Fulltext BM25 search (3 indexes: message content, entity name, fact content)
- Hybrid retrieval (vector + BM25 combined with max-score merge)
- Graph multi-hop traversal (`RELATED_TO*1..2`) via `Neo4jGraphRagContextSource`
- Temporal point-in-time retrieval for entities, facts, and preferences

#### Graph schema

- 13 node labels, 11 uniqueness constraints
- 6 vector indexes, 3 fulltext indexes, 1 geospatial Point index, plus owner-scope and consolidation property indexes
- Versioned migration runner (`MigrationRunner`) with `.cypher` migration files

#### Testing

- Extensive unit test suite covering all packages including stub-based tests without external services
- Integration test suite using Testcontainers (disposable Neo4j 5 containers)
- Semantic Kernel adapter unit tests
- Cypher snapshot tests for query validation

### Changed

> The items below describe API shaping done before first publish.

- **`IMemoryService` split into role interfaces** (`AgentMemory.Abstractions`). Its members are now
  declared on three focused interfaces — `IMemoryRecall` (read), `IMemoryIngestion` (write), and
  `IMemoryMaintenance` (upkeep) — and `IMemoryService` composes all three
  (`IMemoryService : IMemoryRecall, IMemoryIngestion, IMemoryMaintenance`). Consumers of
  `IMemoryService` are source-compatible (all members remain available); new code can depend on a
  narrow role for ISP. DI binds all three roles to the same scoped instance.
- **`IEmbeddingOrchestrator` slimmed to two primitives** (`AgentMemory.Abstractions`). The interface
  now declares only `EmbedAsync(string)` and the new `EmbedBatchAsync(IReadOnlyList<string>)`. The
  six domain-specific methods (`EmbedEntityAsync`, `EmbedFactAsync`, `EmbedPreferenceAsync`,
  `EmbedMessageAsync`, `EmbedQueryAsync`, `EmbedTextAsync`) are preserved as **extension methods**
  in `EmbeddingOrchestratorExtensions` (same namespace), so call sites that `using
  AgentMemory.Abstractions.Services` are source-compatible. Code that *implements* or *mocks*
  `IEmbeddingOrchestrator` must now implement/mock `EmbedAsync`/`EmbedBatchAsync`.
- Renamed all NuGet packages from `Neo4j.AgentMemory.*` to `AgentMemory.*` to remove the implied Neo4j
  affiliation before first publish. C# namespaces updated accordingly across all 11 source packages, 3
  test projects, and 3 sample projects.
- **Retrieval blend modes are now enforced.** `RecallOptions.BlendMode` previously had no effect —
  every mode behaved like `Blended`. `MemoryContextAssembler` now honors it: `MemoryOnly` suppresses
  GraphRAG, `GraphRagOnly` suppresses the memory layers (and the query-embedding call), and
  `GraphRagOnly`/`GraphRagThenMemory` render GraphRAG context ahead of memory in both
  `MemoryContextFormatter` and the MAF context mapper. `MemoryContext` gains a `BlendMode` property
  (defaults to `Blended`, so existing output ordering is unchanged).
- **Microsoft Agent Framework (MAF) 1.9.0** (from 1.1.0) and `Microsoft.Extensions.AI.Abstractions`
  10.5.1 (from 10.4.1, the floor MAF 1.9.0 requires). The migration was source-compatible — no adapter
  code changes — see `docs/archive/maf-1.9.0-migration.md`.

### Fixed

- **Batch entity upsert now persists geospatial `location`.** `Neo4jEntityRepository.UpsertBatchAsync`
  set embeddings, labels, and provenance but silently dropped `Latitude`/`Longitude`, so entities
  created via the batch path had no `location` point and were invisible to `SearchByLocationAsync` /
  `SearchInBoundingBoxAsync` (single `UpsertAsync` already persisted it). The batch now writes the
  point for every entity with both coordinates, matching the single path. Covered by single + batch
  round-trip integration tests (model coords → read-back → spatial search) and batch unit tests.
- **Owner-scoped entity resolution (R1 isolation hardening — fixes a cross-owner write-path leak).**
  Entity resolution fetched its candidate set via an unscoped read, so when extracting for user A an
  incoming entity could exact/fuzzy/semantic-match onto user B's **private** entity and auto-merge into
  it (aliases/sources appended; the foreign node's `owner_id` then re-stamped at persistence). The
  resolution read is now owner-scoped: `MemoryScope?` flows through `IEntityResolver.ResolveEntityAsync` /
  `FindPotentialDuplicatesAsync`, the extraction stage, and `MemoryExtractionPipeline` (derived from
  `ExtractionRequest.UserId`), down to a new owner-conditional `IEntityRepository.GetByTypeAsync(…, scope)`.
  Shared/global entities (`owner_id IS NULL`) stay matchable by everyone; a null scope reproduces the
  prior single-tenant behavior. Proven by live cross-owner resolution-isolation tests.
- **Scope-optional hooks on dedup/name reads (R1).** `Neo4jEntityRepository.SearchByNameAsync`,
  `IEntityRepository.FindSimilarByEmbeddingAsync`, and `IFactRepository.FindByTripleAsync` gained an
  optional `MemoryScope` (default global) so a future user-facing wiring can confine them; the entity
  name search also fixes a latent precedence bug by parenthesizing the name/canonical-name OR. The
  remaining unscoped reads (by-id lookups, background embedding back-fill, and admin dedup/provenance
  surfaces) are intentionally global — documented inline, since the lookup key is itself an owned handle
  or the surface is operator-only.
- **MCP resources `memory://entities` and `memory://preferences` are now owner-scopable (R1).** Both
  listed every owner's nodes via raw Cypher with no owner filter and no user parameter — a cross-owner
  read leak (preference free-text is sensitive). They now accept an optional `userId` that confines the
  listing to that owner's plus shared rows; omitting it stays unscoped (admin/single-tenant), consistent
  with the MCP tools. `memory://status` stays global (aggregate counts only).
- **Cross-owner write/delete/merge denial (R1 isolation hardening).** `IEntityRepository.DeleteAsync` /
  `IFactRepository.DeleteAsync` / `IPreferenceRepository.DeleteAsync` / `ILongTermMemoryService.DeletePreferenceAsync`,
  `Neo4jEntityRepository.MergeEntitiesAsync`, and the spatial reads (`SearchByLocationAsync` /
  `SearchInBoundingBoxAsync`) now take an optional `MemoryScope`. When scoped, a delete only removes the
  owner's **own** node (never another owner's, and never shared/global data), a merge cannot cross the
  owner boundary, and spatial search can't enumerate another owner's locations. Previously these matched
  by id/coordinates with no owner check — a destructive multi-tenant gap. Unscoped (null) stays
  admin/global for back-compat. Covered by cross-owner integration tests.
- **The meta `AddNeo4jAgentMemory` is now self-sufficient.** It now registers default `IClock`
  (`SystemClock`) and `IIdGenerator` (`GuidIdGenerator`) via `TryAdd`, so consolidation, reasoning,
  the context assembler, and dedup resolve out of the box. Previously these were *registered* but not
  *resolvable* — every consumer (and every sample) had to register the two primitives by hand. Consumers
  can still override by registering their own first.
- **`DatabasePerApplication` provisioning now works.** `Neo4jMemoryStoreProvisioner` inlined the store
  database name into `CREATE DATABASE … IF NOT EXISTS WAIT` unquoted. The default `DatabasePrefix` is
  `mem-`, so every provisioned name contains a dash — which is a Cypher syntax error unquoted, breaking
  store provisioning for all real `DatabasePerApplication` users. The name is now backtick-quoted.
  (Caught by a new live Neo4j Enterprise integration test; previously only mock-tested.)
- **Memory-only DI now works.** Two `AddAgentMemoryCore` registrations failed at runtime for consumers
  that don't add the GraphRAG adapter: `MemoryContextAssembler` required `IGraphRagContextSource`
  (now resolved optionally via `GetService`), and `IMemoryExtractionPipeline` was registered by type
  despite an internal constructor (now registered via a factory).
- **`ReasoningStep.TimestampUtc` / `ToolCall.TimestampUtc` are now read back.** Both nodes are created
  with a server `timestamp` that was previously write-only from .NET; the domain records gained the
  property and the Neo4j mappers now populate it.
- **`Fact.Category` is now persisted and read back.** It was defined on the domain model and indexed
  (`fact_category`) but omitted from the upsert queries and mapping, so it was silently dropped on
  write and always returned `null`. `FactQueries.Upsert`/`UpsertBatch`, the repository parameters, and
  `MapToFact` now round-trip it (mirroring `Preference.Category`).
- **CI now builds and tests.** `.github/workflows/squad-ci.yml` was a placeholder that ran no
  commands; it now restores, builds, and runs unit + SemanticKernel tests plus the Testcontainers
  integration suite. Fixed `Directory.Build.props` so the src-only `TreatWarningsAsErrors` condition
  evaluates correctly on non-Windows CI runners, and tagged `Neo4jConnectivityTests` with
  `[Trait("Category", "Integration")]` so it no longer leaks into the unit-test filter.

---

[Unreleased]: https://github.com/joslat/agent-memory-dotnet/compare/v0.1.0-preview.1...HEAD
[0.1.0-preview.1]: https://github.com/joslat/agent-memory-dotnet/releases/tag/v0.1.0-preview.1
