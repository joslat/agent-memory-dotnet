# Agent Memory for .NET

> Independent community project. Not affiliated with, endorsed by, or supported by Neo4j, Inc.

Persistent graph-native memory for AI agents in .NET, backed by Neo4j and built for Microsoft Agent Framework, Semantic Kernel, direct .NET usage, and MCP clients.

Start here:

- [CONTINUE-HERE.md](CONTINUE-HERE.md) - 30-second current-state landing.
- [docs/ROADMAP.md](docs/ROADMAP.md) - current release status, shipped capabilities, and next work.
- [docs/core/](docs/core/) - canonical project philosophy, requirements, design, specification, ADRs, and summaries.
- [docs/getting-started.md](docs/getting-started.md) - install and first-run guide.

## Status

`1.0.0` is the first stable release: the public API surface is locked under Semantic Versioning. The library is feature-complete — three-tier graph memory (short-term / long-term / reasoning), bitemporal recall with non-destructive decay, multi-tenant owner isolation, GraphRAG retrieval, consolidation/hygiene, and first-class adapters for the Microsoft Agent Framework, Semantic Kernel, MCP, and direct .NET use. It passes the upstream agent-memory compatibility kit (TCK) through the Gold tier.

The library ships from a warning-free Release build with a full unit + Semantic Kernel + live-Neo4j integration suite green on `main`.

## What It Provides

Agent Memory for .NET provides three memory layers:

- Short-term memory: conversations, messages, ordering, sessions, roles, timestamps, and embeddings.
- Long-term memory: entities, facts, preferences, relationships, provenance, owner scope, confidence, and temporal state.
- Reasoning memory: traces, steps, tool calls, tool aggregate nodes, task embeddings, and prior execution patterns.

It supports:

- Neo4j-backed graph persistence.
- Vector, fulltext, hybrid, and graph traversal retrieval.
- Optional GraphRAG context retrieval from `AgentMemory.Neo4j`.
- Microsoft Agent Framework context, chat-store, tools, and trace-recording integration.
- Semantic Kernel plugin integration.
- Model Context Protocol server surface.
- LLM and Azure Language extraction backends.
- OpenTelemetry observability, geocoding/entity enrichment, optional GDS analytics, schema bootstrap/migration, and CLI maintenance workflows.

## Package Topology

| Package | Purpose |
|---|---|
| `AgentMemory.Abstractions` | Domain models, service/repository interfaces, options, schema constants. |
| `AgentMemory.Core` | Memory services, context assembly, extraction pipeline, entity resolution, stubs. |
| `AgentMemory.Neo4j` | Neo4j repositories, Cypher queries, schema bootstrap, migrations, GraphRAG retrieval. |
| `AgentMemory.Extraction.Llm` | LLM-backed extractors through Microsoft.Extensions.AI. |
| `AgentMemory.Extraction.AzureLanguage` | Azure Language extraction support. |
| `AgentMemory.AgentFramework` | Microsoft Agent Framework adapter. |
| `AgentMemory.SemanticKernel` | Semantic Kernel adapter. |
| `AgentMemory.McpServer` | MCP tools/resources/prompts surface. |
| `AgentMemory.Observability` | Optional OpenTelemetry decorators and metrics. |
| `AgentMemory.Enrichment` | Optional geocoding and entity enrichment. |
| `AgentMemory.Analytics` | Optional Neo4j GDS PageRank/Louvain analytics. |
| `AgentMemory` | Convenience meta-package for the common stack. |

## Quick Start

```bash
dotnet add package AgentMemory
```

```csharp
using AgentMemory;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();

services.AddNeo4jAgentMemory(
    memory => { },
    neo4j =>
    {
        neo4j.Uri = "bolt://localhost:7687";
        neo4j.Username = "neo4j";
        neo4j.Password = "password";
    },
    configureLlm: null);

var provider = services.BuildServiceProvider();

var bootstrapper = provider.GetRequiredService<ISchemaBootstrapper>();
await bootstrapper.BootstrapAsync();

var memory = provider.GetRequiredService<IMemoryService>();
await memory.AddMessageAsync("session-01", "conversation-01", "user", "I prefer dark mode.");
```

For production semantic search, register a real `IEmbeddingGenerator<string, Embedding<float>>` from `Microsoft.Extensions.AI`; the core stubs are safe defaults, not production embeddings.

## Isolation Model

Memory is scoped as store -> owner -> session.

- Store: `ApplicationId`, default shared database, optional database per application for Enterprise/AuraDB.
- Owner: `owner_id`, `owner_key`, and `MemoryScope`; null owner means shared/global.
- Session: `session_id` and conversation IDs for run-local context.

Owner and store scope are enforced in repository, recall, GraphRAG, reasoning, maintenance, and temporal paths rather than only in adapters.

## Relationship to Upstream Projects

This project is inspired by `neo4j-labs/agent-memory` and preserves compatible graph concepts where useful. It is implemented independently in .NET and is not an official Neo4j repository or product.

GraphRAG retrieval is implemented inside `AgentMemory.Neo4j` and registered explicitly with `AddGraphRagAdapter(...)`; there is no separate current GraphRAG adapter package.

## Documentation

Full documentation lives in [docs/](docs/). The canonical core set is [docs/core/](docs/core/):

- [Philosophy](docs/core/philosophy.md)
- [Requirements and Constraints](docs/core/requirements-and-constraints.md)
- [Design Document](docs/core/design-document.md)
- [Specification](docs/core/specification.md)
- [ADRs](docs/core/adr/)
- [Summaries](docs/core/summaries.md)

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for build, test, and contribution guidance.

## License

This project is licensed under the [MIT License](LICENSE).
