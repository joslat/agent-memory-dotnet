# Package Strategy & New Feature Proposals

**Document:** Strategic analysis of NuGet package topology and forward-looking feature proposals for Agent Memory for .NET.

**Author:** Deckard (Lead / Solution Architect)  
**Requested by:** Jose Luis Latorre Millas  
**Date:** April 2026

---

## Table of Contents

- [Part 1: Package Strategy Analysis](#part-1-package-strategy-analysis)
  - [1. Current Package Landscape](#1-current-package-landscape)
  - [2. Should Neo4jMemoryContextProvider Be a Separate NuGet Package?](#2-should-neo4jmemorycontextprovider-be-a-separate-nuget-package)
  - [3. Should GraphRAG Capabilities Be Separated?](#3-should-graphrag-capabilities-be-separated)
  - [4. What We Offer Over neo4j-maf-provider](#4-what-we-offer-over-neo4j-maf-provider)
  - [5. Use Case Matrix](#5-use-case-matrix)
  - [6. Recommended Package Topology](#6-recommended-package-topology)
- [Part 2: New Feature Proposals](#part-2-new-feature-proposals)
  - [7. Feature Proposals](#7-feature-proposals)
  - [8. Impact/Effort Grid](#8-impacteffort-grid)
  - [9. Priority Recommendations](#9-priority-recommendations)

---

## Part 1: Package Strategy Analysis

### 1. Current Package Landscape

Today, the solution contains **10 source projects** organized in a layered ports-and-adapters architecture:

```
┌─────────────────────────────────────────────────────────────────────┐
│                        ADAPTERS / EDGE                              │
│  ┌──────────────────┐ ┌───────────────────┐ ┌───────────────────┐  │
│  │  AgentFramework   │ │  GraphRagAdapter   │ │    McpServer      │  │
│  │ MAF 1.1.0         │ │ neo4j-maf-provider │ │ MCP 1.2.0         │  │
│  │ ~300 LOC          │ │ IRetriever reuse   │ │ 21 tools          │  │
│  └────────┬──────────┘ └────────┬──────────┘ └────────┬──────────┘  │
│           │                     │                     │              │
├───────────┼─────────────────────┼─────────────────────┼──────────────┤
│           │       EXTENSIONS / CROSS-CUTTING          │              │
│  ┌────────┴──────────┐ ┌───────┴───────────┐ ┌───────┴───────────┐  │
│  │  Extraction.Llm   │ │ Extraction.Azure   │ │  Observability    │  │
│  │ IChatClient        │ │ TextAnalytics      │ │ OpenTelemetry     │  │
│  └────────┬──────────┘ └───────┬───────────┘ └───────┬───────────┘  │
│           │                    │                     │              │
│  ┌────────┴──────────────────────────────────────────┘              │
│  │  Enrichment (Geocoding, HTTP, Caching)                           │
│  └────────┬──────────────────────────────────────────               │
│           │                                                         │
├───────────┼─────────────────────────────────────────────────────────┤
│           │            PERSISTENCE                                  │
│  ┌────────┴──────────┐                                              │
│  │   Neo4j            │  Neo4j.Driver 6.0.0                         │
│  │   Repositories     │  Schema, Cypher, Transactions               │
│  └────────┬──────────┘                                              │
│           │                                                         │
├───────────┼─────────────────────────────────────────────────────────┤
│           │            ORCHESTRATION                                │
│  ┌────────┴──────────┐                                              │
│  │   Core             │  FuzzySharp, M.E.* only                     │
│  │   Services,        │  Entity Resolution, Extraction Pipeline     │
│  │   Resolution       │                                             │
│  └────────┬──────────┘                                              │
│           │                                                         │
├───────────┼─────────────────────────────────────────────────────────┤
│           │            FOUNDATION                                   │
│  ┌────────┴──────────┐                                              │
│  │   Abstractions     │  ZERO external dependencies                 │
│  │   Domain, Services │  Pure C# records + interfaces               │
│  └───────────────────┘                                              │
└─────────────────────────────────────────────────────────────────────┘
```

**Current Dependency Graph (per .csproj analysis):**

| Package | External Dependencies | Internal Dependencies |
|---------|----------------------|----------------------|
| **Abstractions** | *none* | *none* |
| **Core** | FuzzySharp 2.0.2, M.E.DI/Logging/Options | Abstractions |
| **Neo4j** | Neo4j.Driver 6.0.0, M.E.DI/Logging/Options | Abstractions, Core |
| **AgentFramework** | Microsoft.Agents.AI 1.1.0, M.E.AI 10.4.1, M.E.DI/Logging/Options | Abstractions, Core |
| **GraphRagAdapter** | M.E.AI 10.4.1, M.E.DI/Logging/Options | Abstractions, neo4j-maf-provider (ProjectReference) |
| **Extraction.Llm** | M.E.AI 10.4.1, M.E.DI/Logging/Options | Abstractions, Core |
| **Extraction.AzureLanguage** | Azure.AI.TextAnalytics 5.3.0, M.E.DI/Logging/Options | Abstractions |
| **Enrichment** | M.E.Http, M.E.Caching.Memory, M.E.DI/Logging/Options | Abstractions |
| **Observability** | OpenTelemetry.Api 1.12.0, M.E.DI/Logging | Abstractions, Core |
| **McpServer** | ModelContextProtocol 1.2.0, M.E.Hosting | Abstractions |

---

### 2. Should Neo4jMemoryContextProvider Be a Separate NuGet Package?

**Answer: YES — and it already is.** The `Neo4j.AgentMemory.AgentFramework` project is architecturally correct as its own package. The question is whether the current boundary is optimal for NuGet publishing.

#### Dependency Analysis

The AgentFramework package pulls in:
- `Microsoft.Agents.AI.Abstractions 1.1.0` — **MAF-specific**, not needed by non-MAF users
- `Microsoft.Extensions.AI.Abstractions 10.4.1` — broadly useful but only needed here for ChatMessage mapping
- Standard M.E.* (DI, Logging, Options) — lightweight
- **Transitively:** Abstractions + Core (our own packages)

**Key Insight:** The MAF dependency is the entire reason this package exists as a separate assembly. Any user consuming just Core + Neo4j would be forced to pull in `Microsoft.Agents.AI` if this were bundled — an unacceptable coupling violation.

#### Consumer Analysis

| Consumer Type | Needs AgentFramework Package? | Why? |
|---|---|---|
| MAF developer with full memory | ✅ YES | Pre-run context + post-run extraction |
| MAF developer wanting just retrieval | ✅ YES (lighter alternative: neo4j-maf-provider) | Only if they want memory-backed recall |
| Semantic Kernel developer | ❌ NO | Would use a future SK adapter instead |
| Custom agent framework | ❌ NO | Use Core + Neo4j directly |
| MCP consumer | ❌ NO | Uses McpServer package |
| Library consumer (embedding in app) | ❌ NO | Use Abstractions + Core |

#### Versioning Story

MAF evolves independently from our core memory engine. Microsoft.Agents.AI is at 1.1.0 today; breaking changes are likely as MAF matures. Having AgentFramework as its own NuGet package means:
- **Core can rev independently** — memory engine improvements ship without touching MAF adapter
- **AgentFramework can track MAF versions** — update to MAF 2.0 without touching Core
- **Consumers pin what they need** — MAF user pins AgentFramework version; non-MAF user never sees it

#### Verdict

> ✅ **Neo4jMemoryContextProvider SHOULD be its own NuGet package (`Neo4j.AgentMemory.AgentFramework`).**
> 
> The boundary is correct as-is. It isolates MAF coupling, enables independent versioning, and prevents dependency pollution for non-MAF consumers. No structural change needed — this is a publish-as-NuGet decision.

---

### 3. Should GraphRAG Capabilities Be Separated?

This question has more nuance. Today we have two GraphRAG-related concepts:

#### What Exists Today

1. **`Neo4j.AgentMemory.GraphRagAdapter`** — Wraps the external `neo4j-maf-provider` project's `IRetriever` (Vector, Fulltext, Hybrid). Implements our `IGraphRagContextSource` abstraction. It is a **read-only bridge** to pre-built knowledge graph indexes.

2. **Our own memory engine** (Core + Neo4j) — Performs full CRUD with vector search, entity resolution, graph traversal, and context assembly. This is **not** GraphRAG in the traditional sense — it's a memory engine that *happens to use graph + vectors*.

#### The Separation Question

Should we extract a standalone "GraphRAG package" with *just* the retrieval capabilities (vector search, hybrid search, knowledge graph traversal) separate from the full memory engine?

**Analysis:**

| Aspect | Keep as-is (separate adapter) | Create new `Neo4j.AgentMemory.GraphRag` |
|--------|------|------|
| **Scope** | GraphRagAdapter wraps neo4j-maf-provider only | New package with our own vector/hybrid/fulltext retrieval |
| **Value** | Clean bridge to existing package | Self-contained retrieval without external project dependency |
| **Effort** | None (already exists) | Medium — extract retrieval logic from Neo4j package |
| **Risk** | External ProjectReference to neo4j-maf-provider | Duplicates retriever logic |
| **Consumer demand** | Users who have existing neo4j-maf-provider setup | Users who want retrieval without full memory engine |

#### Key Observation

The GraphRagAdapter currently depends on the `neo4j-maf-provider` project via **ProjectReference** (not a NuGet package). This is problematic for NuGet publishing — consumers would need the source code of neo4j-maf-provider checked out locally.

#### Recommended Approach: Two-Phase

**Phase A (Now):** Publish `Neo4j.AgentMemory.GraphRagAdapter` as a NuGet package that depends on a published NuGet of neo4j-maf-provider (coordinate with Neo4j team), OR internalize the retriever patterns.

**Phase B (Future):** Create `Neo4j.AgentMemory.Retrieval` — a standalone retrieval package that contains our own vector/fulltext/hybrid search implementations *extracted from the Neo4j persistence layer*. This gives users lightweight read-only retrieval without the full memory engine.

#### Verdict

> ✅ **Yes, GraphRAG retrieval should be available as a lighter package — but the path is to create `Neo4j.AgentMemory.Retrieval`, not just repackage the existing adapter.**
>
> The GraphRagAdapter serves a specific role (bridge to neo4j-maf-provider). A new Retrieval package would serve users who want search capabilities without committing to the full memory engine. This is a **future** concern — not blocking.

---

### 4. What We Offer Over neo4j-maf-provider

#### Feature Comparison

| Capability | neo4j-maf-provider | Agent Memory for .NET | Delta |
|---|---|---|---|
| **Read: Vector search** | ✅ | ✅ | Parity |
| **Read: Fulltext search** | ✅ | ✅ | Parity |
| **Read: Hybrid search** | ✅ | ✅ | Parity |
| **Read: Graph traversal (enrichment)** | ✅ (configurable Cypher) | ✅ (relationship traversal) | Parity |
| **Write: Message persistence** | ❌ | ✅ | **+NEW** |
| **Write: Entity CRUD** | ❌ | ✅ | **+NEW** |
| **Write: Fact/Preference CRUD** | ❌ | ✅ | **+NEW** |
| **Write: Reasoning trace storage** | ❌ | ✅ | **+NEW** |
| **Extraction: Entity extraction** | ❌ | ✅ (LLM + Azure Language) | **+NEW** |
| **Extraction: Fact/Pref extraction** | ❌ | ✅ | **+NEW** |
| **Resolution: Entity dedup** | ❌ | ✅ (exact→fuzzy→semantic→new) | **+NEW** |
| **Memory tiers** | 0 (stateless) | 3 (short/long/reasoning) | **+NEW** |
| **Context assembly** | Basic (index→format) | Advanced (multi-tier, blending, budget) | **+BETTER** |
| **Framework coupling** | Tight (MAF only) | Loose (core-agnostic) | **+BETTER** |
| **Observability** | ❌ | ✅ (OpenTelemetry) | **+NEW** |
| **MCP support** | ❌ | ✅ (21 tools, 6 resources, 3 prompts) | **+NEW** |
| **Schema management** | ❌ (assumes indexes exist) | ✅ (27 schema objects bootstrapped) | **+NEW** |
| **Testing** | Reference only | 419+ unit + integration tests | **+NEW** |

#### Use Case Served by Each

| Use Case | neo4j-maf-provider | Agent Memory for .NET |
|---|---|---|
| "I have a knowledge graph; inject it into MAF agent context" | ✅ **Primary** | ✅ Also supported (via GraphRagAdapter) |
| "I want my agent to learn and remember across conversations" | ❌ | ✅ **Primary** |
| "I want entity resolution and dedup" | ❌ | ✅ |
| "I want to use memory outside MAF" | ❌ (MAF-coupled) | ✅ (framework-agnostic core) |
| "I need observability on memory operations" | ❌ | ✅ |
| "I want MCP-based memory access for Claude/LLMs" | ❌ | ✅ |

#### Migration Path for neo4j-maf-provider Users

1. **Parallel mode:** Run both. Use `RetrievalBlendMode.Blended` to combine memory recall with GraphRAG retrieval.
2. **Feature parity:** Validate entity resolution and context quality match expectations.
3. **Cutover:** Replace `Neo4jContextProvider` registration with `Neo4jMemoryContextProvider`. Schema migration is one-time.
4. **Complementary long-term:** Keep neo4j-maf-provider for pure read-only knowledge graph retrieval alongside our memory engine.

---

### 5. Use Case Matrix

| Persona | Abstractions | Core | Neo4j | AgentFramework | GraphRagAdapter | Extraction.Llm | Extraction.Azure | Enrichment | Observability | McpServer |
|---|:---:|:---:|:---:|:---:|:---:|:---:|:---:|:---:|:---:|:---:|
| **MAF dev: full agent memory** | ✅ | ✅ | ✅ | ✅ | ◯ | ✅ | ◯ | ◯ | ◯ | ◯ |
| **MAF dev: just context retrieval** | ✅ | — | — | — | ✅¹ | — | — | — | — | — |
| **Non-MAF dev: agent memory (SK, custom)** | ✅ | ✅ | ✅ | — | ◯ | ✅ | ◯ | ◯ | ◯ | — |
| **MCP consumer** | ✅ | ✅ | ✅ | — | ◯ | ◯ | ◯ | ◯ | ◯ | ✅ |
| **Dev: just entity resolution** | ✅ | ✅ | — | — | — | — | — | — | — | — |
| **Dev: just extraction pipeline** | ✅ | ✅ | — | — | — | ✅ | ◯ | — | — | — |
| **Dev: GraphRAG retrieval only** | ✅ | — | — | — | ✅ | — | — | — | — | — |
| **Ops: observability** | ✅ | ✅ | ✅ | ◯ | ◯ | ◯ | ◯ | ◯ | ✅ | ◯ |

**Legend:** ✅ = Required | ◯ = Optional | — = Not needed | ¹ = Or use neo4j-maf-provider directly

---

### 6. Recommended Package Topology

#### NuGet Package Names & Structure

```
NuGet Packages (recommended publish order)
═══════════════════════════════════════════

TIER 1: Foundation
┌──────────────────────────────────────────┐
│  Neo4j.AgentMemory.Abstractions          │  ← Zero deps. Published first.
│  Contracts, domain models, interfaces    │     Every other package depends on this.
└──────────────────────────────────────────┘

TIER 2: Core Logic
┌──────────────────────────────────────────┐
│  Neo4j.AgentMemory.Core                  │  ← Abstractions + FuzzySharp + M.E.*
│  Entity resolution, extraction pipeline, │     Framework-agnostic business logic.
│  context assembly, service impls         │
└──────────────────────────────────────────┘

TIER 3: Infrastructure
┌──────────────────────────────────────────┐
│  Neo4j.AgentMemory.Neo4j                 │  ← Abstractions + Core + Neo4j.Driver
│  Repositories, schema, Cypher queries,   │     The persistence adapter.
│  transaction management                  │
└──────────────────────────────────────────┘

TIER 4: Framework Adapters (independent of each other)
┌──────────────────────────┐ ┌──────────────────────────┐
│  Neo4j.AgentMemory       │ │  Neo4j.AgentMemory       │
│  .AgentFramework         │ │  .GraphRagAdapter        │
│  MAF 1.1.0 adapter       │ │  IRetriever bridge       │
│  Context + Trace          │ │  Vector/FT/Hybrid        │
└──────────────────────────┘ └──────────────────────────┘
┌──────────────────────────┐
│  Neo4j.AgentMemory       │
│  .McpServer              │
│  14 MCP tools            │
│  ModelContextProtocol    │
└──────────────────────────┘

TIER 4: Extension Packages (independent of each other)
┌──────────────────────────┐ ┌──────────────────────────┐
│  Neo4j.AgentMemory       │ │  Neo4j.AgentMemory       │
│  .Extraction.Llm         │ │  .Extraction.Azure       │
│  IChatClient extraction  │ │  Azure TextAnalytics     │
└──────────────────────────┘ └──────────────────────────┘
┌──────────────────────────┐ ┌──────────────────────────┐
│  Neo4j.AgentMemory       │ │  Neo4j.AgentMemory       │
│  .Enrichment             │ │  .Observability          │
│  Geocoding, HTTP cache   │ │  OpenTelemetry           │
└──────────────────────────┘ └──────────────────────────┘

FUTURE (proposed, not yet implemented):
┌──────────────────────────┐ ┌──────────────────────────┐
│  Neo4j.AgentMemory       │ │  Neo4j.AgentMemory       │
│  .SemanticKernel         │ │  .Retrieval              │
│  SK memory plugin        │ │  Standalone read-only    │
│                          │ │  vector/hybrid search    │
└──────────────────────────┘ └──────────────────────────┘
```

#### Dependency Diagram

```
                    Abstractions (zero deps)
                    ┌───────────┐
                    │           │
          ┌────────┤           ├──────────┬──────────┬──────────┐
          │        │           │          │          │          │
          ▼        └─────┬─────┘          ▼          ▼          ▼
        Core             │           Extraction  Enrichment   McpServer
     ┌────────┐          │           .Azure
     │        │          │
     ├────────┼──────────┼───────┬────────────┬──────────┐
     │        │          │       │            │          │
     ▼        ▼          │       ▼            ▼          ▼
   Neo4j  AgentFwk       │   Extraction    Observ.   (future:
              │          │      .Llm                   SK,
              │          │                             Retrieval)
              │          ▼
              │     GraphRag
              │     Adapter
              │
              └──→ Microsoft.Agents.AI (external)
```

#### Package Summary Table

| Package | NuGet ID | Target Audience | Key External Deps | Install Size* |
|---------|----------|----------------|-------------------|---------------|
| Abstractions | `Neo4j.AgentMemory.Abstractions` | Everyone | *none* | Tiny (~50 KB) |
| Core | `Neo4j.AgentMemory.Core` | Library devs, pipeline builders | FuzzySharp | Small (~200 KB) |
| Neo4j | `Neo4j.AgentMemory.Neo4j` | App devs using Neo4j | Neo4j.Driver | Medium (~400 KB) |
| AgentFramework | `Neo4j.AgentMemory.AgentFramework` | MAF developers | Microsoft.Agents.AI | Small (~100 KB) |
| GraphRagAdapter | `Neo4j.AgentMemory.GraphRagAdapter` | GraphRAG users | neo4j-maf-provider† | Small (~80 KB) |
| Extraction.Llm | `Neo4j.AgentMemory.Extraction.Llm` | Apps with LLM access | M.E.AI.Abstractions | Small (~100 KB) |
| Extraction.Azure | `Neo4j.AgentMemory.Extraction.AzureLanguage` | Azure shops | Azure.AI.TextAnalytics | Small (~80 KB) |
| Enrichment | `Neo4j.AgentMemory.Enrichment` | Apps needing entity enrichment | M.E.Http, M.E.Caching | Small (~60 KB) |
| Observability | `Neo4j.AgentMemory.Observability` | Production deployments | OpenTelemetry.Api | Small (~80 KB) |
| McpServer | `Neo4j.AgentMemory.McpServer` | MCP ecosystem (Claude, etc.) | ModelContextProtocol | Small (~100 KB) |

*\* Estimated assembly sizes, not including transitive dependencies.*  
*† GraphRagAdapter currently uses ProjectReference to neo4j-maf-provider source; needs resolution for NuGet publishing.*

#### Meta-package Recommendation

For convenience, consider publishing a meta-package:

| Meta-package | Contents | Use Case |
|---|---|---|
| `Neo4j.AgentMemory` | Abstractions + Core + Neo4j + Extraction.Llm | "Give me everything for a basic memory-enabled agent" |
| `Neo4j.AgentMemory.All` | All 10 packages | "Give me the full kitchen sink" |

---

## Part 2: New Feature Proposals

### 7. Feature Proposals

#### Performance

| # | Feature | Description | Impact | Effort | Rationale | Implementation Approach | Dependencies |
|---|---------|-------------|--------|--------|-----------|------------------------|--------------|
| P1 | **Batch Operations** | UNWIND-based batch upsert for messages, entities, facts, preferences. `UpsertBatchAsync` across all repositories. | **High** | **M** | Python already has this. Current N+1 pattern in extraction (each entity upserted individually) is the #1 perf bottleneck for write-heavy workloads. | Add `UpsertBatchAsync(IEnumerable<T>)` to all repositories. Use Cypher `UNWIND $items AS item MERGE ...` pattern. | Neo4j package |
| P2 | **Embedding Cache** | In-memory LRU cache for recently computed embeddings. Avoid re-embedding identical strings. | **Medium** | **S** | Embedding calls are the single most expensive operation (network + LLM inference). Caching avoids redundant calls for repeated entities ("the user", "Neo4j"). | `IEmbeddingCache` interface in Abstractions. `MemoryEmbeddingCache` in Core using `IMemoryCache`. Keyed by SHA256 of input text. TTL configurable. | Core, M.E.Caching.Memory |
| P3 | **Parallel Tier Recall** | Execute short-term, long-term, and reasoning recall concurrently using `Task.WhenAll`. | **Medium** | **S** | Currently sequential in MemoryContextAssembler. Each tier hits Neo4j independently — 3× latency reduction possible. | Refactor `AssembleContextAsync` to launch 3 parallel tasks. Requires independent transaction scopes per tier. | Core refactor |
| P4 | **Connection Pool Tuning** | Expose Neo4j driver connection pool configuration (`MaxConnectionPoolSize`, `ConnectionAcquisitionTimeout`) through `IOptions<Neo4jOptions>`. | **Low** | **S** | Under high concurrency, default pool settings may throttle. Exposing as config lets operators tune without code changes. | Add pool properties to `Neo4jOptions`. Apply in `DriverFactory`. | Neo4j package |

#### Observability

| # | Feature | Description | Impact | Effort | Rationale | Implementation Approach | Dependencies |
|---|---------|-------------|--------|--------|-----------|------------------------|--------------|
| O1 | **Health Checks** | ASP.NET Core `IHealthCheck` implementations: Neo4j connectivity, schema version, embedding provider availability. | **High** | **S** | Production deployments need health endpoints for load balancers and monitoring. Python issue #91 requests this same capability. | `Neo4jHealthCheck : IHealthCheck` — runs `RETURN 1` against driver. `SchemaHealthCheck` — verifies constraint count. `EmbeddingHealthCheck` — tests provider with probe text. | Neo4j, M.E.Diagnostics.HealthChecks |
| O2 | **Structured Logging Enrichment** | Add `SessionId`, `ConversationId`, `EntityCount`, `OperationType` as structured log properties on all memory operations. | **Medium** | **S** | Makes log filtering and correlation trivial in Seq, Datadog, etc. Currently logs are present but not enriched with memory-domain context. | Use `ILogger.BeginScope()` with structured properties in service decorators. | Observability package |
| O3 | **Memory Usage Dashboard Template** | Grafana JSON dashboard template for memory metrics: recall latency, extraction counts, entity resolution hit rates, tier distributions. | **Low** | **S** | Reduces time-to-value for observability adoption. "Install package, import dashboard, see metrics." | JSON template file in `samples/` or `docs/`. Pre-configured panels for all exported metrics. | Observability, Grafana |

#### Developer Experience

| # | Feature | Description | Impact | Effort | Rationale | Implementation Approach | Dependencies |
|---|---------|-------------|--------|--------|-----------|------------------------|--------------|
| D1 | **Fluent Configuration Builder** | `services.AddAgentMemory(m => m.UseNeo4j(n => n.Uri("...")).UseLlmExtraction().UseObservability())` — single fluent entry point. | **High** | **M** | Current DI setup requires 5-6 separate `Add*` calls. Fluent builder reduces boilerplate and guides users through the configuration pit-of-success. | `AgentMemoryBuilder` class with method chaining. Each extension method calls the appropriate `IServiceCollection` registration. Terminal `.Build()` validates configuration. | All packages contribute extension methods |
| D2 | **Source Generator for MCP Tools** | Auto-generate MCP tool registrations from `IMemoryService` methods using a Roslyn source generator. | **Low** | **L** | Nice-to-have for keeping MCP server in sync with service API. Current manual registration works fine for 21 tools. | `[McpToolExport]` attribute + Roslyn IIncrementalGenerator. Generates `McpToolRegistrations.g.cs`. | McpServer, Roslyn APIs |
| D3 | **Memory Playground CLI** | Standalone `dotnet tool` that connects to Neo4j and exposes memory operations as interactive REPL commands: `recall "Alice"`, `extract "Alice likes pizza"`, `stats`. | **Medium** | **M** | Enables rapid experimentation without writing a full app. Python has CLI request (issue #11). | `dotnet tool install Neo4j.AgentMemory.Cli`. Uses Spectre.Console for TUI. Connects to Neo4j via config file or args. | Abstractions, Core, Neo4j, Spectre.Console |

#### Intelligence

| # | Feature | Description | Impact | Effort | Rationale | Implementation Approach | Dependencies |
|---|---------|-------------|--------|--------|-----------|------------------------|--------------|
| I1 | **Conversation Summarization** | Auto-generate conversation summaries after N messages. Store as `ConversationSummary` nodes. Use in context assembly for older conversations. | **High** | **M** | Python issue #44 explicitly requests this. Long conversations waste token budget on individual messages when a summary would suffice. Reduces context assembly costs. | `ISummarizationService` in Core. Uses `IChatClient` for LLM-based summarization. Trigger: configurable threshold (e.g., every 10 messages). Summary node linked to Conversation via `HAS_SUMMARY`. | Core, Extraction.Llm |
| I2 | **Memory Decay / TTL** | Time-based relevance scoring. Older facts/preferences decay in relevance unless reinforced by new mentions. Configurable decay curves. | **High** | **M** | Python issue #42 explicitly requests this. Without decay, old irrelevant facts pollute context ("User liked Java in 2019" crowds out "User now uses Rust"). | `IDecayStrategy` interface. `ExponentialDecayStrategy`, `LinearDecayStrategy`. Applied as score modifier in `MemoryContextAssembler`. Optional background job to prune expired items. | Core, Abstractions |
| I3 | **Smarter Extraction: Relationship Mining** | Extract relationship types between entities automatically ("Alice manages Bob", "Project X uses Neo4j") beyond simple entity extraction. | **Medium** | **L** | Current extraction finds entities and facts independently. Relationship mining connects them in the graph, enabling richer traversal ("who manages whom?"). | Extend `IRelationshipExtractor` with LLM prompt that outputs `(subject, predicate, object)` triples. Map to Neo4j relationships. Dedup against existing. | Extraction.Llm |
| I4 | **Auto-Cleanup / Dedup** | Periodic background job that finds near-duplicate facts (embedding cosine similarity > 0.95), merges them, and links via `SAME_AS`. | **Medium** | **M** | Over time, similar facts accumulate ("Alice likes pizza", "Alice enjoys pizza", "Alice prefers pizza"). Dedup keeps the graph clean. Python community issue #77 relates. | `IMemoryMaintenanceService` in Core. Scheduled via `IHostedService`. Uses vector similarity search to find candidates. Merge strategy preserves highest-confidence version. | Core, Neo4j |
| I5 | **Temporal Resolution** | Track when facts become true/false. "Alice was CEO from 2020-2023" vs "Alice is CTO since 2024". Temporal properties on Fact/Relationship nodes. | **Medium** | **L** | Python issue #6 requests this. Without temporal tracking, stale facts ("Alice is CEO") may override current truth. Critical for long-lived agents. | Add `ValidFrom`, `ValidUntil` nullable DateTime properties to Fact/Relationship records. Extraction prompts ask for temporal qualifiers. Context assembler filters by current time. | Abstractions, Core, Extraction.Llm |

#### Integration

| # | Feature | Description | Impact | Effort | Rationale | Implementation Approach | Dependencies |
|---|---------|-------------|--------|--------|-----------|------------------------|--------------|
| F1 | **Semantic Kernel Memory Plugin** | `IMemoryStore` implementation for Microsoft Semantic Kernel. Enables SK agents to use Neo4j agent memory. | **High** | **M** | SK is the most popular .NET AI framework. A first-class adapter (like our MAF adapter) unlocks the largest .NET AI developer audience. | New `Neo4j.AgentMemory.SemanticKernel` package. Implement `IMemoryStore` and `ITextMemory` interfaces. Map SK's memory model to our 3-tier model. | New package, Microsoft.SemanticKernel.Abstractions |
| F2 | **AutoGen Adapter** | Integration with Microsoft AutoGen for multi-agent scenarios. Shared memory across agent group. | **Medium** | **M** | AutoGen is growing fast for multi-agent orchestration. Shared memory between agents is a key differentiator. | New `Neo4j.AgentMemory.AutoGen` package. Implement AutoGen's state management interfaces. Multi-agent session isolation via `AgentId` scoping. | New package, AutoGen SDK |
| F3 | **LangChain.NET Adapter** | Memory integration for LangChain .NET port (if/when it matures). | **Low** | **M** | Speculative — LangChain .NET is not yet mature. Keep on radar. | Future: implement LangChain's memory interface. | New package, LangChain.NET |
| F4 | **Google ADK Adapter** | Integration with Google's Agent Development Kit. Python version already has community demand (issue #86). | **Medium** | **M** | Google ADK is an emerging framework. Being first to offer .NET memory integration is a competitive advantage. | New `Neo4j.AgentMemory.GoogleAdk` package. Implement ADK's BaseMemoryService interface equivalent. | New package, Google ADK .NET SDK |

#### Operations

| # | Feature | Description | Impact | Effort | Rationale | Implementation Approach | Dependencies |
|---|---------|-------------|--------|--------|-----------|------------------------|--------------|
| Ops1 | **Schema Migration Runner** | Versioned schema migrations with up/down support. Track applied migrations in a `_migrations` node. | **Medium** | **M** | SchemaBootstrapper creates schema from scratch but can't handle evolution. As we add new indexes/constraints, existing deployments need upgrade paths. | `IMigrationRunner` interface. Migrations as numbered classes. `_SchemaVersion` node tracks applied version. `dotnet agent-memory migrate` CLI command. | Neo4j package |
| Ops2 | **Graph Export/Import** | Export agent memory subgraph (per user/session) as JSON-LD or Cypher dump. Import for backup/restore or environment migration. | **Medium** | **M** | Python community (issue #91 related) needs memory portability. Essential for backup, environment migration, and debugging. | `IMemoryExporter` / `IMemoryImporter` in Abstractions. JSON-LD serialization. Cypher LOAD CSV for bulk import. Scope by SessionId or UserId. | Core, Neo4j |
| Ops3 | **Multi-Tenant Isolation** | Scope all memory operations by `TenantId`. Separate graph partitions per tenant using Neo4j labels or separate databases. | **Medium** | **L** | SaaS deployments need tenant isolation. Without it, one tenant's agent can see another's memories. | Add `TenantId` to all domain records. Repository-level filtering. Option A: label-based (`Tenant_X:Entity`). Option B: multi-database (Neo4j Enterprise). | All packages |

#### Security

| # | Feature | Description | Impact | Effort | Rationale | Implementation Approach | Dependencies |
|---|---------|-------------|--------|--------|-----------|------------------------|--------------|
| S1 | **PII Detection & Redaction** | Scan extracted entities/facts for PII (emails, phones, SSNs). Redact or flag before persistence. | **High** | **M** | GDPR/CCPA compliance. Agents may inadvertently memorize sensitive user data. Python issue #13 requests security features. | `IPiiDetector` interface. Azure AI Text Analytics or regex-based fallback. Run in extraction pipeline before persistence. Configurable: redact, hash, flag, or block. | Extraction pipeline, optionally Azure.AI |
| S2 | **Memory Access Control** | RBAC for memory operations. Agent A can read but not write to Agent B's memory. User-scoped memory visibility. | **Medium** | **L** | Multi-agent and multi-user deployments need access boundaries. Without ACL, any agent can read/modify any memory. | `IMemoryAccessPolicy` interface. Claim-based authorization in service decorators. Neo4j label-based visibility filtering. | Core, Abstractions |
| S3 | **Encryption at Rest** | Encrypt sensitive fields (entity names, fact text, preferences) before Neo4j persistence. Application-level encryption with key rotation. | **Medium** | **L** | Defense-in-depth. Even if Neo4j is compromised, extracted memories remain encrypted. | `IFieldEncryptor` interface. AES-256-GCM encryption. Key stored in Azure Key Vault or similar. Applied transparently in repository layer. | Neo4j package, key management |

---

### 8. Impact/Effort Grid

```
                    ║  Small (S)           │  Medium (M)          │  Large (L)          │  XL
════════════════════╬══════════════════════╪══════════════════════╪═════════════════════╪═══════════
  HIGH Impact       ║  O1 Health Checks    │  P1 Batch Ops        │                     │
                    ║                      │  I1 Conversation Sum  │                     │
                    ║                      │  I2 Memory Decay      │                     │
                    ║                      │  D1 Fluent Config     │                     │
                    ║                      │  F1 Semantic Kernel   │                     │
                    ║                      │  S1 PII Detection     │                     │
────────────────────╫──────────────────────┼──────────────────────┼─────────────────────┼──────────
  MEDIUM Impact     ║  P2 Embedding Cache  │  I4 Auto-Cleanup      │  I3 Relationship    │
                    ║  P3 Parallel Recall  │  F2 AutoGen           │      Mining         │
                    ║  O2 Log Enrichment   │  F4 Google ADK        │  I5 Temporal Res.   │
                    ║                      │  Ops1 Schema Migrate  │  Ops3 Multi-Tenant  │
                    ║                      │  Ops2 Graph Export    │  S2 Access Control  │
                    ║                      │  D3 Memory CLI        │  S3 Encryption      │
────────────────────╫──────────────────────┼──────────────────────┼─────────────────────┼──────────
  LOW Impact        ║  P4 Pool Tuning      │  F3 LangChain.NET    │  D2 Source Gen      │
                    ║  O3 Dashboard        │                      │                     │
════════════════════╩══════════════════════╧══════════════════════╧═════════════════════╧══════════

LEGEND:  P=Performance  O=Observability  D=DevEx  I=Intelligence  F=Integration  Ops=Operations  S=Security
```

---

### 9. Priority Recommendations

#### Tier 1: Do Next (High impact, reasonable effort)

| Priority | Feature | Why Now |
|----------|---------|---------|
| 🥇 1 | **P1: Batch Operations** | Directly addresses perf bottleneck. Python already has it. Prerequisite for production workloads. |
| 🥇 2 | **O1: Health Checks** | Small effort, essential for production. Python community is asking for it (issue #91). |
| 🥇 3 | **I1: Conversation Summarization** | High user demand (Python #44). Reduces context budget waste. Natural extension of existing extraction pipeline. |
| 🥇 4 | **D1: Fluent Configuration Builder** | Dramatically improves onboarding experience. Every user benefits. |
| 🥇 5 | **F1: Semantic Kernel Adapter** | Unlocks the largest .NET AI developer audience. Positions us as the default memory layer for SK. |

#### Tier 2: Do Soon (High/Medium impact, moderate effort)

| Priority | Feature | Why Soon |
|----------|---------|----------|
| 🥈 6 | **I2: Memory Decay** | Python #42 requests it. Essential for long-lived agents. Prevents context pollution. |
| 🥈 7 | **S1: PII Detection** | Compliance requirement for enterprise adoption. Python #13 requests security features. |
| 🥈 8 | **P2: Embedding Cache** | Quick win. Reduces cost (LLM API calls) and latency. |
| 🥈 9 | **P3: Parallel Tier Recall** | Quick win. Direct latency reduction in the hot path. |
| 🥈 10 | **Ops1: Schema Migration Runner** | Essential before first NuGet publish. Existing deployments need upgrade paths. |

#### Tier 3: Do When Ready (Medium impact, larger effort or emerging dependencies)

| Priority | Feature | When |
|----------|---------|------|
| 🥉 11 | **I4: Auto-Cleanup** | After batch operations are in place (P1 prerequisite). |
| 🥉 12 | **Ops2: Graph Export/Import** | Before enterprise pilots. Backup/restore is table stakes. |
| 🥉 13 | **F2: AutoGen Adapter** | When AutoGen .NET SDK stabilizes. |
| 🥉 14 | **F4: Google ADK Adapter** | When Google ADK .NET SDK ships. |
| 🥉 15 | **D3: Memory CLI** | After fluent config (D1). Makes debugging and experimentation frictionless. |

#### Tier 4: Backlog (Monitor & Plan)

| Feature | Trigger |
|---------|---------|
| **I5: Temporal Resolution** | When temporal facts become a reported problem in user feedback. |
| **I3: Relationship Mining** | When basic extraction is mature and users ask for deeper graph connectivity. |
| **Ops3: Multi-Tenant** | When first SaaS customer needs isolation. |
| **S2: Access Control** | When multi-agent deployments with security requirements appear. |
| **S3: Encryption at Rest** | When enterprise security review requires it. |
| **D2: Source Generator** | Low priority; manual MCP tool registration works fine. |
| **F3: LangChain.NET** | If/when LangChain .NET matures. |

---

## Appendix: Python agent-memory Open Issues We Can Get Ahead Of

The `neo4j-labs/agent-memory` Python project has 21 open issues. Several map directly to our feature proposals:

| Python Issue | Our Feature | Status |
|---|---|---|
| #91 — Memory health diagnostic tool | **O1: Health Checks** | Proposed (Tier 1) |
| #44 — Conversation summary property | **I1: Conversation Summarization** | Proposed (Tier 1) |
| #42 — Memory decay in search retrievers | **I2: Memory Decay** | Proposed (Tier 2) |
| #13 — Security/Auth features | **S1: PII Detection**, **S2: Access Control** | Proposed (Tiers 2, 4) |
| #11 — Add CLI | **D3: Memory CLI** | Proposed (Tier 3) |
| #6 — Temporal resolution for long-term memory | **I5: Temporal Resolution** | Proposed (Tier 4) |
| #4 — TypeScript client | N/A (different language, but our MCP server enables it!) | MCP covers this |
| #2 — Time travel and conversation forking | Future investigation | Not yet proposed |
| #1 — Offline background enrichment | Partially covered by **Enrichment** package | Exists |
| #8 — Performance improvements | **P1–P4** | Proposed (Tiers 1–2) |
| #104 — Embedding model context length | **P2: Embedding Cache** + chunking strategy | Proposed (Tier 2) |
| #90 — add_fact should report entity linking | Already in our design (extraction returns `EntityResolutionResult`) | Done ✅ |
| #77 — Facts not linked to entities | Our cross-memory relationships address this | Partially done |
| #86 — Google ADK validation failure | **F4: Google ADK Adapter** | Proposed (Tier 3) |

**Competitive advantage:** By implementing Tiers 1–2, we would address 8 of the 21 open Python issues *before* the Python project does, positioning .NET as the more mature agent memory implementation.

---

*This document should be reviewed and updated as the project evolves. Feature priorities may shift based on community feedback, framework maturity, and enterprise adoption patterns.*
