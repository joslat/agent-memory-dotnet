# Architecture Overview — Agent Memory for .NET

**Last Updated:** 2026-07-11 (preview.4 reality check)
**Author:** Jose Luis Latorre Millas
**Canonical Specification:** [specification.md](specification.md)

---

## 1. Vision & Goals

### What It Is

Agent Memory for .NET is a **native .NET implementation of graph-native persistent memory for AI agents**, backed by Neo4j. It provides three memory layers — short-term (conversations), long-term (entities, facts, preferences, relationships), and reasoning (traces, steps, tool calls) — that persist across agent sessions and runs. The system is designed as a **framework-agnostic core** with an adapter model that enables integration with Microsoft Agent Framework, GraphRAG, MCP, and future frameworks. *(Spec §1.2–1.3)*

### What It Provides

- **Three-layer memory model**: short-term, long-term, and reasoning memory — each with dedicated domain types, repositories, and services *(Spec §3.1)*
- **Framework-agnostic core**: the memory engine has zero dependencies on MAF, GraphRAG SDKs, or any AI framework *(Spec §2.4)*
- **Adapter model**: MAF, GraphRAG, and MCP are thin adapter layers that depend inward on the core — never the reverse *(Plan §7.4)*
- **Neo4j graph-native persistence**: direct Neo4j driver usage, no ORM, with schema bootstrapping and migration support *(Plan §7.3)*
- **Context assembly**: configurable recall with budget enforcement and truncation strategies *(Spec §3.4, Plan §14)*
- **Extraction pipeline**: pluggable extraction from conversations to structured long-term memory *(Plan §13)*
- **Owner/store scoping**: `MemoryScope`/`owner_id` isolation runs through the repository, recall,
  GraphRAG, reasoning, and maintenance layers, but it is opt-in per call — a null scope (the
  backward-compatible default) is global, not isolated. Multi-tenant hosts must establish an owner scope
  for every agent run; see [Owner isolation](getting-started.md#owner-isolation) and the
  [threat model](security/threat-model.md) for exactly which isolation properties hold today, which are
  partial, and which are open gaps with an owner and a plan.

### What It Does NOT Do

- **No Python runtime** — purely .NET, no Python bridge or subprocess *(Spec §1.4)*
- **No bundled LLM** — extraction and embedding providers are pluggable interfaces *(Decision D5)*
- **No fork of upstream Python agent-memory** — inspired by its architecture, not a port *(Spec §0.1)*
- **Not an official Neo4j product** — independent community project *(Spec §1.1)*

---

## 2. Layered Architecture

### 2.1 Package Dependency Diagram

```
┌─────────────────────────────────────────────────────────────────────┐
│                        ADAPTERS (Phase 3–6)                         │
│                                                                     │
│  ┌─────────────────────┐  ┌──────────────────────┐  ┌───────────┐  │
│  │ AgentMemory.        │  │ AgentMemory.          │  │ AgentMem. │  │
│  │ AgentFramework      │  │ SemanticKernel        │  │ McpServer │  │
│  │                     │  │                       │  │           │  │
│  │ + Microsoft.Agents  │  │ + Microsoft.          │  │ + MCP SDK │  │
│  │   .AI.*             │  │   SemanticKernel.*    │  │           │  │
│  └────────┬────────────┘  └─────────┬─────────────┘  └─────┬─────┘  │
│           │                         │                       │        │
│           └─────────────┬───────────┘───────────────────────┘        │
│                         │  depends inward                            │
│                         ▼                                            │
├─────────────────────────────────────────────────────────────────────┤
│                 EXTENSIONS & CROSS-CUTTING (Phase 4–5)               │
│                                                                     │
│  ┌───────────────────────────────────────────────────────────────┐ │
│  │ AgentMemory.Analytics  (optional GDS: PageRank + Louvain)     │ │
│  │ deps: Abstractions + Neo4j; graceful no-op w/o GDS plugin     │ │
│  └───────────────────────────────────────────────────────────────┘ │
│                                                                     │
│  ┌──────────────────────┐  ┌──────────────────────┐  ┌───────────┐ │
│  │ Observability        │  │ Extraction.          │  │Enrichment │ │
│  │ (OTel decorators)    │  │ AzureLanguage        │  │(Geocoding)│ │
│  │                      │  │ (Azure Text Analytics│  │           │ │
│  │ + OpenTelemetry.Api  │  │                      │  │ + Nominat │ │
│  │   1.12.0             │  │ + Azure.AI.TextAnal) │  │ + Wikimed │ │
│  └──────────┬───────────┘  └──────────┬───────────┘  └─────┬─────┘ │
│             │                         │                    │         │
│             └─────────────┬───────────┘────────────────────┘         │
│                           │  decorates / extends                     │
│                           ▼                                          │
├─────────────────────────────────────────────────────────────────────┤
│                    INFRASTRUCTURE (Phase 1)                          │
│                                                                     │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │  AgentMemory.Neo4j                                    │   │
│  │  (persistence — repositories, Cypher, schema, transactions) │   │
│  │                                                              │   │
│  │  + Neo4j.Driver 6.0.0                                       │   │
│  │  + Microsoft.Extensions.DI/Logging/Options 10.0.5           │   │
│  └──────────────────────┬───────────────────────────────────────┘   │
│                         │  depends on                               │
│                         ▼                                           │
├─────────────────────────────────────────────────────────────────────┤
│                    ORCHESTRATION (Phase 1)                           │
│                                                                     │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │  AgentMemory.Core                                     │   │
│  │  (services, stubs, validation, context assembly)            │   │
│  │                                                              │   │
│  │  + Microsoft.Extensions.DI/Logging/Options 10.0.5           │   │
│  └──────────────────────┬───────────────────────────────────────┘   │
│                         │  depends on                               │
│                         ▼                                           │
├─────────────────────────────────────────────────────────────────────┤
│                    FOUNDATION (Phase 1)                              │
│                                                                     │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │  AgentMemory.Abstractions                             │   │
│  │  (domain models, service interfaces, repository interfaces, │   │
│  │   configuration options — IGeocodingService,                │   │
│  │   IEnrichmentService added Phase 5)                         │   │
│  │                                                              │   │
│  │  One approved external dep: M.E.AI.Abstractions 10.5.1      │   │
│  └──────────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────────┘
```

### 2.2 Dependency Direction Rule

**Dependencies flow strictly inward.** Adapters (MAF, SemanticKernel, Observability, MCP) depend directly
on Core, never on Neo4j or on each other. Neo4j depends on Core and Abstractions as its own, separate
branch. Core depends on Abstractions. Never the reverse. See the diagram below.

```mermaid
graph TD
    MAF["MAF Adapter<br/>(Phase 3)"] --> Core
    SK["SemanticKernel Adapter<br/>(Phase 6)"] --> Core
    OBS["Observability<br/>(Phase 4)"] --> Core
    MCP["MCP Server<br/>(Phase 6)"] --> Core
    Neo4j["AgentMemory.Neo4j<br/>(+ GraphRAG retrieval)"] --> Core
    Neo4j --> Abs
    Core["AgentMemory.Core"] --> Abs
    Abs["AgentMemory.Abstractions<br/>(M.E.AI.Abstractions only)"]
    OBS -. decorates .-> MAF
    OBS -. decorates .-> Neo4j
```

---

## 3. Package Responsibilities

### 3.1 AgentMemory.Abstractions

| Attribute | Value |
|---|---|
| **Purpose** | Domain contracts — all models, interfaces, and configuration types shared across the system |
| **Dependencies** | **Microsoft.Extensions.AI.Abstractions** 10.5.1 (approved, D-AR2-1) — .NET BCL otherwise (multi-targets net8.0/net9.0/net10.0) |
| **MUST NOT reference** | Neo4j.Driver, Microsoft.Agents.*, any GraphRAG SDK, any MCP SDK, any NuGet package **except** Microsoft.Extensions.AI.Abstractions |
| **Key types** | 49 domain records (Conversation, Message, Entity, Fact, Preference, Relationship, MemoryHistoryQuery, MemoryHistoryRecord, ReasoningTrace, ReasoningStep, ToolCall, ToolCallStats, IngestionItemOutcome, etc.), 39 service interfaces (incl. `IMemoryIsolationPolicy`, #100), 11 repository interfaces, 16 configuration types (incl. `MemoryRankingOptions`, `MemoryIsolationOptions`), 23 enums (incl. `MemoryProfile`, `RankingIntent`, `DuplicateStatus`, `EntityMatchType`, `MemoryNodeKind`, `MemoryOperationAccess`, `MemoryIsolationMode`, `IngestionStatus`, `IngestionStage`, `IngestionItemStatus`, `MemoryItemKind`, `IngestionFailureMode`) (see the catalogs in `design.md §5/§6` for the authoritative, per-type list) |

**Namespace structure:**
```
AgentMemory.Abstractions.Domain        — records and enums
AgentMemory.Abstractions.Services      — service interfaces
AgentMemory.Abstractions.Repositories  — repository interfaces
AgentMemory.Abstractions.Options       — configuration records
```

### 3.2 AgentMemory.Core

| Attribute | Value |
|---|---|
| **Purpose** | Orchestration — service implementations, extraction pipeline, context assembly, stubs |
| **Dependencies** | Abstractions (project ref), Microsoft.Extensions.AI.Abstractions 10.5.1, Microsoft.Extensions.DependencyInjection.Abstractions 10.0.5, Microsoft.Extensions.Logging.Abstractions 10.0.5, Microsoft.Extensions.Options 10.0.5, FuzzySharp |
| **MUST NOT reference** | Neo4j.Driver, Microsoft.Agents.*, any GraphRAG SDK |
| **Key types** | SystemClock, GuidIdGenerator, StubEmbeddingGenerator, EmbeddingOrchestrator, StubExtractionPipeline, StubEntityExtractor, StubFactExtractor, StubPreferenceExtractor, StubRelationshipExtractor, StubEntityResolver |

#### 3.2.1 Ingestion outcomes (#101)

`ExtractAndPersistAsync`/`IMemoryExtractionPipeline.ExtractAsync` extract and persist independent
items (entities, facts, preferences, relationships) from a batch of messages. Individual items can
fail at any stage — an extractor throws, an entity fails validation or resolution, a node fails to
persist, or its `EXTRACTED_FROM` provenance edge fails after the node itself succeeded — and by
default (`IngestionFailureMode.BestEffort`) the pipeline keeps going: one bad item must not discard
several good ones. `ExtractionResult` makes that visible instead of only logging it:

- `Status` (`IngestionStatus`: `Succeeded` / `PartiallySucceeded` / `Failed`) — derived from
  `Outcomes`: any `Failed` item outcome makes it at least `PartiallySucceeded`; `Failed` overall only
  when *nothing* succeeded. Nothing to ingest is `Succeeded`, not `Failed`.
- `Outcomes` (`IReadOnlyList<IngestionItemOutcome>`) — one entry per meaningful event (a success, a
  validation/resolution skip, or a failure), each carrying `Kind` (entity/fact/preference/
  relationship), `Stage` (extraction/validation/resolution/persistence/provenance/relationship-
  persistence), a stable `ErrorCode` (`MemoryErrorCodes`, all `MEMORY_`-prefixed), and `Retryable`.
  Routine confidence-threshold filtering is deliberately **not** recorded — it's expected pipeline
  behavior, not a failure or a meaningful skip.

Set `ExtractionOptions.FailureMode = IngestionFailureMode.FailFast` to instead stop at the first
failure and throw `MemoryIngestionException`, which carries every `IngestionItemOutcome` completed
before the failure (`CompletedOutcomes`) so a caller can see exactly how far ingestion got. The four
extractor categories (entity/fact/preference/relationship) run concurrently and can't be pre-empted
mid-flight, so fail-fast takes effect at the next stoppable point: once all four finish, or inside
the sequential per-item resolution/persistence loops. `OperationCanceledException` always propagates
as itself — under either mode — never converted into a partial or failed outcome.

Item-level transactionality (e.g. a fact and its provenance edges committing atomically) is an
explicit non-goal of #101 — Neo4j write ordering here is still best-effort, not atomic, and nothing
in the API claims otherwise.

### 3.3 AgentMemory.Neo4j

| Attribute | Value |
|---|---|
| **Purpose** | Persistence — Neo4j repository implementations, Cypher queries, schema management, driver infrastructure |
| **Dependencies** | Abstractions (project ref), Core (project ref), Neo4j.Driver 6.0.0, Microsoft.Extensions.AI.Abstractions 10.5.1, Microsoft.Extensions.DependencyInjection.Abstractions 10.0.5, Microsoft.Extensions.Logging.Abstractions 10.0.5, Microsoft.Extensions.Options 10.0.5 |
| **MUST NOT reference** | Microsoft.Agents.* |
| **Key types** | Neo4jDriverFactory, Neo4jSessionFactory, Neo4jTransactionRunner, SchemaBootstrapper, MigrationRunner, Neo4jOptions, ServiceCollectionExtensions |

### 3.4 Adapter Packages

#### 3.4.1 AgentMemory.AgentFramework (Phase 3 ✅ COMPLETE)

| Attribute | Value |
|---|---|
| **Purpose** | Thin adapter layer exposing memory capabilities to Microsoft Agent Framework |
| **Dependencies** | Abstractions (project ref), Core (project ref), Neo4j (project ref), Microsoft.Agents.AI.Abstractions 1.9.0, Microsoft.Extensions.DependencyInjection.Abstractions 10.0.5, Microsoft.Extensions.Logging.Abstractions 10.0.5, Microsoft.Extensions.Options 10.0.5 |
| **MUST NOT reference** | Business logic — act only as a type mapper and adapter |
| **Key types** | `Neo4jMemoryContextProvider` (extends `AIContextProvider`), `Neo4jChatMessageStore`, `Neo4jMicrosoftMemoryFacade`, `MafTypeMapper` (bidirectional `ChatMessage` ↔ `Message` mapping), `MemoryToolFactory` (6 tools), `AgentTraceRecorder`, `IAutomaticRecallPolicy` (#88) and its `ConfiguredAutomaticRecallPolicy`/`HeuristicAutomaticRecallPolicy` implementations, `IMemoryContextAdmissionPolicy` (#92 Phase 2) and its `DefaultMemoryContextAdmissionPolicy` implementation |
| **Core responsibility** | Bridge between Microsoft Agent Framework lifecycle (`ProvideAIContextAsync`, `StoreAIContextAsync`) and Neo4j memory persistence |

**Key Patterns:**

1. **Pre-run Context Injection** — `Neo4jMemoryContextProvider : AIContextProvider` fetches relevant memory from Neo4j before agent execution begins
2. **Post-run Persistence** — `Neo4jMicrosoftMemoryFacade` orchestrates message storage and trace recording after execution
3. **Type Mapping** — `MafTypeMapper` handles bidirectional conversion between MAF's `ChatMessage` and internal `Message` types
4. **Memory Tools** — `MemoryToolFactory` creates 6 tools for agent use:
   - `search_memory` — semantic search across all memory layers
   - `remember_preference` — store user preferences
   - `remember_fact` — store facts
   - `recall_preferences` — retrieve stored preferences
   - `search_knowledge` — search entities and facts
   - `find_similar_tasks` — retrieve similar prior executions
5. **Trace Capture** — `AgentTraceRecorder` records agent reasoning steps and tool calls to Neo4j for future analysis
6. **Task-aware Automatic Recall (#88)** — `IAutomaticRecallPolicy.DecideAsync` runs inside `BuildContextAsync`, before the `RecallRequest` is built, and decides whether to recall at all, which memory categories to query, and which ranking intent to use — see §3.4.1.1
7. **Memory-context Admission Policy (#92 Phase 2)** — `IMemoryContextAdmissionPolicy.Evaluate` runs inside `MafTypeMapper.ToContextMessages` for each candidate recalled-memory block, deciding whether to admit or exclude it based on a lightweight instruction-like-content detector — see §3.4.1.2

**Namespace structure:**
```
AgentMemory.AgentFramework.Integration     — context provider, message store, facade
AgentMemory.AgentFramework.Tools            — memory tool definitions and factory
AgentMemory.AgentFramework.Mapping          — MAF type mapping
AgentMemory.AgentFramework.Tracing          — reasoning trace recording
AgentMemory.AgentFramework.Recall           — task-aware automatic recall policy (#88)
AgentMemory.AgentFramework.Security         — memory-context admission policy (#92 Phase 2)
```

#### 3.4.1.1 Task-aware automatic recall policy (#88)

`Neo4jMemoryContextProvider` previously ran the same configured `RecallOptions` for every turn with user
text, regardless of whether the turn actually needed long-term memory. `IAutomaticRecallPolicy` makes that
decision pluggable, running deterministically inside `BuildContextAsync` before anything is queried:

- `IAutomaticRecallPolicy.DecideAsync(AutomaticRecallContext, CancellationToken)` returns an
  `AutomaticRecallDecision` with `ShouldRecall`, `Categories` (an `AutomaticRecallCategories` flags enum:
  `RecentMessages`/`RelevantMessages`/`Entities`/`Facts`/`Preferences`/`ReasoningTraces`/`GraphRag`),
  an optional `Intent` override (D3's `RankingIntent`), and an optional full `RecallOptions` override.
- `ConfiguredAutomaticRecallPolicy` (the default, registered by `AddAgentMemoryFramework`) always returns
  `Categories = AutomaticRecallCategories.All` with no `Intent`/`RecallOptions` override — this reproduces
  the pre-#88 behavior exactly, deferring entirely to whatever the host already configured.
- `HeuristicAutomaticRecallPolicy` is a lightweight, deterministic, model-call-free policy: skips recall
  for an empty or greeting/acknowledgement-only turn (a linear-time tokenizer, not a regex — an earlier
  regex-based version of this check exhibited catastrophic backtracking on adversarial input), applies
  `RankingIntent.Latest` for recency-oriented phrasing, applies `RankingIntent.Analog` plus reasoning
  traces for precedent-oriented phrasing ("similar case", "how did we solve this before"), and includes
  reasoning traces for task/troubleshooting phrasing. It has no rule about GraphRAG at all, so its baseline
  always retains the `GraphRag` category on top of `AutomaticRecallCategories.Default` — it must never
  silently disable a host's independently configured `EnableGraphRag`/`MaxGraphRagItems`/`BlendMode`.
  A host opts in via `services.AddScoped<IAutomaticRecallPolicy, HeuristicAutomaticRecallPolicy>()` (after
  `AddAgentMemoryFramework`, or a custom policy the same way) — plain `AddScoped` wins over the `TryAdd`-
  registered default regardless of call order.
- Excluding a category from the decision zeroes its `RecallOptions.MaxX` limit. `MemoryContextAssembler`
  (Core) skips that category's repository call entirely instead of issuing a `LIMIT`/`TopK` 0 query — for
  every category except GraphRag that query's result is always empty anyway, so this is a pure efficiency
  win; for GraphRag specifically, `Neo4jGraphRagContextSource` treats `TopK<=0` as "use the configured
  default TopK" (a pre-existing, unrelated quirk, not "return nothing"), so skipping the call here is what
  makes `MaxGraphRagItems=0` actually mean "no GraphRAG" for the first time. This applies uniformly to both
  `AssembleContextAsync` and the bitemporal `AssembleContextAsOfAsync`. Because `RetrievalBlendMode.GraphRagOnly`
  retrieves nothing else, `AssembleContextAsync` logs a Warning if that blend mode is requested and the
  effective `MaxGraphRagItems<=0` (in addition to the pre-existing warning for GraphRAG being unavailable)
  — otherwise a host combining `GraphRagOnly` with a policy that excludes `GraphRag` would get a completely
  empty context every turn with nothing to explain why.
- Whichever branch produces the effective `RecallOptions`, `Scope` is always cleared afterwards — scope is
  always resolved from the invocation's authenticated `userId` (#100's isolation policy), never from a
  statically configured or policy-supplied value, unchanged from before #88.
- The decision is logged at Debug level (policy type, `ShouldRecall`, `Categories`, `Intent`) for
  observability. Automatic recall complements, and never replaces, the model-invokable memory tools
  (`Tools.MemoryToolFactory`).

#### 3.4.1.2 Trust boundaries: memory-context admission policy (#92 Phase 2)

Issue #92 is a large, explicitly multi-phase epic (full trust taxonomy, provenance-through-extraction,
configurable message roles, observed/inferred/verified knowledge distinctions). **Phase 1** (#108, merged)
made every recalled block delimited and escaped (`<recalled_memory category="...">...</recalled_memory>`,
angle brackets escaped so content can't forge or close its own boundary) and framed the default
`ContextFormatOptions.ContextPrefix` as an explicit untrusted-reference-data instruction. **Phase 2** (this
slice) adds an admission-policy layer on top of that delimiting, still without the full trust-metadata
model (deferred to a future phase):

- `IMemoryContextAdmissionPolicy.Evaluate(MemoryAdmissionContext)` (`AgentMemory.AgentFramework.Security`)
  runs inside `MafTypeMapper.ToContextMessages`, once per candidate recalled-memory ITEM within each of the
  five categories Phase 1 delimited (entities, facts, preferences, reasoning traces, GraphRAG), before that
  item contributes to the category's rendered block. Deliberately per-item, not per-block: entities/facts/
  preferences/traces each join several independent items into one string, and evaluating the whole joined
  string would mean one flagged item (a false positive, or a genuinely planted one) silently drops every
  other, unrelated, legitimate item alongside it. GraphRAG is the one exception -- it arrives as a single
  opaque string, not a list of items, so it is unavoidably evaluated as one block. Deliberately synchronous:
  the built-in check is pure, CPU-bound pattern matching, no I/O.
- `DefaultMemoryContextAdmissionPolicy` (the default, registered by `AddAgentMemoryFramework`) runs
  `InstructionLikeContentDetector` — a lightweight, deterministic regex over a fixed set of unambiguous
  phrasings ("ignore previous instructions", "reveal all secrets", "call this tool", etc.; a plain
  alternation of fixed phrases, deliberately NOT a repeating group with nested quantifiers, per the
  catastrophic-backtracking lesson learned building a similar detector for #88's `HeuristicAutomaticRecallPolicy`).
  This is explicitly not a complete prompt-injection detector (see issue #92's non-goals) — applications
  wanting stronger detection implement their own `IMemoryContextAdmissionPolicy`.
- `ContextFormatOptions.SecurityMode` (`MemoryContextSecurityMode`) governs the outcome when an item is
  flagged: `Permissive` (the default) still includes it — every admitted item is delimited/escaped
  regardless (#92 Phase 1) — because detection is necessarily heuristic and a false positive must never
  silently discard genuine stored information (issue #92's own deployment-runbook example: content that
  merely *resembles* an instruction while being useful must not be dropped). `Strict` excludes flagged
  items entirely instead, for hosts willing to accept that tradeoff.
- A flagged-but-included item (Permissive) is logged at Debug level; a `Strict`-mode exclusion is logged at
  Warning level. Both log only the category and (for exclusions) the reason — never the content itself.
- **Not covered by Phase 2** (still open, part of #92's larger scope): trust metadata (`MemoryTrustLevel`,
  provenance-through-extraction), configurable message role, observed/inferred/verified knowledge
  distinctions, admission/trust telemetry beyond logs, non-tag-based injection techniques beyond the fixed
  detector phrase list, and recalled conversation history (`RelevantMessages`/`RecentMessages`, including
  the separate recall path in `Neo4jChatHistoryProvider`) — which, like Phase 1, keeps its
  originally-persisted role rather than being re-injected as an unrestricted system message, and is
  therefore not run through the admission policy. The threat this issue addresses is specifically content
  gaining ELEVATED, unwarranted authority by being promoted to `ChatRole.System`; replaying a message under
  its own original role does not do that.

#### 3.4.2 GraphRAG Retrieval — built into AgentMemory.Neo4j (Phase 4 ✅ COMPLETE)

GraphRAG retrieval capability is implemented directly inside `AgentMemory.Neo4j` rather than as a separate package. This keeps the retrieval infrastructure co-located with the repositories that own the same Neo4j driver connection.

| Attribute | Value |
|---|---|
| **Purpose** | Expose `IGraphRagContextSource` with vector, fulltext, hybrid, and graph-enriched retrieval modes |
| **Location** | `AgentMemory.Neo4j` — `Retrieval/` subfolder |
| **Key types** | `Neo4jGraphRagContextSource : IGraphRagContextSource`, `GraphRagOptions`, `IRetriever`, `VectorRetriever`, `FulltextRetriever`, `HybridRetriever`, `RetrieverResult` |

**Key Patterns:**

1. **Provider delegation** — `Neo4jGraphRagContextSource` creates the appropriate `IRetriever` (vector, fulltext, hybrid, or graph-enriched) based on `GraphRagOptions.SearchMode` and delegates all retrieval to it.
2. **Resilience** — Exceptions from the underlying retriever are caught and logged; an empty `GraphRagContextResult` is returned so the agent run is never blocked by a retrieval failure.
3. **Search modes** — Supports `Vector`, `Fulltext`, `Hybrid` (vector + fulltext RRF fusion), and `Graph` (vector + multi-hop traversal).

**Namespace structure:**
```
AgentMemory.Neo4j.Retrieval           — IRetriever, RetrieverResult, public surface
AgentMemory.Neo4j.Retrieval.Internal  — VectorRetriever, FulltextRetriever, HybridRetriever
AgentMemory.Neo4j.Services            — Neo4jGraphRagContextSource
```

#### 3.4.3 AgentMemory.Observability (Phase 4 ✅ COMPLETE)

| Attribute | Value |
|---|---|
| **Purpose** | Opt-in OTel decorator that wraps `IMemoryService` and `IGraphRagContextSource` with distributed tracing spans and metrics |
| **Dependencies** | Abstractions (project ref), Core (project ref), OpenTelemetry.Api 1.12.0, Microsoft.Extensions.DI/Logging.Abstractions 10.0.5 |
| **MUST NOT reference** | Neo4j.Driver, Microsoft.Agents.*, any GraphRAG SDK |
| **Key types** | `InstrumentedMemoryService`, `InstrumentedGraphRagContextSource`, `MemoryActivitySource`, `MemoryMetrics`, `ServiceCollectionExtensions` |

**Key Patterns:**

1. **Decorator pattern** — `AddAgentMemoryObservability()` finds the already-registered `IMemoryService` and `IGraphRagContextSource` descriptors, removes them, and re-registers them wrapped in instrumented decorators. No Scrutor dependency.
2. **OTel API only** — Uses only the vendor-neutral `OpenTelemetry.Api` package. The actual exporter (OTLP, console, etc.) is wired up by the host application.
3. **Registration order** — Must be called **after** `AddAgentMemoryCore()` and, when GraphRAG is enabled, after `AgentMemory.Neo4j.Infrastructure.AddGraphRagAdapter()`. If no `IGraphRagContextSource` is registered, the decorator step is skipped.
4. **Metrics** — `MemoryMetrics` exposes counters (`messages.stored`, `entities.extracted`, `graphrag.queries`) and histograms (`recall.duration`, `persist.duration`, `graphrag.duration`).
5. **Tracing** — All spans are emitted under `ActivitySource` name `"AgentMemory"` (version `1.0.0`).

**Namespace structure:**
```
AgentMemory.Observability    — all types (decorators, metrics, activity source, DI)
```

#### 3.4.4 AgentMemory.Extraction.AzureLanguage (Phase 5 ✅ COMPLETE)

| Attribute | Value |
|---|---|
| **Purpose** | Alternative extraction backend using Azure Cognitive Services (Text Analytics) |
| **Dependencies** | Abstractions (project ref), Core (project ref), Azure.AI.TextAnalytics 5.3.0, Microsoft.Extensions.DI/Logging.Abstractions 10.0.5, Microsoft.Extensions.Options 10.0.5 |
| **MUST NOT reference** | Business logic — extraction only, no memory persistence |
| **Key types** | `AzureEntityExtractor : IEntityExtractor`, `AzureKeyPhraseExtractor : IFactExtractor`, `AzurePiiExtractor : IEntityExtractor` |

**Key Patterns:**

1. **Azure Text Analytics wrapper** — Uses Azure Cognitive Services for NER, key phrase extraction, and PII detection
2. **IEntityExtractor implementations** — Named entities (NER) and PII detection as entity extractors
3. **IFactExtractor implementation** — Key phrases extracted as facts
4. **Language-agnostic** — Supports 100+ languages via Azure's language detection
5. **Async design** — All extractors use `async/await` for non-blocking service calls

**Namespace structure:**
```
AgentMemory.Extraction.AzureLanguage    — Azure-backed extractors and DI
```

#### 3.4.5 AgentMemory.Enrichment (Phase 5 ✅ COMPLETE)

| Attribute | Value |
|---|---|
| **Purpose** | Geocoding and entity enrichment services with caching and rate limiting |
| **Dependencies** | Abstractions (project ref only — no Core ref), Microsoft.Extensions.DI/Logging.Abstractions 10.0.5, Microsoft.Extensions.Options 10.0.5, Microsoft.Extensions.Http 10.0.5, Microsoft.Extensions.Caching.Memory 10.0.5 |
| **MUST NOT reference** | Neo4j.Driver (repositories handle persistence) |
| **Key types** | `IGeocodingService`, `IEnrichmentService` (interfaces in Abstractions), `NominatimGeocodingService`, `WikimediaEntityEnrichmentService`, `CachedGeocodingService`, `RateLimitedGeocodingService` |

**Key Patterns:**

1. **Decorator chain** — Pluggable layers: Cache → RateLimiter → Backend service
   - `CachedGeocodingService` wraps the backend, checks cache first
   - `RateLimitedGeocodingService` enforces request throttling (by default Nominatim: 1 request/sec)
   - Backend: `NominatimGeocodingService` (OSM geocoding) or `WikimediaEntityEnrichmentService`
2. **Geocoding** — NominatimGeocodingService converts addresses to coordinates
3. **Entity enrichment** — WikimediaEntityEnrichmentService augments entities with Wikipedia descriptions and links
4. **Async design** — All services use `async/await` for non-blocking external API calls
5. **Configurable** — Rate limits, cache TTL, and backend selection via options

**Namespace structure:**
```
AgentMemory.Enrichment                           — services and DI
AgentMemory.Enrichment.Geocoding                 — Nominatim geocoding impl
AgentMemory.Enrichment.EntityEnrichment          — Wikimedia enrichment impl
AgentMemory.Enrichment.Decorators                — Cache/RateLimit decorators
```

#### 3.4.6 Shipped Adapter Packages

All adapter packages have shipped. The table below was the original roadmap; `AgentMemory.McpServer` is the completed MCP package.

| Package | Phase | External Dependency | Implements |
|---|---|---|---|
| `AgentMemory.McpServer` | 6 ✅ | ModelContextProtocol SDK 1.2.0, M.E.Hosting | 25 MCP tools, 6 resources, 3 prompts |

#### 3.4.7 AgentMemory.Analytics (Optional GDS Analytics ✅ SHIPPED)

| Attribute | Value |
|---|---|
| **Purpose** | Optional Neo4j Graph Data Science (GDS) analytics over the entity `RELATED_TO` graph — PageRank (memory importance) and Louvain community detection (topic clustering) |
| **Dependencies** | Abstractions (project ref), Neo4j (project ref), Microsoft.Extensions.DependencyInjection.Abstractions 10.0.5, Microsoft.Extensions.Logging.Abstractions 10.0.5, Microsoft.Extensions.Options 10.0.5 |
| **MUST NOT reference** | Microsoft.Agents.*, any framework adapter SDK |
| **Key types** | `IMemoryPageRankService` / `MemoryPageRankService`, `IMemoryCommunityService` / `MemoryCommunityService`, `IGdsAvailability` / `GdsAvailability`, `EntityRank`, `EntityCommunity`, `GdsAnalyticsOptions`, `ServiceCollectionExtensions` |
| **Core responsibility** | Surface graph-importance and topic-cluster signals when the GDS plugin is installed; degrade to a graceful no-op (empty results) when it is not |

**Key Patterns:**

1. **Opt-in registration** — `AddGdsMemoryAnalytics()` registers the three analytics services. Call `AddNeo4jAgentMemory()` first — they reuse the Neo4j transaction runner (`INeo4jTransactionRunner`).
2. **Graceful no-op** — `IGdsAvailability` probes `gds.version()` and memoizes only a *definitive* answer (present, or genuinely not installed). When GDS is absent, `RankEntitiesAsync`/`DetectCommunitiesAsync` log a warning and return an empty list rather than throwing. A transient probe failure is **not** cached, so analytics re-enable automatically once Neo4j recovers.
3. **Owner-scoped projection** — PageRank and Louvain run over the (owner-scoped) `RELATED_TO` graph via a `MemoryScope`, so one owner's ranks/communities are not perturbed by another owner's data (R1).
4. **Depends inward on Neo4j** — the only extension package (besides the driver firewall itself) that references `AgentMemory.Neo4j`, because it issues Cypher against the same driver.

**Namespace structure:**
```
AgentMemory.Analytics    — GDS services, availability probe, models, options, DI
```

---

## 4. Neo4j Graph Model

*(Derived from Plan §9 and SchemaBootstrapper implementation)*

### 4.1 Node Types

> **Note:** All Neo4j properties use `snake_case` (matching Python reference). C# domain models use PascalCase per .NET convention. The repository layer handles the translation.

| Neo4j Label | Domain Type | Key Properties (Neo4j snake_case) |
|---|---|---|
| `:Conversation` | `Conversation` | `id`, `session_id`, `user_id`, `title`, `created_at`, `updated_at`, `metadata` |
| `:Message` | `Message` | `id`, `conversation_id`, `session_id`, `role`, `content`, `timestamp`, `embedding`, `tool_call_ids`, `metadata` |
| `:Entity` | `Entity` | `id`, `name`, `canonical_name`, `type`, `subtype`, `description`, `confidence`, `embedding`, `aliases`, `attributes`, `source_message_ids`, `location`, `metadata` |
| `:Fact` | `Fact` | `id`, `subject`, `predicate`, `object`, `confidence`, `valid_from`, `valid_until`, `embedding`, `source_message_ids`, `created_at`, `metadata` |
| `:Preference` | `Preference` | `id`, `category`, `preference`, `context`, `confidence`, `embedding`, `source_message_ids`, `created_at`, `metadata` |
| `:ReasoningTrace` | `ReasoningTrace` | `id`, `session_id`, `task`, `outcome`, `success`, `started_at`, `completed_at`, `task_embedding`, `metadata` |
| `:ReasoningStep` | `ReasoningStep` | `id`, `trace_id`, `step_number`, `thought`, `action`, `observation`, `embedding`, `metadata` |
| `:ToolCall` | `ToolCall` | `id`, `step_id`, `tool_name`, `arguments`, `result`, `status`, `duration_ms`, `error`, `metadata` |
| `:Tool` | *(aggregate)* | `name`, `created_at`, `total_calls` |
| `:Extractor` | `ExtractorModel` | `id`, `name`, `version`, `config`, `created_at` — extraction provenance (upstream-parity node) |
| `:ConsolidationRun` | *(audit)* | `id`, `kind`, `ran_at`, `dry_run`, `candidate_count`, `actions_taken` — memory-hygiene audit trail written when a consolidation run is applied (PR #113) |
| `:MemoryReadAudit` | *(audit)* | `id`, `kind`, `memory_id`, `owner_id`, `read_at`, `access_count` — read/privacy audit trail recording long-term memory recall hits, written by `DecayQueries.UpdateAccessTimestamp` (upstream v0.5-compatible) |
| `:Schema` | `SchemaModel` / `EntitySchemaConfig` | `id`, `name`, `version`, `description`, `config`, `is_active`, `created_at`, `created_by` — custom-schema persistence; label + indexes declared by `SchemaBootstrapper`; CRUD via `ISchemaManager` → `Neo4jSchemaManager` (G4, see `docs/schema.md`) |

> **Note:** `SchemaConstants.NodeLabels` defines all 13 labels above. Entity-to-entity relationships use `RELATED_TO` via Neo4j native relationships (not a separate `:MemoryRelationship` node). The `Relationship` domain type maps to `RELATED_TO` relationship properties.

### 4.2 Relationship Types

```mermaid
graph LR
    Conversation -->|HAS_MESSAGE| Message
    Conversation -->|FIRST_MESSAGE| Message
    Message -->|NEXT_MESSAGE| Message
    Message -->|MENTIONS| Entity
    Entity -->|RELATED_TO| Entity
    Entity -->|SAME_AS| Entity
    Preference -->|ABOUT| Entity
    Fact -->|ABOUT| Entity
    ReasoningTrace -->|HAS_STEP| ReasoningStep
    ReasoningStep -->|USES_TOOL| ToolCall
    ToolCall -->|INSTANCE_OF| Tool
    Conversation -->|HAS_TRACE| ReasoningTrace
    ReasoningTrace -->|INITIATED_BY| Message
    ToolCall -->|TRIGGERED_BY| Message
    Entity -->|EXTRACTED_FROM| Message
    Fact -->|EXTRACTED_FROM| Message
    Preference -->|EXTRACTED_FROM| Message
    Entity -->|EXTRACTED_BY| Extractor
    Fact -->|EXTRACTED_BY| Extractor
    Preference -->|EXTRACTED_BY| Extractor
    ReasoningStep -->|TOUCHED| Entity
    Fact -->|SUPERSEDED_BY| Fact
    Preference -->|SUPERSEDED_BY| Preference
    Conversation -->|HAS_FACT| Fact
    Conversation -->|HAS_PREFERENCE| Preference
```

| Relationship Type | From | To | Purpose |
|---|---|---|---|
| `HAS_MESSAGE` | Conversation | Message | Conversation contains messages |
| `FIRST_MESSAGE` | Conversation | Message | Head of linked list |
| `NEXT_MESSAGE` | Message | Message | Message ordering within conversation |
| `MENTIONS` | Message | Entity | Entity mention in message |
| `RELATED_TO` | Entity | Entity | Inter-entity relationships |
| `ABOUT` | Preference/Fact | Entity | Links knowledge to entity |
| `SAME_AS` | Entity | Entity | Entity deduplication |
| `SUPERSEDED_BY` | Fact/Preference | Fact/Preference | Supersession (D7): loser soft-invalidated, points to winner (contradiction resolution / duplicate collapse; both kept, non-destructive) |
| `HAS_STEP` | ReasoningTrace | ReasoningStep | Trace contains steps (with `order` property) |
| `USES_TOOL` | ReasoningStep | ToolCall | Step-to-tool-call link |
| `INSTANCE_OF` | ToolCall | Tool | Links call to tool definition |
| `TOUCHED` | ReasoningStep | Entity | Audit/provenance edge — step read or acted upon an entity (carries `recorded_at`) |
| `HAS_TRACE` | Conversation | ReasoningTrace | Conversation-to-trace |
| `INITIATED_BY` | ReasoningTrace | Message | Trace triggered by message |
| `TRIGGERED_BY` | ToolCall | Message | Tool call triggered by message |
| `EXTRACTED_FROM` | Entity/Fact/Preference | Message | Extraction provenance (source message) |
| `EXTRACTED_BY` | Entity/Fact/Preference | Extractor | Extraction provenance (producing extractor) |
| `IN_SESSION` | ReasoningTrace | Conversation | .NET extension (reverse of HAS_TRACE) |
| `HAS_FACT` | Conversation | Fact | .NET extension |
| `HAS_PREFERENCE` | Conversation | Preference | .NET extension |

### 4.3 Constraints (Implemented in SchemaBootstrapper)

```cypher
CREATE CONSTRAINT conversation_id IF NOT EXISTS FOR (c:Conversation) REQUIRE c.id IS UNIQUE
CREATE CONSTRAINT message_id IF NOT EXISTS FOR (m:Message) REQUIRE m.id IS UNIQUE
CREATE CONSTRAINT entity_id IF NOT EXISTS FOR (e:Entity) REQUIRE e.id IS UNIQUE
CREATE CONSTRAINT fact_id IF NOT EXISTS FOR (f:Fact) REQUIRE f.id IS UNIQUE
CREATE CONSTRAINT preference_id IF NOT EXISTS FOR (p:Preference) REQUIRE p.id IS UNIQUE
CREATE CONSTRAINT reasoning_trace_id IF NOT EXISTS FOR (t:ReasoningTrace) REQUIRE t.id IS UNIQUE
CREATE CONSTRAINT reasoning_step_id IF NOT EXISTS FOR (s:ReasoningStep) REQUIRE s.id IS UNIQUE
CREATE CONSTRAINT tool_call_id IF NOT EXISTS FOR (tc:ToolCall) REQUIRE tc.id IS UNIQUE
CREATE CONSTRAINT tool_name IF NOT EXISTS FOR (t:Tool) REQUIRE t.name IS UNIQUE
CREATE CONSTRAINT extractor_name IF NOT EXISTS FOR (ex:Extractor) REQUIRE ex.name IS UNIQUE
CREATE CONSTRAINT consolidation_run_id IF NOT EXISTS FOR (r:ConsolidationRun) REQUIRE r.id IS UNIQUE
CREATE CONSTRAINT memory_read_audit_id IF NOT EXISTS FOR (a:MemoryReadAudit) REQUIRE a.id IS UNIQUE
```

### 4.4 Fulltext Indexes (Implemented in SchemaBootstrapper)

```cypher
CREATE FULLTEXT INDEX message_content IF NOT EXISTS FOR (m:Message) ON EACH [m.content]
CREATE FULLTEXT INDEX entity_name IF NOT EXISTS FOR (e:Entity) ON EACH [e.name, e.description]
CREATE FULLTEXT INDEX fact_content IF NOT EXISTS FOR (f:Fact) ON EACH [f.subject, f.predicate, f.object]
```

### 4.5 Vector Indexes (Implemented in SchemaBootstrapper)

Vector indexes for semantic search, using cosine similarity with configurable dimensions (default 1536). *(Plan §9.3)*

```cypher
CREATE VECTOR INDEX message_embedding_idx IF NOT EXISTS FOR (n:Message) ON (n.embedding)
  OPTIONS {indexConfig: {`vector.dimensions`: 1536, `vector.similarity_function`: 'cosine'}}
CREATE VECTOR INDEX entity_embedding_idx IF NOT EXISTS FOR (n:Entity) ON (n.embedding)
  OPTIONS {indexConfig: {`vector.dimensions`: 1536, `vector.similarity_function`: 'cosine'}}
CREATE VECTOR INDEX preference_embedding_idx IF NOT EXISTS FOR (n:Preference) ON (n.embedding)
  OPTIONS {indexConfig: {`vector.dimensions`: 1536, `vector.similarity_function`: 'cosine'}}
CREATE VECTOR INDEX fact_embedding_idx IF NOT EXISTS FOR (n:Fact) ON (n.embedding)
  OPTIONS {indexConfig: {`vector.dimensions`: 1536, `vector.similarity_function`: 'cosine'}}
CREATE VECTOR INDEX reasoning_step_embedding_idx IF NOT EXISTS FOR (n:ReasoningStep) ON (n.embedding)
  OPTIONS {indexConfig: {`vector.dimensions`: 1536, `vector.similarity_function`: 'cosine'}}
```

> **Note:** A `task_embedding_idx` for `ReasoningTrace.task_embedding` is used by `SearchByTaskVectorAsync` and is created in `SchemaBootstrapper` as part of the standard vector index set.

### 4.6 Property Indexes (Implemented in SchemaBootstrapper)

**21 range indexes** (`SchemaQueries.PropertyIndexes`, in bootstrap order — note `rel_owner_idx` is a **relationship-property** index on the `RELATED_TO` edge):

```cypher
CREATE INDEX conversation_session_idx IF NOT EXISTS FOR (c:Conversation) ON (c.session_id)
CREATE INDEX message_timestamp_idx IF NOT EXISTS FOR (m:Message) ON (m.timestamp)
CREATE INDEX message_role_idx IF NOT EXISTS FOR (m:Message) ON (m.role)
CREATE INDEX entity_type_idx IF NOT EXISTS FOR (e:Entity) ON (e.type)
CREATE INDEX entity_name_idx IF NOT EXISTS FOR (e:Entity) ON (e.name)
CREATE INDEX entity_canonical_idx IF NOT EXISTS FOR (e:Entity) ON (e.canonical_name)
CREATE INDEX fact_category IF NOT EXISTS FOR (f:Fact) ON (f.category)
CREATE INDEX preference_category_idx IF NOT EXISTS FOR (p:Preference) ON (p.category)
CREATE INDEX trace_session_idx IF NOT EXISTS FOR (t:ReasoningTrace) ON (t.session_id)
CREATE INDEX trace_success_idx IF NOT EXISTS FOR (t:ReasoningTrace) ON (t.success)
CREATE INDEX reasoning_step_timestamp IF NOT EXISTS FOR (s:ReasoningStep) ON (s.timestamp)
CREATE INDEX tool_call_status_idx IF NOT EXISTS FOR (tc:ToolCall) ON (tc.status)
CREATE INDEX schema_name_idx IF NOT EXISTS FOR (s:Schema) ON (s.name)
CREATE INDEX schema_version_idx IF NOT EXISTS FOR (s:Schema) ON (s.version)
CREATE INDEX fact_owner_idx IF NOT EXISTS FOR (f:Fact) ON (f.owner_id)
CREATE INDEX entity_owner_idx IF NOT EXISTS FOR (e:Entity) ON (e.owner_id)
CREATE INDEX preference_owner_idx IF NOT EXISTS FOR (p:Preference) ON (p.owner_id)
CREATE INDEX trace_owner_idx IF NOT EXISTS FOR (t:ReasoningTrace) ON (t.owner_id)
CREATE INDEX rel_owner_idx IF NOT EXISTS FOR ()-[r:RELATED_TO]-() ON (r.owner_id)
CREATE INDEX conversation_archived_idx IF NOT EXISTS FOR (c:Conversation) ON (c.archived)
CREATE INDEX memory_read_audit_kind_idx IF NOT EXISTS FOR (a:MemoryReadAudit) ON (a.kind)
```

**1 point index** (also in `SchemaQueries.PropertyIndexes`, for geospatial entity queries):

```cypher
CREATE POINT INDEX entity_location_idx IF NOT EXISTS FOR (e:Entity) ON (e.location)
```

> **Note:** The five owner-scope indexes — four node indexes (`fact_owner_idx`, `entity_owner_idx`, `preference_owner_idx`, `trace_owner_idx`) plus the `rel_owner_idx` relationship-property index — accelerate the `owner_id` filter applied during scoped vector recall (R1, multi-user isolation).

---

## 5. Boundary Enforcement Rules

These rules are inviolable. Violation of any rule is a blocking review finding.

| Rule | Constraint | Rationale |
|---|---|---|
| **B1** | Abstractions MUST NOT reference any NuGet package **except** `Microsoft.Extensions.AI.Abstractions` (approved — D-AR2-1) | `M.E.AI.Abstractions` provides the `IEmbeddingGenerator<string, Embedding<float>>` contract consumed by `IEmbeddingOrchestrator`. It is treated as a near-BCL contract layer with zero runtime coupling. All other Abstractions types remain free of NuGet dependencies. |
| **B2** | Core MUST NOT reference Neo4j.Driver | Orchestration layer is persistence-agnostic |
| **B3** | Core MUST NOT reference Microsoft.Agents.* | Core is framework-agnostic; MAF lives in adapter |
| **B4** | Core MUST NOT reference any framework adapter SDK (Microsoft.Agents.*, SemanticKernel.*, MCP SDK) | Core has zero knowledge of adapters; GraphRAG retrieval lives in the Neo4j package, not in a separate adapter package |
| **B5** | Neo4j MUST NOT reference Microsoft.Agents.* | Persistence layer has no framework knowledge |
| **B6** | Neo4j MUST NOT reference any framework adapter SDK (Microsoft.Agents.*, SemanticKernel.*, MCP SDK) | Persistence and retrieval layer has no framework knowledge; it is consumed by adapter packages, never the reverse |
| **B7** | No adapter may contain business logic that belongs in Core | Adapters are thin translation layers only |
| **B8** | Adapters depend on Core/Abstractions — never the reverse | Dependency inversion; core doesn't know about adapters |

**Enforcement:** Code review gates on all PRs, plus automated CI guards — **B1** via `AbstractionsContractGuardTests` and **B2–B6/B8** via `PackageBoundaryGuardTests` (both compiled-reference and `.csproj` scans). These run as unit tests in the CI workflow on every PR. (**B7** — "no business logic in adapters" — remains a review-only rule.)

**Current Verification (as of Gap Closure Sprint + MEAI adoption D-AR2-1):**
- ✅ Abstractions .csproj: one `<PackageReference>` — `Microsoft.Extensions.AI.Abstractions` 10.5.1 (approved, B1)
- ✅ Core .csproj: FuzzySharp + M.E.AI.Abstractions + M.E.DI/Logging/Options (no Neo4j.Driver, no framework SDKs)
- ✅ Neo4j .csproj: Neo4j.Driver 6.0.0 + M.E.DI/Logging/Options (no Microsoft.Agents.*, no MCP SDK)
- ✅ `grep` for `Microsoft.Agents` across `src/AgentMemory.Neo4j/` returns zero matches
- ✅ GraphRAG retrieval (`Neo4jGraphRagContextSource`, `IRetriever`, `VectorRetriever`, `FulltextRetriever`, `HybridRetriever`) lives inside `AgentMemory.Neo4j` — no separate `GraphRagAdapter` package exists

---

## 6. Relationship to neo4j-maf-provider

The existing `Neo4j/neo4j-maf-provider/dotnet` project is a Neo4j GraphRAG context provider for Microsoft Agent Framework. It is **reference material**, not a dependency for our core packages.

### 6.1 What It Provides

The existing package (`Neo4j.AgentFramework.GraphRAG`) contains:
- `Neo4jContextProvider` — a MAF `AIContextProvider` that retrieves knowledge graph context from Neo4j
- `IRetriever` / `VectorRetriever` / `FulltextRetriever` / `HybridRetriever` — a clean retriever abstraction with production-quality Cypher queries
- `RetrieverResult` / `RetrieverResultItem` — result types for retriever output
- `StopWords` — utility for fulltext query stop-word filtering
- `Neo4jContextProviderOptions` — configuration with index type, embedding generator, retrieval query

### 6.2 What We Reuse (Patterns Only)

We adapt the following **Cypher query patterns** from the retriever layer:

| Pattern | Source | Our Use |
|---|---|---|
| `db.index.vector.queryNodes($index, $k, $embedding)` | `VectorRetriever.cs` | Vector search in Entity, Message, Fact, Preference, ReasoningTrace repositories |
| `db.index.fulltext.queryNodes($index_name, $query)` | `FulltextRetriever.cs` | Fulltext search in Message, Entity, Fact repositories |
| `RoutingControl.Readers` read routing | All retrievers | All read queries routed to Neo4j cluster readers |
| Concurrent search + max-score merge | `HybridRetriever.cs` | Future hybrid search in context assembly |
| Parameterized Cypher queries | All retrievers | All repository queries use parameters, never string interpolation |
| Optional `retrieval_query` enrichment | `VectorRetriever.cs` | Future graph traversal enrichment in repositories |

### 6.3 What We Don't Reuse

| Component | Reason |
|---|---|
| `Neo4jContextProvider : AIContextProvider` | MAF-specific base class; we are framework-agnostic in Core |
| `RetrieverResult` / `RetrieverResultItem` | We have our own typed domain models (Entity, Fact, etc.) with scored tuple returns |
| `IEmbeddingGenerator<string, Embedding<float>>` | Used by the reference project; we use it via `IEmbeddingOrchestrator` in our own packages (MEAI-native, D-AR2-1) |
| `Neo4jContextProviderOptions.EmbeddingGenerator` | Tied to M.E.AI type system — handled natively in our packages |
| `InvokingContext` / MAF lifecycle hooks | MAF-specific; bridged by the AgentFramework adapter (Phase 3 complete) |

### 6.4 How GraphRAG Retrieval Is Bridged (Phase 4 ✅ Complete)

Rather than a separate adapter package, GraphRAG retrieval was internalized into `AgentMemory.Neo4j`:

```
┌──────────────────────┐     ┌──────────────────────────────────┐
│ Core Memory Engine   │     │ AgentMemory.Neo4j           │
│                      │     │   (same package as Neo4j repos)   │
│ IGraphRagContextSource ◄────── Neo4jGraphRagContextSource     │
│   (in Abstractions)  │     │     │                             │
│                      │     │     │ delegates to                │
│                      │     │     ▼                             │
│                      │     │   IRetriever (VectorRetriever,    │
│                      │     │    FulltextRetriever,             │
│                      │     │    HybridRetriever)               │
└──────────────────────┘     └──────────────────────────────────┘
```

This approach:
1. Owns the `IRetriever` interface and retriever implementations directly in the Neo4j package
2. Implements `IGraphRagContextSource` (defined in Abstractions)
3. Uses `IEmbeddingGenerator<string, Embedding<float>>` natively (no external neo4j-maf-provider dependency)
4. Adapts the Cypher query patterns (`db.index.vector.queryNodes`, `db.index.fulltext.queryNodes`) to our schema

### 6.5 Why Internalized Rather Than Separate Package

1. **No upstream dependency needed**: Neo4j.AgentFramework.GraphRAG is MAF-version-coupled (was built for MAF 0.3). Owning the retriever implementations removes that dependency.
2. **Single driver connection**: GraphRAG retrievers and Neo4j repositories share the same `IDriver` instance via DI — no separate connection overhead.
3. **Cohesive Cypher ownership**: Retrieval Cypher patterns naturally belong with the repository Cypher patterns in the same package.

### 6.6 MAF Version Context

The upstream `neo4j-maf-provider` was built for **MAF 0.3** (pre-GA). Our Phase 3 MAF adapter targets the current **MAF 1.9.0** API surface. The reference project remains useful as architectural inspiration but is not referenced as a package dependency.

---

## 7. Test Strategy

*(Spec §2.4, Plan §16)*

| Test Layer | Project | Scope | Key Dependencies |
|---|---|---|---|
| **Unit** | `AgentMemory.Tests.Unit` | Core services, stubs, domain logic, validation | xUnit 2.9.2, FluentAssertions 8.9.0, NSubstitute 5.3.0, coverlet 6.0.2 |
| **Integration** | `AgentMemory.Tests.Integration` | Repository implementations, schema bootstrap, transaction behavior | Testcontainers.Neo4j 4.11.0, Neo4j.Driver 6.0.0, real Neo4j container |
| **E2E** | `Tests.E2E` (Phase 3+) | Full pipeline with MAF adapter | MAF test host + Testcontainers |

### Testing Rules

1. Every repository implementation gets **integration tests** before moving to the next repository
2. Every service implementation gets **unit tests** before the service is considered done
3. Integration tests use a **shared Neo4j fixture** (one Testcontainer per test run)
4. Unit tests use **NSubstitute mocks** via `MockFactory` — no real infrastructure
5. Test data seeders provide factory methods for all domain types

### Current Test Inventory

- **Unit tests:** Covering all src packages — domain models, services, repositories, extraction pipeline, entity resolution, MCP tools/resources/prompts, MAF adapter, GraphRAG, observability, enrichment, geocoding, configuration, datetime migration, session strategies, metadata filters
- **Integration tests:** Neo4j connectivity, repository CRUD, schema bootstrap, transaction behavior via Testcontainers
- **Test infrastructure:** Neo4jTestFixture, IntegrationTestBase, TestDataSeeders, MockFactory, Neo4jTestCollection

---

## 8. Phase Roadmap

| Phase | Name | Objective | Status |
|---|---|---|---|
| **0** | Discovery & Design Lock | Freeze architecture, interfaces, graph schema | ✅ Complete |
| **1** | Core Memory Engine | Framework-agnostic memory core + Neo4j persistence | ✅ **Complete** |
| **2** | LLM Extraction Pipeline | .NET-native structured extraction using LLMs | ✅ **Complete** |
| **3** | MAF Adapter | Microsoft Agent Framework integration | ✅ **Complete** |
| **4** | GraphRAG + Observability | GraphRAG retrieval inside `AgentMemory.Neo4j`, blended context, OpenTelemetry | ✅ **Complete** |
| **5** | Advanced Extraction | Azure Language, geocoding, enrichment | ✅ **Complete** |
| **6** | MCP Server | External access via Model Context Protocol | ✅ **Complete** |
| **7** | Gap Closure (Waves A–C) | Python parity sprint — datetime, sessions, filters, MCP resources | ✅ **Complete** |

### All Phases Complete

All 6 implementation phases plus the gap closure and hardening work are complete. The project ships 11 adapter/library packages plus the `AgentMemory` meta-package, with extensive unit and integration test coverage and ~99% functional parity with the Python reference.

### Phase 1 Exit Criteria

- ✅ All repositories implemented with Neo4j persistence
- ✅ All services unit tested
- ✅ All repositories integration tested with real Neo4j via Testcontainers
- ✅ Context assembler functional with configurable budgets
- ✅ No MAF or GraphRAG dependencies in Core or Abstractions
- ✅ Schema bootstrap creates all constraints and indexes (12 constraints, 21 property, 6 vector, 1 point, 3 fulltext)
- ✅ In-process memory engine works without Agent Framework

---

## 9. Package Strategy Analysis

**Added:** 2026-04-17
**Author:** Jose Luis Latorre Millas

### 9.1 Package Dependency Isolation Audit

Each package exists to prevent a specific unwanted transitive dependency from reaching consumers who don't need it. The following table shows what each package adds to the dependency graph and why that isolation matters.

| # | Package | Key External Deps | Depends On (Project Refs) | Isolation Justification |
|---|---|---|---|---|
| 1 | **Abstractions** | M.E.AI.Abstractions (for `IEmbeddingGenerator`) | — | **Foundation stone.** Contract package. Every other package references this. Minimal dependencies — only what is required for core domain contracts. |
| 2 | **Core** | FuzzySharp, M.E.AI.Abstractions, M.E.DI/Logging/Options | Abstractions | **Orchestration without infrastructure.** Services, entity resolution, extraction pipeline coordination. No driver, no framework. Consumers who only need in-memory stubs never touch Neo4j.Driver. |
| 3 | **Neo4j** | Neo4j.Driver 6.0.0 | Abstractions, Core | **Driver firewall.** The *only* package that references Neo4j.Driver. Also contains GraphRAG retrieval (`Neo4jGraphRagContextSource`, retrievers). |
| 4 | **Enrichment** | M.E.Http, M.E.Caching.Memory | Abstractions | **HTTP isolation.** Wikimedia/Nominatim enrichment requires HttpClient infrastructure and caching. Consumers who don't need external entity enrichment don't inherit these. |
| 5 | **Extraction.AzureLanguage** | Azure.AI.TextAnalytics 5.3.0 | Abstractions | **Azure SDK firewall.** Azure.AI.TextAnalytics pulls Azure.Core, Azure.Identity, and their transitive graph. Users of LLM extraction should never see these. |
| 6 | **Extraction.Llm** | M.E.AI.Abstractions | Abstractions, Core | **LLM extraction alternative.** Uses IChatClient for structured extraction. Separated from AzureLanguage so users choose one backend without pulling the other. |
| 7 | **AgentFramework** | Microsoft.Agents.AI.Abstractions 1.9.0 | Abstractions, Core | **MAF firewall.** Non-MAF users (MCP hosts, standalone apps) should never see Microsoft.Agents.* in their dependency tree. |
| 8 | **SemanticKernel** | Microsoft.SemanticKernel 1.74.0 | Abstractions, Core | **SK firewall.** SK-specific integration layer — only SK users pay this cost (the full SK package, not just contracts). |
| 9 | **McpServer** | ModelContextProtocol 1.2.0, M.E.Hosting | Abstractions | **MCP SDK firewall.** Only relevant for MCP server deployments. Library consumers never inherit MCP protocol overhead. |
| 10 | **Observability** | OpenTelemetry.Api 1.12.0 | Abstractions, Core | **OTel opt-in.** Observability is additive, not mandatory. Consumers who don't export traces shouldn't reference OTel. |
| 11 | **Analytics** | *(no new NuGet dep — GDS is a server-side Neo4j plugin)* | Abstractions, Neo4j | **Optional GDS analytics.** PageRank + Louvain community detection over the entity `RELATED_TO` graph. Opt-in; degrades to a graceful no-op when the GDS plugin is absent. The only extension package that references Neo4j (issues Cypher on the same driver). |

### 9.2 Dependency Graph (Simplified)

```
                        ┌─────────────────────┐
                        │    Abstractions      │  ← M.E.AI.Abstractions only
                        └──────────┬──────────┘
                                   │
                    ┌──────────────┼──────────────┐
                    │              │              │
              ┌─────▼─────┐  ┌────▼────┐   ┌────▼──────────────┐
              │   Core     │  │Enrichmt │   │ Extraction.Azure  │
              │ (FuzzySharp│  │ (HTTP,  │   │ (Azure.AI.Text)   │
              │  M.E.AI)   │  │ Cache)  │   └───────────────────┘
              └─────┬──────┘  └─────────┘
                    │
        ┌───────────┼───────────┬───────────────┐
        │           │           │               │
  ┌─────▼─────┐ ┌──▼────────┐ ┌▼────────────┐ ┌▼──────────────┐
  │   Neo4j   │ │ Extract.  │ │AgentFramework│ │ Observability │
  │(Neo4j.Drv)│ │   Llm     │ │(MS.Agents)  │ │(OTel.Api)     │
  │ +GraphRAG │ └───────────┘ └─────────────┘ └───────────────┘
  └───────────┘

  ┌──────────────┐   ┌──────────────┐   ┌──────────────────┐
  │SemanticKernel│   │  McpServer   │   │    Analytics     │
  │(SK.Abstract.)│   │(MCP SDK +   │   │ (→ Neo4j; opt-in │
  └──────────────┘   │ Hosting)     │   │  GDS PageRank /  │
                     └──────────────┘   │  Louvain)        │
                                        └──────────────────┘
```

### 9.3 Can We Simplify? Merger Candidates Analysis

| Merge Candidate | External Deps Gained | Verdict | Rationale |
|---|---|---|---|
| **Core + Neo4j** → single package | Neo4j.Driver 6.0.0 | ❌ **Do not merge** | Core is usable without Neo4j (in-memory stubs, testing). Merging forces every consumer to pull the driver (~4 MB + native deps) even when they only need service interfaces. This is the most valuable split in the system. |
| **Core + Observability** → single package | OpenTelemetry.Api | ⚠️ **Possible but not recommended** | OTel.Api is light (~200 KB), but making it mandatory violates the opt-in principle. Libraries shouldn't force telemetry on consumers. Keep separate. |
| **Extraction.Llm + Core** → single package | *None new* (same M.E.AI dep) | ⚠️ **Plausible** | Extraction.Llm depends on Core and shares the M.E.AI.Abstractions dependency. *However*, keeping it separate lets users deploy Core without any LLM extraction cost, which is valid for read-only or manually-curated memory use cases. **Defer until user feedback says otherwise.** |
| **Enrichment + Core** → single package | M.E.Http, M.E.Caching | ❌ **Do not merge** | Enrichment adds HttpClient factory and caching infrastructure — real runtime overhead that most consumers won't need. |
| **AgentFramework + SemanticKernel** → single package | Both MS.Agents + SK | ❌ **Do not merge** | Different frameworks, different consumers. A MAF user may not want SK. Each pulls a distinct SDK. |
| **Extraction.AzureLanguage + Extraction.Llm** → single package | Azure.AI.TextAnalytics | ❌ **Do not merge** | Azure SDK is ~12 transitive packages. LLM extraction is lightweight. Merging forces Azure SDK on LLM-only users. The whole point of extraction backends is pick-one-or-both. |
| **McpServer + anything** | MCP SDK + Hosting | ❌ **Do not merge** | MCP is an executable deployment unit, not a library. It has fundamentally different packaging concerns (hosting, stdio/SSE transport). |

### 9.4 Recommendation: Keep Current Package Topology

**The current package topology is justified.** Each package isolates a genuine external dependency that would otherwise pollute consumers who don't need it. The four strongest splits are:

1. **Abstractions ↔ everything** — minimal-dep contracts (industry standard pattern: cf. M.E.Logging.Abstractions)
2. **Core ↔ Neo4j** — driver isolation (the most impactful split)
3. **Extraction.AzureLanguage ↔ Extraction.Llm** — pick-your-backend without inheriting the other's SDK
4. **McpServer ↔ library packages** — executable vs. library concern separation

The only debatable merge is **Extraction.Llm → Core**, and even that should be deferred. The naming convention is clear and the solution file organizes them well.

### 9.5 Consumer Use-Case Matrix

| Use Case | Packages Required | Package Count |
|---|---|---|
| **Library consumer (read/write memory)** | Abstractions + Core + Neo4j | 3 |
| **+ LLM extraction** | + Extraction.Llm | 4 |
| **+ Azure extraction** | + Extraction.AzureLanguage | 4–5 |
| **+ Entity enrichment** | + Enrichment | 4–6 |
| **MAF agent integration** | Abstractions + Core + Neo4j + AgentFramework | 4 |
| **Semantic Kernel integration** | Abstractions + Core + Neo4j + SemanticKernel | 4 |
| **GraphRAG retrieval** | Abstractions + Core + Neo4j (GraphRAG built-in) | 3 |
| **MCP server deployment** | Abstractions + Core + Neo4j + McpServer | 4 |
| **+ Observability** | + Observability (additive to any above) | +1 |
| **+ GDS analytics (PageRank / communities)** | + Analytics (additive; requires Neo4j + GDS plugin) | +1 |

---

## 10. DateTime Storage — Native `datetime()` (Completed)

**Added:** 2026-04-17 (analysis) | **Completed:** Gap Closure Sprint Wave B (G1)
**Author:** Jose Luis Latorre Millas

### 10.1 Completed State

All timestamps are stored as **native Neo4j `datetime()`** values via the `Neo4jDateTimeHelper` utility class. All 7 Neo4j repositories use this approach. A backward-compatible reader gracefully handles both ISO-8601 strings and native datetime values during any transition period.

**Domain model types:** All timestamp properties use `DateTimeOffset` (correct .NET practice). The conversion at the serialization boundary uses `ZonedDateTime` from Neo4j.Driver 6.0.0.

### 10.2 Benefits Realized

| Benefit | Status |
|---|---|
| **Correct temporal ordering** | ✅ Neo4j native `datetime()` supports `>`, `<`, `duration.between()` natively |
| **Temporal query support** | ✅ Enables Cypher temporal functions: `duration.between()`, `date.truncate()`, etc. |
| **Schema consistency** | ✅ All repositories use the same approach — no more mixed ISO string / native datetime |
| **Neo4j Browser UX** | ✅ Native datetime renders properly in Neo4j tools |
