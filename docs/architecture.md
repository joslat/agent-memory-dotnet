# Architecture Overview — Agent Memory for .NET

**Last Updated:** 2026-04-14 (Phase 6 — Complete)  
**Author:** Deckard (Lead Architect)  
**Canonical Specification:** [Agent-Memory-for-DotNet-Specification.md](../Agent-Memory-for-DotNet-Specification.md)
**Implementation Plan:** [Agent-memory-for-dotnet-implementation-plan.md](../Agent-memory-for-dotnet-implementation-plan.md)

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

### What It Does NOT Do

- **No Python runtime** — purely .NET, no Python bridge or subprocess *(Spec §1.4)*
- **MCP Server** — Phase 6 complete, exposes 21 memory tools, 6 resources, and 3 prompts via Model Context Protocol *(Plan §Phase 6)*
- **No bundled LLM** — extraction and embedding providers are pluggable interfaces, stubbed in Phase 1 *(Decision D5)*
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
│  │ AgentMemory.MAF     │  │ AgentMemory.          │  │ AgentMem. │  │
│  │ (MAF adapter)       │  │ GraphRagAdapter       │  │ Mcp       │  │
│  │                     │  │                       │  │           │  │
│  │ + Microsoft.Agents  │  │ + Neo4j.AgentFW.      │  │ + MCP SDK │  │
│  │   .AI.*             │  │   GraphRAG            │  │           │  │
│  └────────┬────────────┘  └─────────┬─────────────┘  └─────┬─────┘  │
│           │                         │                       │        │
│           └─────────────┬───────────┘───────────────────────┘        │
│                         │  depends inward                            │
│                         ▼                                            │
├─────────────────────────────────────────────────────────────────────┤
│                 EXTENSIONS & CROSS-CUTTING (Phase 4–5)               │
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
│  │  Neo4j.AgentMemory.Neo4j                                    │   │
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
│  │  Neo4j.AgentMemory.Core                                     │   │
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
│  │  Neo4j.AgentMemory.Abstractions                             │   │
│  │  (domain models, service interfaces, repository interfaces, │   │
│  │   configuration options — IGeocodingService,                │   │
│  │   IEnrichmentService added Phase 5)                         │   │
│  │                                                              │   │
│  │  ZERO external dependencies — .NET 9 BCL only               │   │
│  └──────────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────────┘
```

### 2.2 Dependency Direction Rule

**Dependencies flow strictly inward.** Adapters → Neo4j → Core → Abstractions. Never the reverse.

```mermaid
graph TD
    MAF["MAF Adapter<br/>(Phase 3)"] --> Core
    GRA["GraphRAG Adapter<br/>(Phase 4)"] --> Core
    OBS["Observability<br/>(Phase 4)"] --> Core
    MCP["MCP Server<br/>(Phase 6)"] --> Core
    Neo4j["Neo4j.AgentMemory.Neo4j"] --> Core
    Neo4j --> Abs
    Core["Neo4j.AgentMemory.Core"] --> Abs
    Abs["Neo4j.AgentMemory.Abstractions<br/>(zero deps)"]
    OBS -. decorates .-> MAF
    OBS -. decorates .-> GRA
```

---

## 3. Package Responsibilities

### 3.1 Neo4j.AgentMemory.Abstractions

| Attribute | Value |
|---|---|
| **Purpose** | Domain contracts — all models, interfaces, and configuration types shared across the system |
| **Dependencies** | **None** — .NET 9 BCL only |
| **MUST NOT reference** | Neo4j.Driver, Microsoft.Agents.*, any GraphRAG SDK, any MCP SDK, any NuGet package |
| **Key types** | 31 domain records (Conversation, Message, Entity, Fact, Preference, Relationship, ReasoningTrace, ReasoningStep, ToolCall, etc.), 15 service interfaces, 10 repository interfaces, 9 configuration types, 6 enums |

**Namespace structure:**
```
Neo4j.AgentMemory.Abstractions.Domain        — records and enums
Neo4j.AgentMemory.Abstractions.Services      — service interfaces
Neo4j.AgentMemory.Abstractions.Repositories  — repository interfaces
Neo4j.AgentMemory.Abstractions.Options       — configuration records
```

### 3.2 Neo4j.AgentMemory.Core

| Attribute | Value |
|---|---|
| **Purpose** | Orchestration — service implementations, extraction pipeline, context assembly, stubs |
| **Dependencies** | Abstractions (project ref), Microsoft.Extensions.DependencyInjection.Abstractions 10.0.5, Microsoft.Extensions.Logging.Abstractions 10.0.5, Microsoft.Extensions.Options 10.0.5 |
| **MUST NOT reference** | Neo4j.Driver, Microsoft.Agents.*, any GraphRAG SDK |
| **Key types** | SystemClock, GuidIdGenerator, StubEmbeddingProvider, StubExtractionPipeline, StubEntityExtractor, StubFactExtractor, StubPreferenceExtractor, StubRelationshipExtractor, StubEntityResolver |

### 3.3 Neo4j.AgentMemory.Neo4j

| Attribute | Value |
|---|---|
| **Purpose** | Persistence — Neo4j repository implementations, Cypher queries, schema management, driver infrastructure |
| **Dependencies** | Abstractions (project ref), Core (project ref), Neo4j.Driver 6.0.0, Microsoft.Extensions.DependencyInjection.Abstractions 10.0.5, Microsoft.Extensions.Logging.Abstractions 10.0.5, Microsoft.Extensions.Options 10.0.5 |
| **MUST NOT reference** | Microsoft.Agents.*, Neo4j.AgentFramework.GraphRAG, any GraphRAG SDK |
| **Key types** | Neo4jDriverFactory, Neo4jSessionFactory, Neo4jTransactionRunner, SchemaBootstrapper, MigrationRunner, Neo4jOptions, ServiceCollectionExtensions |

### 3.4 Adapter Packages

#### 3.4.1 Neo4j.AgentMemory.AgentFramework (Phase 3 ✅ COMPLETE)

| Attribute | Value |
|---|---|
| **Purpose** | Thin adapter layer exposing memory capabilities to Microsoft Agent Framework |
| **Dependencies** | Abstractions (project ref), Core (project ref), Neo4j (project ref), Microsoft.Agents.AI.Abstractions 1.1.0, Microsoft.Extensions.DependencyInjection.Abstractions 10.0.5, Microsoft.Extensions.Logging.Abstractions 10.0.5, Microsoft.Extensions.Options 10.0.5 |
| **MUST NOT reference** | Business logic — act only as a type mapper and adapter |
| **Key types** | `Neo4jMemoryContextProvider` (extends `AIContextProvider`), `Neo4jChatMessageStore`, `Neo4jMicrosoftMemoryFacade`, `MafTypeMapper` (bidirectional `ChatMessage` ↔ `Message` mapping), `MemoryToolFactory` (6 tools), `AgentTraceRecorder` |
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

**Namespace structure:**
```
Neo4j.AgentMemory.AgentFramework.Integration     — context provider, message store, facade
Neo4j.AgentMemory.AgentFramework.Tools            — memory tool definitions and factory
Neo4j.AgentMemory.AgentFramework.Mapping          — MAF type mapping
Neo4j.AgentMemory.AgentFramework.Tracing          — reasoning trace recording
```

#### 3.4.2 Neo4j.AgentMemory.GraphRagAdapter (Phase 4 ✅ COMPLETE)

| Attribute | Value |
|---|---|
| **Purpose** | Thin adapter exposing the existing Neo4j GraphRAG retrieval pipeline as an `IGraphRagContextSource` |
| **Dependencies** | Abstractions (project ref), Neo4j.AgentFramework.GraphRAG (project ref), Microsoft.Extensions.AI.Abstractions 10.4.1, Microsoft.Extensions.DI/Logging/Options 10.0.5 |
| **MUST NOT reference** | Business logic — wraps a reference provider only |
| **Key types** | `Neo4jGraphRagContextSource : IGraphRagContextSource`, `GraphRagAdapterOptions` |

**Key Patterns:**

1. **Provider delegation** — `Neo4jGraphRagContextSource` creates the appropriate `IRetriever` (vector, fulltext, hybrid, or graph-enriched) based on `GraphRagAdapterOptions.SearchMode` and delegates all retrieval to it.
2. **Resilience** — Exceptions from the underlying retriever are caught and logged; an empty `GraphRagContextResult` is returned so the agent run is never blocked by a retrieval failure.
3. **Search modes** — Supports `Vector`, `Fulltext`, `Hybrid` (vector + fulltext RRF fusion), and `Graph` (vector + multi-hop traversal).

**Namespace structure:**
```
Neo4j.AgentMemory.GraphRagAdapter             — public surface (source, options, DI)
Neo4j.AgentMemory.GraphRagAdapter.Internal    — adapter retrievers (vector, fulltext, hybrid)
```

#### 3.4.3 Neo4j.AgentMemory.Observability (Phase 4 ✅ COMPLETE)

| Attribute | Value |
|---|---|
| **Purpose** | Opt-in OTel decorator that wraps `IMemoryService` and `IGraphRagContextSource` with distributed tracing spans and metrics |
| **Dependencies** | Abstractions (project ref), Core (project ref), OpenTelemetry.Api 1.12.0, Microsoft.Extensions.DI/Logging.Abstractions 10.0.5 |
| **MUST NOT reference** | Neo4j.Driver, Microsoft.Agents.*, any GraphRAG SDK |
| **Key types** | `InstrumentedMemoryService`, `InstrumentedGraphRagContextSource`, `MemoryActivitySource`, `MemoryMetrics`, `ServiceCollectionExtensions` |

**Key Patterns:**

1. **Decorator pattern** — `AddAgentMemoryObservability()` finds the already-registered `IMemoryService` and `IGraphRagContextSource` descriptors, removes them, and re-registers them wrapped in instrumented decorators. No Scrutor dependency.
2. **OTel API only** — Uses only the vendor-neutral `OpenTelemetry.Api` package. The actual exporter (OTLP, console, etc.) is wired up by the host application.
3. **Registration order** — Must be called **after** `AddAgentMemoryCore()` and `AddGraphRagAdapter()`. If no `IGraphRagContextSource` is registered, the decorator step is silently skipped.
4. **Metrics** — `MemoryMetrics` exposes counters (`messages.stored`, `entities.extracted`, `graphrag.queries`) and histograms (`recall.duration`, `persist.duration`, `graphrag.duration`).
5. **Tracing** — All spans are emitted under `ActivitySource` name `"Neo4j.AgentMemory"` (version `1.0.0`).

**Namespace structure:**
```
Neo4j.AgentMemory.Observability    — all types (decorators, metrics, activity source, DI)
```

#### 3.4.4 Neo4j.AgentMemory.Extraction.AzureLanguage (Phase 5 ✅ COMPLETE)

| Attribute | Value |
|---|---|
| **Purpose** | Alternative extraction backend using Azure Cognitive Services (Text Analytics) |
| **Dependencies** | Abstractions (project ref), Core (project ref), Azure.AI.TextAnalytics 13.0.0, Microsoft.Extensions.DI/Logging.Abstractions 10.0.5 |
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
Neo4j.AgentMemory.Extraction.AzureLanguage    — Azure-backed extractors and DI
```

#### 3.4.5 Neo4j.AgentMemory.Enrichment (Phase 5 ✅ COMPLETE)

| Attribute | Value |
|---|---|
| **Purpose** | Geocoding and entity enrichment services with caching and rate limiting |
| **Dependencies** | Abstractions (project ref), Core (project ref), Microsoft.Extensions.DI/Logging/Caching.Abstractions 10.0.5 |
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
Neo4j.AgentMemory.Enrichment                           — services and DI
Neo4j.AgentMemory.Enrichment.Geocoding                 — Nominatim geocoding impl
Neo4j.AgentMemory.Enrichment.EntityEnrichment          — Wikimedia enrichment impl
Neo4j.AgentMemory.Enrichment.Decorators                — Cache/RateLimit decorators
```

#### 3.4.6 Future Adapter Packages

| Package | Phase | External Dependency | Implements |
|---|---|---|---|
| `Neo4j.AgentMemory.Mcp` | 6 | C# MCP SDK | MCP tool server exposing memory operations |

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

> **Note:** Entity-to-entity relationships use `RELATED_TO` via Neo4j native relationships (not a separate `:MemoryRelationship` node). The `Relationship` domain type maps to `RELATED_TO` relationship properties.

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
| `HAS_STEP` | ReasoningTrace | ReasoningStep | Trace contains steps (with `order` property) |
| `USES_TOOL` | ReasoningStep | ToolCall | Step-to-tool-call link |
| `INSTANCE_OF` | ToolCall | Tool | Links call to tool definition |
| `HAS_TRACE` | Conversation | ReasoningTrace | Conversation-to-trace |
| `INITIATED_BY` | ReasoningTrace | Message | Trace triggered by message |
| `TRIGGERED_BY` | ToolCall | Message | Tool call triggered by message |
| `EXTRACTED_FROM` | Entity/Fact/Preference | Message | Extraction provenance |
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
CREATE CONSTRAINT relationship_id IF NOT EXISTS FOR (r:MemoryRelationship) REQUIRE r.id IS UNIQUE  -- .NET extension
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

> **Known Gap:** A `task_embedding_idx` for `ReasoningTrace.taskEmbedding` is needed for `SearchByTaskVectorAsync` but not yet created. Will be added during Epic 6 (Reasoning Memory Repositories).

### 4.6 Property Indexes (Implemented in SchemaBootstrapper)

```cypher
CREATE INDEX message_session_id IF NOT EXISTS FOR (m:Message) ON (m.sessionId)
CREATE INDEX message_timestamp IF NOT EXISTS FOR (m:Message) ON (m.timestamp)
CREATE INDEX entity_type IF NOT EXISTS FOR (e:Entity) ON (e.type)
CREATE INDEX entity_name_prop IF NOT EXISTS FOR (e:Entity) ON (e.name)
CREATE INDEX fact_category IF NOT EXISTS FOR (f:Fact) ON (f.category)
CREATE INDEX preference_category IF NOT EXISTS FOR (p:Preference) ON (p.category)
CREATE INDEX reasoning_trace_session_id IF NOT EXISTS FOR (t:ReasoningTrace) ON (t.sessionId)
CREATE INDEX reasoning_step_timestamp IF NOT EXISTS FOR (s:ReasoningStep) ON (s.timestamp)
CREATE INDEX tool_call_status IF NOT EXISTS FOR (tc:ToolCall) ON (tc.status)
```

---

## 5. Boundary Enforcement Rules

These rules are inviolable. Violation of any rule is a blocking review finding.

| Rule | Constraint | Rationale |
|---|---|---|
| **B1** | Abstractions MUST NOT reference any NuGet package | Foundation layer stays portable; zero external coupling |
| **B2** | Core MUST NOT reference Neo4j.Driver | Orchestration layer is persistence-agnostic |
| **B3** | Core MUST NOT reference Microsoft.Agents.* | Core is framework-agnostic; MAF lives in adapter |
| **B4** | Core MUST NOT reference Neo4j.AgentFramework.GraphRAG | GraphRAG is a separate adapter |
| **B5** | Neo4j MUST NOT reference Microsoft.Agents.* | Persistence layer has no framework knowledge |
| **B6** | Neo4j MUST NOT reference Neo4j.AgentFramework.GraphRAG | Existing GraphRAG package is referenced only by future adapter |
| **B7** | No adapter may contain business logic that belongs in Core | Adapters are thin translation layers only |
| **B8** | Adapters depend on Core/Abstractions — never the reverse | Dependency inversion; core doesn't know about adapters |

**Enforcement:** Code review gates on all PRs. Future CI step to scan .csproj files for prohibited `<PackageReference>` entries.

**Current Verification (as of Phase 1):**
- ✅ Abstractions .csproj: zero `<PackageReference>` entries
- ✅ Core .csproj: only M.E.DI/Logging/Options 10.0.5
- ✅ Neo4j .csproj: only Neo4j.Driver 6.0.0 + M.E.DI/Logging/Options 10.0.5
- ✅ `grep` for `Microsoft.Agents` across `src/` returns zero matches
- ✅ `grep` for `Microsoft.Extensions.AI` across `src/` returns zero matches

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
| `IEmbeddingGenerator<string, Embedding<float>>` | This is from `Microsoft.Extensions.AI`; we define our own `IEmbeddingProvider` in Abstractions |
| `Neo4jContextProviderOptions.EmbeddingGenerator` | Tied to M.E.AI type system |
| `InvokingContext` / MAF lifecycle hooks | MAF-specific; our adapter (Phase 3) will bridge these |

### 6.4 How the GraphRAG Adapter Will Bridge (Phase 4)

```
┌──────────────────────┐     ┌──────────────────────────────────┐
│ Core Memory Engine   │     │ Neo4j.AgentMemory.GraphRagAdapter │
│                      │     │                                   │
│ IGraphRagContextSource ◄────── GraphRagContextSourceAdapter    │
│   (in Abstractions)  │     │     │                             │
│                      │     │     │ delegates to                │
│                      │     │     ▼                             │
│                      │     │   IRetriever                      │
│                      │     │   (from Neo4j.AgentFramework.     │
│                      │     │    GraphRAG)                      │
└──────────────────────┘     └──────────────────────────────────┘
```

The adapter will:
1. Reference `Neo4j.AgentFramework.GraphRAG` as a NuGet dependency
2. Implement our `IGraphRagContextSource` interface (defined in Abstractions)
3. Delegate search calls to the existing `IRetriever` implementations
4. Map `RetrieverResult` to our `GraphRagContextResult` domain type
5. Be registered via DI — Core never knows the adapter exists at compile time

### 6.5 Why We ADAPT Rather Than Fork or Wrap

1. **Don't fork**: the retriever code is coupled to `RetrieverResult` types and `IEmbeddingGenerator<string, Embedding<float>>` from M.E.AI. Forking creates maintenance burden with no upstream sync.
2. **Don't wrap in Core**: wrapping would add a dependency from our Neo4j package to `Neo4j.AgentFramework.GraphRAG`, violating boundary rule B6.
3. **Do adapt the Cypher**: the `db.index.vector.queryNodes` and `db.index.fulltext.queryNodes` patterns are the valuable knowledge. We copy Cypher query structures into our typed repositories, adapted to our schema.
4. **Do bridge in Phase 4**: the `GraphRagAdapter` package is the correct integration point — it wraps the existing package behind our `IGraphRagContextSource` interface.

### 6.6 MAF Version Gap

The existing neo4j-maf-provider was built for **MAF 0.3** (pre-GA). MAF is now **1.1.0 post-GA**. Key implications:
- `AIContextProvider` base class and `ProvideAIContextAsync(InvokingContext)` signature may have changed
- Our Phase 3 MAF adapter will target the current MAF 1.1.0 API surface
- The existing neo4j-maf-provider may need updating before the GraphRAG adapter can reference it

---

## 7. Test Strategy

*(Spec §2.4, Plan §16)*

| Test Layer | Project | Scope | Key Dependencies |
|---|---|---|---|
| **Unit** | `Neo4j.AgentMemory.Tests.Unit` | Core services, stubs, domain logic, validation | xUnit 2.9.2, FluentAssertions 8.9.0, NSubstitute 5.3.0, coverlet 6.0.2 |
| **Integration** | `Neo4j.AgentMemory.Tests.Integration` | Repository implementations, schema bootstrap, transaction behavior | Testcontainers.Neo4j 4.11.0, Neo4j.Driver 6.0.0, real Neo4j container |
| **E2E** | `Tests.E2E` (Phase 3+) | Full pipeline with MAF adapter | MAF test host + Testcontainers |

### Testing Rules

1. Every repository implementation gets **integration tests** before moving to the next repository
2. Every service implementation gets **unit tests** before the service is considered done
3. Integration tests use a **shared Neo4j fixture** (one Testcontainer per test run)
4. Unit tests use **NSubstitute mocks** via `MockFactory` — no real infrastructure
5. Test data seeders provide factory methods for all domain types

### Current Test Inventory

- **Unit tests (1058):** Covering all 10 src packages — domain models, services, repositories, extraction pipeline, entity resolution, MCP tools/resources/prompts, MAF adapter, GraphRAG, observability, enrichment, geocoding, configuration, datetime migration, session strategies, metadata filters
- **Integration tests (71):** Neo4j connectivity, repository CRUD, schema bootstrap, transaction behavior via Testcontainers
- **Test infrastructure:** Neo4jTestFixture, IntegrationTestBase, TestDataSeeders, MockFactory, Neo4jTestCollection

---

## 8. Phase Roadmap

| Phase | Name | Objective | Status |
|---|---|---|---|
| **0** | Discovery & Design Lock | Freeze architecture, interfaces, graph schema | ✅ Complete |
| **1** | Core Memory Engine | Framework-agnostic memory core + Neo4j persistence | ✅ **Complete** |
| **2** | LLM Extraction Pipeline | .NET-native structured extraction using LLMs | ✅ **Complete** |
| **3** | MAF Adapter | Microsoft Agent Framework integration | ✅ **Complete** |
| **4** | GraphRAG + Observability | GraphRAG adapter, blended context, OpenTelemetry | ✅ **Complete** |
| **5** | Advanced Extraction | Azure Language, geocoding, enrichment | ✅ **Complete** |
| **6** | MCP Server | External access via Model Context Protocol | ✅ **Complete** |
| **7** | Gap Closure (Waves A–C) | Python parity sprint — datetime, sessions, filters, MCP resources | ✅ **Complete** |

### All Phases Complete

All 6 implementation phases plus the gap closure sprint are complete. The project ships 10 packages with 1058 unit tests passing and ~99% functional parity with the Python reference.

### Phase 1 Exit Criteria

- ✅ All repositories implemented with Neo4j persistence
- ✅ All services unit tested
- ✅ All repositories integration tested with real Neo4j via Testcontainers
- ✅ Context assembler functional with configurable budgets
- ✅ No MAF or GraphRAG dependencies in Core or Abstractions
- ✅ Schema bootstrap creates all constraints and indexes (10 constraints, 14 property, 6 vector, 1 point, 3 fulltext)
- ✅ In-process memory engine works without Agent Framework

---

## 9. Package Strategy Analysis

**Added:** 2025-07-17  
**Author:** Deckard (Lead Architect)

### 9.1 Why 10 Packages? Dependency Isolation Audit

Each package exists to prevent a specific unwanted transitive dependency from reaching consumers who don't need it. The following table shows what each package adds to the dependency graph and why that isolation matters.

| # | Package | Key External Deps | Depends On (Project Refs) | Isolation Justification |
|---|---|---|---|---|
| 1 | **Abstractions** | *None* (BCL only) | — | **Foundation stone.** Zero-dep contract package. Every other package references this. If it pulled *anything*, every consumer inherits that cost. This is non-negotiable. |
| 2 | **Core** | FuzzySharp, M.E.AI.Abstractions, M.E.DI/Logging/Options | Abstractions | **Orchestration without infrastructure.** Services, entity resolution, extraction pipeline coordination. Depends only on lightweight M.E.* abstractions — no driver, no AI SDK, no framework. Consumers who only need in-memory stubs never touch Neo4j.Driver. |
| 3 | **Neo4j** | Neo4j.Driver 6.0.0 | Abstractions, Core | **Driver firewall.** The *only* package that references Neo4j.Driver. Without this separation, every consumer — including MCP, MAF, Observability — would transitively pull the driver. Driver is ~4 MB with native dependencies. |
| 4 | **Enrichment** | M.E.Http, M.E.Caching.Memory | Abstractions | **HTTP isolation.** Wikimedia/Nominatim enrichment requires HttpClient infrastructure and caching. Consumers who don't need external entity enrichment don't inherit HTTP factory overhead or geocoding dependencies. |
| 5 | **Extraction.AzureLanguage** | Azure.AI.TextAnalytics 5.3.0 | Abstractions | **Azure SDK firewall.** Azure.AI.TextAnalytics pulls Azure.Core, Azure.Identity, and their transitive graph (~12 packages). Users of LLM extraction or no extraction at all should never see these. |
| 6 | **Extraction.Llm** | M.E.AI.Abstractions | Abstractions, Core | **LLM extraction alternative.** Uses IChatClient for structured extraction. Separated from AzureLanguage so users choose one extraction backend without pulling the other. Depends on Core for extraction pipeline types. |
| 7 | **AgentFramework** | Microsoft.Agents.AI.Abstractions 1.1.0 | Abstractions, Core | **MAF firewall.** Microsoft Agent Framework is a specific runtime commitment. Non-MAF users (MCP hosts, standalone apps) should never see Microsoft.Agents.* in their dependency tree. |
| 8 | **GraphRagAdapter** | Neo4j.AgentFramework.GraphRAG (project ref) | Abstractions | **GraphRAG firewall.** The upstream Neo4j.AgentFramework.GraphRAG package pulls its own retriever/embedding infrastructure. Only consumers who specifically want GraphRAG retrieval should pay this cost. |
| 9 | **McpServer** | ModelContextProtocol 1.2.0, M.E.Hosting | Abstractions | **MCP SDK firewall.** MCP SDK + hosting stack is only relevant for MCP server deployments. Agent Framework consumers, library consumers, and CLI tools should never inherit MCP protocol overhead. |
| 10 | **Observability** | OpenTelemetry.Api 1.12.0 | Abstractions, Core | **OTel opt-in.** OpenTelemetry.Api is lightweight (~200 KB), but the pattern is correct: observability is a cross-cutting concern that should be additive, not mandatory. Consumers who don't export traces shouldn't reference OTel. |

### 9.2 Dependency Graph (Simplified)

```
                        ┌─────────────────────┐
                        │    Abstractions      │  ← zero deps (BCL only)
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
  └───────────┘ └───────────┘ └─────────────┘ └───────────────┘

  ┌──────────────┐   ┌──────────────┐
  │GraphRagAdapter│   │  McpServer   │
  │(GraphRAG ref) │   │(MCP SDK +   │
  └──────────────┘   │ Hosting)     │
                     └──────────────┘
```

### 9.3 Can We Simplify? Merger Candidates Analysis

| Merge Candidate | External Deps Gained | Verdict | Rationale |
|---|---|---|---|
| **Core + Neo4j** → single package | Neo4j.Driver 6.0.0 | ❌ **Do not merge** | Core is usable without Neo4j (in-memory stubs, testing). Merging forces every consumer to pull the driver (~4 MB + native deps) even when they only need service interfaces. This is the most valuable split in the system. |
| **Core + Observability** → single package | OpenTelemetry.Api | ⚠️ **Possible but not recommended** | OTel.Api is light (~200 KB), but making it mandatory violates the opt-in principle. Libraries shouldn't force telemetry on consumers. Keep separate. |
| **Extraction.Llm + Core** → single package | *None new* (same M.E.AI dep) | ⚠️ **Plausible** | Extraction.Llm depends on Core and shares the M.E.AI.Abstractions dependency. The extraction pipeline is architecturally part of Core's orchestration concern. *However*, keeping it separate lets users deploy Core without any LLM extraction cost, which is valid for read-only or manually-curated memory use cases. **Defer until user feedback says otherwise.** |
| **Enrichment + Core** → single package | M.E.Http, M.E.Caching | ❌ **Do not merge** | Enrichment adds HttpClient factory and caching infrastructure — real runtime overhead that most consumers won't need. It's an external API integration layer. |
| **AgentFramework + GraphRagAdapter** → single package | Both MS.Agents + GraphRAG | ❌ **Do not merge** | Different frameworks, different consumers. A MAF user may not want GraphRAG. A GraphRAG user may not want MAF lifecycle. Each pulls a distinct SDK. |
| **Extraction.AzureLanguage + Extraction.Llm** → single package | Azure.AI.TextAnalytics | ❌ **Do not merge** | Azure SDK is ~12 transitive packages. LLM extraction is lightweight. Merging forces Azure SDK on LLM-only users. The whole point of extraction backends is pick-one-or-both. |
| **McpServer + anything** | MCP SDK + Hosting | ❌ **Do not merge** | MCP is an executable deployment unit, not a library. It has fundamentally different packaging concerns (hosting, stdio/SSE transport). |

### 9.4 Recommendation: Keep 10 Packages

**The current 10-package topology is justified.** Each package isolates a genuine external dependency that would otherwise pollute consumers who don't need it. The four strongest splits are:

1. **Abstractions ↔ everything** — zero-dep contracts (industry standard pattern: cf. M.E.Logging.Abstractions)
2. **Core ↔ Neo4j** — driver isolation (the most impactful split)
3. **Extraction.AzureLanguage ↔ Extraction.Llm** — pick-your-backend without inheriting the other's SDK
4. **McpServer ↔ library packages** — executable vs. library concern separation

The only debatable merge is **Extraction.Llm → Core**, and even that should be deferred. The cognitive overhead of 10 projects is modest with a clear naming convention and the solution file organizes them well.

### 9.5 Consumer Use-Case Matrix

| Use Case | Packages Required | Package Count |
|---|---|---|
| **Library consumer (read/write memory)** | Abstractions + Core + Neo4j | 3 |
| **+ LLM extraction** | + Extraction.Llm | 4 |
| **+ Azure extraction** | + Extraction.AzureLanguage | 4–5 |
| **+ Entity enrichment** | + Enrichment | 4–6 |
| **MAF agent integration** | Abstractions + Core + Neo4j + AgentFramework | 4 |
| **GraphRAG retrieval only** | Abstractions + GraphRagAdapter | 2 |
| **MCP server deployment** | Abstractions + Core + Neo4j + McpServer | 4 |
| **+ Observability** | + Observability (additive to any above) | +1 |

---

## 10. DateTime Assessment — ISO Strings vs. Native `datetime()` (P1-9)

**Added:** 2025-07-17  
**Author:** Deckard (Lead Architect)  
**Tracking:** P1-9 (from package-strategy-and-features.md feature proposals)

### 10.1 Current State

All timestamps are stored as **ISO 8601 strings** using C#'s `DateTimeOffset.ToString("O")` roundtrip format (e.g., `"2025-07-17T14:30:00.0000000+00:00"`). They are parsed back using `DateTimeOffset.Parse(value, null, DateTimeStyles.RoundtripKind)`.

**Domain model types:** All timestamp properties use `DateTimeOffset` (correct .NET practice).

**Serialization boundary:** The `Neo4j.*Repository` classes convert `DateTimeOffset` → ISO string on write and string → `DateTimeOffset` on read.

**Inconsistency found:** `Neo4jEntityRepository.cs` uses Cypher's `datetime()` function directly in some MERGE clauses (entity resolution `SAME_AS` relationships, `merged_at` properties), while all other repositories pass ISO strings as parameters. This mixed approach is a minor schema inconsistency.

### 10.2 What Would Change

**Domain models:** No change needed. Properties remain `DateTimeOffset`.

**Repository files affected (12 files):**

| Repository File | Properties Affected |
|---|---|
| `Neo4jMessageRepository.cs` | `timestamp` |
| `Neo4jConversationRepository.cs` | `created_at`, `updated_at` |
| `Neo4jEntityRepository.cs` | `created_at` (+ normalize existing `datetime()` calls) |
| `Neo4jFactRepository.cs` | `created_at`, `valid_from`, `valid_until` |
| `Neo4jRelationshipRepository.cs` | `created_at`, `updated_at`, `valid_from`, `valid_until` |
| `Neo4jPreferenceRepository.cs` | `created_at` |
| `Neo4jReasoningTraceRepository.cs` | `started_at`, `completed_at` |
| `Neo4jReasoningStepRepository.cs` | `timestamp` |
| `Neo4jToolCallRepository.cs` | `created_at`, `started_at`, `completed_at` |
| `Neo4jSessionInfoRepository.cs` | `started_at`, `ended_at` |
| `SchemaBootstrapper.cs` | Index definitions (if any use timestamp properties) |
| `SchemaConstants.cs` | No change (constants are property name strings, not types) |

**Write path changes:**
```csharp
// Before (ISO string):
parameters.Add("created_at", entity.CreatedAtUtc.ToString("O"));

// After (native ZonedDateTime):
parameters.Add("created_at", new ZonedDateTime(entity.CreatedAtUtc));
```

**Read path changes:**
```csharp
// Before (ISO string parse):
DateTimeOffset.Parse(node["created_at"].As<string>(), null, DateTimeStyles.RoundtripKind)

// After (native ZonedDateTime):
node["created_at"].As<ZonedDateTime>().ToDateTimeOffset()
```

**Cypher query changes:** Queries using `ORDER BY m.timestamp` or `WHERE e.created_at > $since` would work *better* with native datetime (proper temporal comparison vs. lexicographic string comparison — though ISO 8601 is lexicographically sortable, native is semantically correct).

### 10.3 Benefits of Migration

| Benefit | Impact |
|---|---|
| **Correct temporal ordering** | Neo4j native `datetime()` supports `>`, `<`, `duration.between()` natively. ISO strings work lexicographically but can break with timezone offsets other than UTC. |
| **Temporal query support** | Enables Cypher temporal functions: `duration.between()`, `date.truncate()`, `datetime().year`, temporal range predicates. |
| **Schema consistency** | Eliminates the current mixed approach (some `datetime()`, some ISO strings). |
| **Neo4j Browser / Bloom UX** | Native datetime renders as a proper temporal value in Neo4j tools, not a raw string. |
| **Index efficiency** | Neo4j temporal indexes on native datetime are more efficient than string property indexes for range scans. |
| **Driver v6 support** | Neo4j.Driver 6.0.0 has first-class `ZonedDateTime` / `LocalDateTime` mapping. No workarounds needed. |

### 10.4 Risks and Breaking Changes

| Risk | Severity | Mitigation |
|---|---|---|
| **Existing data incompatibility** | 🔴 **High** | Databases with ISO string timestamps cannot be read by code expecting `ZonedDateTime`. Requires a data migration Cypher script or dual-read fallback. |
| **Breaking change for consumers** | 🟡 **Medium** | Domain model is unchanged (`DateTimeOffset`). The break is at the Neo4j property storage level — consumers don't see it unless they run raw Cypher against the graph. |
| **Integration test rewrites** | 🟡 **Medium** | All integration tests that assert on stored timestamp values will need updating. Test data seeders may need adjustment. |
| **MCP server raw queries** | 🟡 **Medium** | If MCP tools expose raw Cypher queries or return timestamp strings, the wire format changes. MCP tool contracts may need versioning consideration. |
| **Schema migration complexity** | 🟡 **Medium** | Need to migrate existing data: `SET n.created_at = datetime(n.created_at)` for every node type. Must handle null values and malformed strings. |
| **Timezone handling edge cases** | 🟢 **Low** | Current approach normalizes to UTC via `DateTimeOffset`. Neo4j `ZonedDateTime` preserves timezone info — which is actually an improvement. |

### 10.5 Data Migration Script (If Proceeding)

```cypher
// Run once per node label — example for Message nodes:
CALL apoc.periodic.iterate(
  "MATCH (n:Message) WHERE n.timestamp IS NOT NULL AND valueType(n.timestamp) = 'STRING' RETURN n",
  "SET n.timestamp = datetime(n.timestamp)",
  {batchSize: 1000, parallel: false}
)

// Repeat for: Conversation, Entity, Fact, Preference, Relationship,
//             ReasoningTrace, ReasoningStep, ToolCall, SessionInfo
```

*Note:* ISO 8601 strings produced by `ToString("O")` are valid input to Cypher's `datetime()` function, so the conversion is safe for well-formed data.

### 10.6 Status: ✅ COMPLETED (Gap Closure Sprint — G1)

The datetime migration was completed as part of the Gap Closure Sprint (Wave B). All 7 Neo4j repositories now use native `datetime()` storage via the `Neo4jDateTimeHelper` utility class. A backward-compatible reader gracefully handles both ISO-8601 strings and native datetime values during the transition period. 1058 unit tests pass with the migration in place.
