# Agent Memory for .NET

> Independent community project. Not affiliated with, endorsed by, or supported by Neo4j, Inc.  
> This project is a .NET implementation inspired by `neo4j-labs/agent-memory` and designed to interoperate with Neo4j and the public `neo4j-maf-provider` GraphRAG integration.

Persistent graph-native memory for AI agents in .NET, built for **Microsoft Agent Framework** and designed to work with **Neo4j**.

This project aims to bring the ideas and capabilities of the Python-based [`neo4j-labs/agent-memory`](https://github.com/neo4j-labs/agent-memory) project into a **native .NET implementation**, while also integrating with the existing [`neo4j-labs/neo4j-maf-provider`](https://github.com/neo4j-labs/neo4j-maf-provider) for **GraphRAG interoperability**.

## What this project is

This repository is building a **.NET memory provider for AI agents** with three core memory layers:

- **Short-term memory** for recent conversations and session history
- **Long-term memory** for entities, preferences, facts, and relationships
- **Reasoning memory** for traces, tool usage, and prior execution patterns

It is designed to support:

- **Microsoft Agent Framework** with context injection, chat store, and memory tools
- **Semantic Kernel** adapter for native integration
- **Neo4j-backed persistent memory** with vector, fulltext, and hybrid search
- **Graph-native retrieval** with relationship traversal and entity resolution
- **GraphRAG interoperability** for blended retrieval and knowledge graph enrichment
- **MCP Server** for external clients and tools (25 tools, 6 resources, 3 prompts)

## Why this exists

The Python project `neo4j-labs/agent-memory` already demonstrates a strong graph-native memory architecture for AI agents.

This project exists to provide a **native .NET equivalent**, so that .NET developers can build agents with:

- persistent memory in Neo4j
- structured long-term knowledge
- session-aware recall
- reasoning trace storage
- first-class integration with Microsoft Agent Framework

## Relationship to the upstream projects

This is an **independent implementation**.

It is:

- **inspired by** `neo4j-labs/agent-memory`
- **designed to interoperate with** `neo4j-labs/neo4j-maf-provider`
- **implemented independently in .NET**

It is **not**:

- an official Neo4j repository
- a Neo4j, Inc. product
- a fork intended to be presented as the upstream project

## Architecture direction

The solution is built in layers with a focus on clean separation of concerns:

### 1. Core memory engine
Framework-agnostic contracts and services for:

- message storage with conversation lifecycle
- memory extraction orchestration (ExtractionStage → PersistenceStage pipeline with streaming support)
- entity extraction, resolution, and deduplication
- fact and preference storage with embedding orchestration
- reasoning traces and step tracking with tool call recording
- context assembly with N+1 pagination pattern and token budget enforcement
- memory decay simulation and temporal retrieval (point-in-time queries)

### 2. Neo4j persistence layer
Unified Neo4j-backed implementations for:

- graph persistence with centralized Cypher queries organized by domain
- vector/fulltext/hybrid/graph search with 6+ indexes
- relationship traversal and entity resolution with cross-tier relationships
- memory retrieval with metadata filtering and structured error handling (MemoryErrorBuilder)
- Cypher snapshot testing for query validation

### 3. Microsoft Agent Framework adapter
A dedicated integration layer on top of the core for:

- context injection before agent runs via `IContextProvider`
- persistence after agent runs with automatic trace recording
- Neo4j-backed chat/message store
- memory tools for agent usage (AIFunction decorator)
- reasoning trace recording with tool call status tracking

### 4. Semantic Kernel adapter
Native integration for Semantic Kernel workflows:

- memory service injection as plugin
- memory functions exposed as SK skills
- embedding coordination with SK's native connector

### 5. GraphRAG interoperability
Internalized retrieval layer providing:

- vector retrieval (db.index.vector.queryNodes)
- fulltext retrieval (db.index.fulltext.queryNodes with BM25 scoring)
- hybrid retrieval (combined vector + fulltext with reranking)
- graph-enriched context retrieval with custom traversal patterns

### 6. MCP layer
A .NET MCP server exposing 25 memory tools, 6 resources, and 3 prompts to external MCP clients (Claude Desktop, etc.) via stdio and HTTP transports.

### 7. Observability layer
OpenTelemetry decorators for:

- distributed tracing across memory, extraction, and persistence operations
- metrics emission for query latency, cache hits, and error rates
- structured logging with operation context

## Capabilities

### Short-term memory
- session-scoped conversation history with participant tracking
- recent message recall with timestamp filtering
- semantic message search via vector embeddings
- conversation summaries and compression

### Long-term memory
- entities with canonical names, aliases, and categories
- facts with provenance and source attribution
- preferences with temporal scoping and decay
- relationships between entities in the knowledge graph
- optional geospatial and enrichment support (Nominatim, Wikimedia)

### Reasoning memory
- reasoning traces from agent reasoning chains
- individual steps with timestamps and metadata
- tool usage tracking with call status and outcomes
- similar-task retrieval for pattern recognition

### Search and retrieval
- BM25 fulltext search for natural language queries
- Vector semantic search with embedding models
- Hybrid search (vector + fulltext combined with reranking)
- Graph-native retrieval with relationship traversal
- Temporal retrieval (point-in-time memory access)
- Metadata filtering with $eq, $ne, $contains, $in, $exists operators

### Framework integration
- Microsoft Agent Framework context provider and tools
- Semantic Kernel memory plugin
- Pre-run context assembly with N+1 pagination
- Post-run memory persistence with trace recording
- Neo4j-backed message store with session management

### GraphRAG interoperability
- use existing Neo4j GraphRAG context sources
- keep GraphRAG as a separate retrieval component
- allow blended retrieval from persistent memory and GraphRAG sources
- shared entity resolution across both systems

## Initial scope

The first implementation focus is:

1. **Native .NET Neo4j memory core**
2. **Microsoft Agent Framework adapter**
3. **GraphRAG retrieval built into `AgentMemory.Neo4j`** (internalized from separate adapter pattern)
4. **Tests and validation harness**
5. **MCP server for external client access**

## Non-goals for the first version

The first version will **not** aim for full 1:1 parity with every Python integration or every Python NLP component.

That means the initial implementation will likely avoid direct equivalents of:

- spaCy pipelines
- GLiNER / GLiREL parity
- Python-specific framework integrations
- Python-only operational tooling

Instead, the .NET version will prioritize:

- clean architecture
- strong persistence model
- excellent Agent Framework integration
- .NET-native extensibility
- clear interfaces for future extraction backends

## Multi-user & multi-store isolation

Long-term knowledge is **owner-scoped** so one user cannot recall another user's stored memories — while keeping an optional **shared/global** tier. The model is additive and backward-compatible: a `null` owner means shared (today's behavior), so existing deployments are unaffected.

- **`owner_id` (nullable) on every long-term node and edge** — `Fact`, `Entity`, `Preference`, `Relationship` (RELATED_TO), and `ReasoningTrace`. `NULL` = shared/global; a concrete id = private to that user. `Fact` additionally MERGEs on an `owner_key` sentinel so the same triple from two users never collapses.
- **`MemoryScope { OwnerId, IncludeShared }`** drives reads. Recall returns `owner_id == me OR (IncludeShared AND owner_id IS NULL)`. Because Neo4j vector indexes can't pre-filter a property, scoped vector/graph recall **over-fetches then filters then limits**, so the owner's matches are never starved by foreign rows.
- **Scoped everywhere a leak could occur:** vector recall (facts/entities/preferences), non-vector lookups (by subject/name/category), GraphRAG retrieval (vector/fulltext/hybrid/graph), reasoning-trace search, relationship reads, and temporal (`AsOf`) recall — each verified with Neo4j integration tests.
- **Identity flows from the agent surface:** the MAF providers read `user_id` (and `application_id`) from the `AgentSession` state bag (`session.WithMemoryIdentity(userId: …)`); MCP and Semantic Kernel tools accept an optional `userId`. Owner indexes (`fact/entity/preference/trace/rel_owner_idx`) back the filters; non-backfilling migrations (`0002`, `0003`) bring existing databases to parity losslessly.
- **Optional store tier (R1b):** an `ApplicationId` above the owner routes to its own Neo4j database (`MemoryStorageStrategy.DatabasePerApplication`, requires Enterprise/AuraDB) or stays on the shared database (default, Community-compatible). `IMemoryStoreContext` is `AsyncLocal`-backed, so per-request routing is concurrency-safe.

> The LLM-invokable memory tools (built from `IMemoryQueryFacade`) scope via an ambient `IMemoryOwnerContext` — set `IWritableMemoryOwnerContext.UserId` for the agent run (the tools can't accept a trusted user id from the model). Status: the owner-scope core and all leak-path closures are implemented and integration-tested (see `docs/Memory_Review_and_Implementation_Plan.md`). Deliberately deferred/limited items (entity-resolution sharing semantics; session-clear owner-scoping) are tracked there.

## Project status

R1/R1b multi-user isolation (above) plus 6 implementation phases complete, plus a gap-closure sprint (Waves A–C) bringing Python parity to ~99%. Foundation memory engine fully implemented with Neo4j persistence, extraction pipeline with LLM and Azure Language backends, Microsoft Agent Framework adapter, Semantic Kernel adapter, GraphRAG blended retrieval (built into the Neo4j package), OpenTelemetry observability, geocoding and entity enrichment services, and MCP Server with 25 tools, 6 resources, and 3 prompts — all ready for deployment. All timestamps use native Neo4j `datetime()` storage. Session ID generation supports 3 strategies (PerConversation, PerDay, PersistentPerUser). MetadataFilterBuilder provides 5 operators ($eq, $ne, $contains, $in, $exists).

The solution ships these packages:

| Package | Phase | Purpose |
|---------|-------|---------|
| `AgentMemory.Abstractions` | 1 | Domain models, service/repository interfaces, configuration options — zero external dependencies |
| `AgentMemory.Core` | 1 | Memory services, extraction pipeline, context assembly, stubs |
| `AgentMemory.Neo4j` | 1 | Neo4j repository implementations, Cypher queries, schema bootstrap, GraphRAG retrieval |
| `AgentMemory.Extraction.Llm` | 2 | LLM-driven entity/fact/preference/relationship extractors (Microsoft.Extensions.AI) |
| `AgentMemory.Extraction.AzureLanguage` | 5 | Azure Text Analytics extractors — NER, key phrases, PII |
| `AgentMemory.AgentFramework` | 3 | Microsoft Agent Framework adapter — context provider, chat store, memory tools, trace recorder |
| `AgentMemory.SemanticKernel` | 6 | Semantic Kernel adapter — memory plugin with native SK integration |
| `AgentMemory.Enrichment` | 5 | Geocoding (Nominatim) + entity enrichment (Wikimedia) with caching and rate limiting |
| `AgentMemory.Observability` | 4 | OpenTelemetry decorators — tracing spans and metrics for all memory operations |
| `AgentMemory.McpServer` | 6 | MCP Server — 25 tools, 6 resources, 3 prompts (search, context, store, entities, facts, preferences, reasoning traces, observations, graph query, export, extract) |
| `AgentMemory.Analytics` | Opt-in | Optional Neo4j GDS analytics — PageRank (memory importance) + Louvain community detection, owner-scoped, graceful no-op without the GDS plugin. Not in the meta-package. |
| `AgentMemory` | Release | Meta-package bundling core + Neo4j + Abstractions for convenient dependencies |

Extensively tested with unit and integration tests covering all packages. ~99% functional parity with the Python reference implementation. Published to NuGet as `0.1.0-preview.3`.

The goal is to produce a robust, testable, production-oriented .NET implementation that is easy for .NET teams to adopt and extend.

## Documentation

Full docs live in [`docs/`](docs/) — start with the **[Roadmap & Status](docs/ROADMAP.md)** (overarching plan, what's shipped, what's pending) or **[Getting Started](docs/getting-started.md)**. See the [docs index](docs/README.md) for the architecture/design/schema references, the completed [review records](docs/reviews/), and the [archive](docs/archive/).

## Contributing

Contributions, design feedback, and implementation ideas are welcome.

See [CONTRIBUTING.md](CONTRIBUTING.md) for build, test, and contribution guidelines.

## Getting Started

### Prerequisites
- .NET 9 SDK or later
- Neo4j 5.x (local, cloud, or containerized)
- For Semantic Kernel integration: Semantic Kernel v1.x
- For Azure Language integration: Azure Text Analytics service

### Quick Start

1. **Install the core package** (or meta-package):
   ```bash
   dotnet add package AgentMemory
   ```

2. **Configure memory services**. `AddNeo4jAgentMemory` takes a `MemoryOptions` delegate, a
   `Neo4jOptions` delegate, and an optional `LlmExtractionOptions` delegate — Neo4j connection
   settings live on the **second** delegate:
   ```csharp
   var provider = new ServiceCollection()
       .AddNeo4jAgentMemory(
           memory => { /* MemoryOptions, e.g. memory.EnableGraphRag = true; */ },
           neo4j  => {
               neo4j.Uri      = "bolt://localhost:7687";
               neo4j.Username = "neo4j";
               neo4j.Password = "password";
           },
           llm => { llm.ModelId = "gpt-4o-mini"; })   // optional
       // Opt-in capabilities (chain as needed):
       .WithObservability()                            // OpenTelemetry metrics + instrumentation
       // .WithEnrichment()                            // Nominatim geocoding + Wikimedia/Diffbot
       // .WithAzureLanguageExtraction(azure => { azure.Endpoint = "..."; azure.ApiKey = "..."; })
       .BuildServiceProvider();
   ```

3. **Initialize Neo4j schema**:
   ```csharp
   var bootstrapper = provider.GetRequiredService<ISchemaBootstrapper>();
   await bootstrapper.BootstrapAsync();
   ```

4. **Use in your agent**:
   ```csharp
   var memory = provider.GetRequiredService<IMemoryService>();
   await memory.AddMessageAsync(sessionId, conversationId, "user", "Hello!");
   var recall = await memory.RecallAsync(new RecallRequest { SessionId = sessionId, Query = "Hello" });
   ```

For detailed examples, see the `samples/` directory.

## Credits

This project is inspired by the ideas and architecture of:

- [`neo4j-labs/agent-memory`](https://github.com/neo4j-labs/agent-memory)
- [`neo4j-labs/neo4j-maf-provider`](https://github.com/neo4j-labs/neo4j-maf-provider)

Please refer to those repositories for the original Python implementation and the existing Neo4j GraphRAG integration for Microsoft Agent Framework.

## License

This project is licensed under the [Apache License 2.0](LICENSE).

If code or substantial derived work is incorporated from upstream repositories, their respective license terms and notice requirements must be followed.
