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

- **Microsoft Agent Framework**
- **Neo4j-backed persistent memory**
- **Graph-native retrieval and memory recall**
- **GraphRAG interoperability**
- **MCP Server for external clients and tools** (21 tools, 6 resources, 3 prompts)

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

The solution is intended to be built in layers:

### 1. Core memory engine
Framework-agnostic contracts and services for:

- message storage
- memory extraction orchestration
- entity / fact / preference storage
- reasoning traces
- context assembly

### 2. Neo4j persistence layer
Neo4j-backed implementations for:

- graph persistence
- vector/fulltext/hybrid search
- relationship traversal
- memory retrieval

### 3. Microsoft Agent Framework adapter
A dedicated integration layer on top of the core for:

- context injection before agent runs
- persistence after agent runs
- Neo4j-backed chat/message store
- memory tools for agent usage

### 4. GraphRAG interoperability
A separate adapter that composes with the existing .NET Neo4j GraphRAG provider for:

- vector retrieval
- hybrid retrieval
- graph-enriched context retrieval
- combined memory + GraphRAG scenarios

### 5. MCP layer
A .NET MCP server exposing 21 memory tools, 6 resources, and 3 prompts to external MCP clients (Claude Desktop, etc.) via stdio and HTTP transports.

## Planned capabilities

### Short-term memory
- session-scoped conversation history
- recent message recall
- semantic message search
- conversation summaries

### Long-term memory
- entities
- preferences
- facts
- relationships
- optional geospatial and enrichment support

### Reasoning memory
- reasoning traces
- steps
- tool usage
- outcomes
- similar-task retrieval

### Agent Framework integration
- pre-run context assembly
- post-run memory persistence
- memory tools
- Neo4j-backed message store

### GraphRAG interoperability
- use the existing Neo4j GraphRAG context provider from .NET
- keep GraphRAG as a separate retrieval component
- allow blended retrieval from persistent memory and GraphRAG sources

## Initial scope

The first implementation focus is:

1. **Native .NET Neo4j memory core**
2. **Microsoft Agent Framework adapter**
3. **GraphRAG adapter using the existing .NET provider**
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

## Project status

All 6 implementation phases complete, plus a gap-closure sprint (Waves A–C) bringing Python parity to ~99%. Foundation memory engine fully implemented with Neo4j persistence, extraction pipeline with LLM and Azure Language backends, Microsoft Agent Framework adapter, GraphRAG blended retrieval adapter, OpenTelemetry observability, geocoding and entity enrichment services, and MCP Server with 21 tools, 6 resources, and 3 prompts — all ready for deployment. All timestamps use native Neo4j `datetime()` storage. Session ID generation supports 3 strategies (PerConversation, PerDay, PersistentPerUser). MetadataFilterBuilder provides 5 operators ($eq, $ne, $contains, $in, $exists).

The solution ships 10 packages:

| Package | Phase | Purpose |
|---------|-------|---------|
| `Neo4j.AgentMemory.Abstractions` | 1 | Domain models, service/repository interfaces, configuration options — zero external dependencies |
| `Neo4j.AgentMemory.Core` | 1 | Memory services, extraction pipeline, context assembly, stubs |
| `Neo4j.AgentMemory.Neo4j` | 1 | Neo4j repository implementations, Cypher queries, schema bootstrap |
| `Neo4j.AgentMemory.Extraction.Llm` | 2 | LLM-driven entity/fact/preference/relationship extractors (Microsoft.Extensions.AI) |
| `Neo4j.AgentMemory.Extraction.AzureLanguage` | 5 | Azure Text Analytics extractors — NER, key phrases, PII |
| `Neo4j.AgentMemory.AgentFramework` | 3 | Microsoft Agent Framework adapter — facade, context provider, chat store, memory tools, trace recorder |
| `Neo4j.AgentMemory.GraphRagAdapter` | 4 | GraphRAG adapter — `IGraphRagContextSource` via Neo4j vector/fulltext/hybrid/graph retrieval |
| `Neo4j.AgentMemory.Enrichment` | 5 | Geocoding (Nominatim) + entity enrichment (Wikimedia) with caching and rate limiting |
| `Neo4j.AgentMemory.Observability` | 4 | OpenTelemetry decorators — tracing spans and metrics for all memory + GraphRAG operations |
| `Neo4j.AgentMemory.McpServer` | 6 | MCP Server — 21 tools, 6 resources, 3 prompts (search, context, store, entities, facts, preferences, reasoning traces, observations, graph query, export, extract) via Model Context Protocol |

**1058 unit tests passing, 0 failures.** (~99% functional parity with Python reference)

The goal is to produce a robust, testable, production-oriented .NET implementation that is easy for .NET teams to adopt and extend.

## Contributing

Contributions, design feedback, and implementation ideas are welcome.

Contribution guidelines, coding standards, and package structure will be added as the repository is initialized.

## Credits

This project is inspired by the ideas and architecture of:

- [`neo4j-labs/agent-memory`](https://github.com/neo4j-labs/agent-memory)
- [`neo4j-labs/neo4j-maf-provider`](https://github.com/neo4j-labs/neo4j-maf-provider)

Please refer to those repositories for the original Python implementation and the existing Neo4j GraphRAG integration for Microsoft Agent Framework.

## License

License to be defined for this repository.

If code or substantial derived work is incorporated from upstream repositories, their respective license terms and notice requirements must be followed.
