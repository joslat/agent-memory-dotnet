# Agent Memory for .NET

> *At last — an agentic memory system for .NET, ported from one of the best: Neo4j Agent Memory.*

> Independent community project. Not affiliated with, endorsed by, or supported by Neo4j, Inc.

Give your .NET agents a memory that actually lasts. Agent Memory for .NET is a persistent,
**graph-native** memory engine for AI agents, backed by Neo4j — so what your agent learns in one
conversation is still there, structured and queryable, in the next one. Built for the Microsoft Agent
Framework, Semantic Kernel, direct .NET usage, and MCP clients.

## Why Agent Memory for .NET

- **Memory that survives the session.** Conversations, facts, preferences, and relationships persist as
  a real knowledge graph, not a scratchpad that evaporates when the process exits.
- **Three memory layers, not one.** Short-term conversation history, long-term facts/preferences/entities,
  and reasoning traces (steps, tool calls, prior executions) are all first-class citizens.
- **Graph-native, not just vectors.** Vector, fulltext, hybrid, and graph-traversal retrieval, plus
  optional GraphRAG context — because "similar text" and "connected knowledge" are different questions.
- **Multi-tenant from day one.** Owner and store isolation are enforced deep in the persistence layer,
  not bolted on at the API edge.
- **Time-aware.** Bitemporal recall and non-destructive decay mean memory can answer "what did we believe
  back then" as well as "what do we believe now."
- **Drops into the ecosystem you already use.** First-class adapters for the Microsoft Agent Framework,
  Semantic Kernel, and MCP, plus a direct .NET API for everything else.
- **Faithful to its roots.** Verified against the upstream Python `neo4j-labs/agent-memory` project's own
  compatibility kit, so the .NET port isn't just inspired by the original — it's checked against it.

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

For production semantic search, register a real `IEmbeddingGenerator<string, Embedding<float>>` from
`Microsoft.Extensions.AI`; the core stubs are safe defaults, not production embeddings.

## Documentation

Full documentation lives in [docs/](https://github.com/joslat/agent-memory-dotnet/tree/main/docs) — start with Getting Started:

- [Getting Started](https://github.com/joslat/agent-memory-dotnet/blob/main/docs/getting-started.md)
- [Architecture](https://github.com/joslat/agent-memory-dotnet/blob/main/docs/architecture.md)
- [Agent Framework Integration](https://github.com/joslat/agent-memory-dotnet/blob/main/docs/agent-framework.md)
- [Schema Reference](https://github.com/joslat/agent-memory-dotnet/blob/main/docs/schema.md)
- [Specification](https://github.com/joslat/agent-memory-dotnet/blob/main/docs/specification.md)
- [Neo4j Memory Ecosystem](https://github.com/joslat/agent-memory-dotnet/blob/main/docs/neo4j-memory-ecosystem.md) - schema-parity/TCK compatibility tooling and the review process behind releases

## Relationship to Upstream

This project is inspired by [`neo4j-labs/agent-memory`](https://github.com/neo4j-labs/agent-memory) and
preserves compatible graph concepts where useful, but it's implemented independently for .NET — not an
official Neo4j repository or product.

## Contributing

See [CONTRIBUTING.md](https://github.com/joslat/agent-memory-dotnet/blob/main/CONTRIBUTING.md) for build, test, and contribution guidance.

## License

This project is licensed under the [MIT License](https://github.com/joslat/agent-memory-dotnet/blob/main/LICENSE).
