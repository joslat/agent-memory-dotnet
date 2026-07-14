# Agent Memory for .NET

![Agent Memory for .NET logo](https://raw.githubusercontent.com/joslat/agent-memory-dotnet/main/resources/logo.png)

> *A native .NET reimplementation of graph-native agent memory, inspired by Neo4j Agent Memory and
> verified against its compatibility kit.*

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
- **Fits the .NET you already run.** Multi-targets net8.0, net9.0, and net10.0, so adopting it doesn't
  mean adopting a new runtime first.
- **Faithful to its roots.** Verified against the upstream Python `neo4j-labs/agent-memory` project's own
  compatibility kit, so the .NET reimplementation isn't just inspired by the original — it's checked
  against it.

## Memory Governance

Any memory system that spans tenants, sessions, and time needs to answer these questions structurally —
not by convention. AgentMemory does:

| Question | How it's answered |
|---|---|
| **Who owns a memory?** | Every long-term record carries an `owner_id`/`MemoryScope`; `null` means shared/global — ownership is never ambiguous. |
| **Where did it come from?** | `source_message_ids` plus `EXTRACTED_FROM`/`EXTRACTED_BY` graph edges trace every fact, entity, and preference back to the message and extractor that produced it. |
| **What changed, and when?** | A bitemporal model separates valid-time (`valid_from`/`valid_until`) from transaction-time (`created_at`/`invalidated_at`); contradictions resolve via non-destructive `SUPERSEDED_BY`, and point-in-time recall can answer "what did we believe back then." |
| **Why was it recalled?** | Every long-term read is logged to a read/access audit trail (who, what, when, how often); ranking is driven by explicit, configurable recency/structural signals — not an opaque score. |
| **How is it invalidated or deleted?** | Long-term memory soft-invalidates by default (kept, recoverable); hard deletion is opt-in. Short-term session data can be cleared explicitly, always scoped to a single owner. |
| **Can tenants see one another's memory?** | No. Owner/store isolation is enforced in the repository, recall, GraphRAG, reasoning, and maintenance layers — not just at the API surface — with an optional database-per-application tier for physical separation. |
| **How do applications meet retention and privacy requirements?** | Scoped decay/pruning, non-destructive-by-default invalidation, the read-audit trail, and physical per-application isolation are the building blocks — wire them into whatever retention or privacy policy your application needs. |

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
- [Microsoft Agent Framework shopping-assistant sample PR](https://github.com/microsoft/agent-framework/pull/7096) — proposed upstream sample using the published AgentMemory packages
- [Schema Reference](https://github.com/joslat/agent-memory-dotnet/blob/main/docs/schema.md)
- [Specification](https://github.com/joslat/agent-memory-dotnet/blob/main/docs/specification.md)
- [Neo4j Memory Ecosystem](https://github.com/joslat/agent-memory-dotnet/blob/main/docs/neo4j-memory-ecosystem.md) - schema-parity/TCK compatibility tooling and the review process behind releases

## Relationship to Upstream

This project is inspired by [`neo4j-labs/agent-memory`](https://github.com/neo4j-labs/agent-memory) — the
Python reference implementation — and preserves compatible graph concepts from its
[schema](https://github.com/joslat/agent-memory-dotnet/blob/main/docs/schema.md) where useful. But it's a
**from-scratch .NET reimplementation, not a port**: it shares no code with the Python project. Most of
what it needs — the Neo4j driver integration, extraction pipeline, embeddings, DI wiring, and the
Microsoft Agent Framework/Semantic Kernel/MCP adapters — has no ready-made equivalent in the .NET
ecosystem to lean on, so it was built natively rather than bound or ported. It is not an official Neo4j
repository or product; schema and behavioral compatibility with the original are verified explicitly (see
[Neo4j Memory Ecosystem](https://github.com/joslat/agent-memory-dotnet/blob/main/docs/neo4j-memory-ecosystem.md)).

## Contributing

See [CONTRIBUTING.md](https://github.com/joslat/agent-memory-dotnet/blob/main/CONTRIBUTING.md) for build, test, and contribution guidance.

## License

This project is licensed under the [MIT License](https://github.com/joslat/agent-memory-dotnet/blob/main/LICENSE).
