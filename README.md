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
- **Future MCP exposure for external clients and tools**

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
A future optional .NET MCP server for exposing memory capabilities to external MCP clients.

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
5. **Optional MCP layer later**

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

Early design / implementation phase.

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
