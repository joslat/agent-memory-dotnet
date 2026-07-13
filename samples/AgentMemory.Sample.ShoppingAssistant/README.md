# Shopping Assistant — AgentMemory + Microsoft Agent Framework

The **.NET reimplementation of the official Neo4j Agent Memory "retail assistant"** example for the Microsoft
Agent Framework
([`microsoft_agent_retail_assistant`](https://github.com/neo4j-labs/agent-memory/tree/main/examples/microsoft_agent_retail_assistant),
referenced from the [Learn integration page](https://learn.microsoft.com/en-us/agent-framework/integrations/neo4j-memory)).
The Python original is a full-stack web app; this is a focused console sample that captures its
essence: a shopping assistant that **learns a customer's preferences** and **recommends products via
graph traversal**, backed by durable Neo4j memory.

## What it demonstrates

- **The canonical MAF wiring** — a `ChatClientAgent` with:
  - `Neo4jMemoryContextProvider` (an `AIContextProvider`) — passive, bidirectional long-term memory,
  - `MemoryToolFactory.CreateAIFunctions()` — the memory tools (`create_memory_tools` equivalent),
  - **`ProductCatalog.CreateAIFunctions()`** — retail tools (search / recommend / related / inventory)
    over a Neo4j product graph (`get_product_tools` equivalent),
  - a retail system prompt.
- **Preference learning** — a stated preference (brand Nike, budget ~$150) is persisted and then
  drives recommendations.
- **Graph-based recommendations & related products** — `:Product` nodes linked to `:ProductCategory`
  / `:ProductBrand` nodes; "related" and "recommended" come from graph traversals.
- **Durable cross-session recall** — a brand-new session for the same shopper still knows her
  preferences, because memory lives in Neo4j.

## Run it

```bash
# A local Neo4j (the sample bootstraps the schema and seeds 10 products)
docker run -d --name neo4j -p 7474:7474 -p 7687:7687 -e NEO4J_AUTH=neo4j/password neo4j:5.26

dotnet run --project samples/AgentMemory.Sample.ShoppingAssistant
# Overrides: Neo4j__Uri / Neo4j__Username / Neo4j__Password
```

## Offline vs. a real model

Like every sample here, it runs **offline** with a mock `IChatClient` (no API key). A mock model does
not drive tool-calls, so this demo **scripts** the shopping journey — it calls the product tools and
memory APIs directly so you can see the graph and recall working.

With a **real** `IChatClient` (OpenAI / Azure OpenAI, via `Microsoft.Extensions.AI`) the agent invokes
those same memory + product tools **itself**, conversationally. **The memory wiring is identical** —
you only swap the `IChatClient` (and `IEmbeddingGenerator`) DI registrations in `Program.cs`; nothing
about the context provider, tools, or product graph changes.

## Files

| File | Purpose |
|---|---|
| `Program.cs` | Host wiring, the agent, and the scripted retail journey. |
| `ProductCatalog.cs` | The sample product graph (seed) and the retail tools (Cypher via `INeo4jTransactionRunner`), exposed as `AIFunction`s. |

## See also

- [Using AgentMemory with the Microsoft Agent Framework](../../docs/agent-framework.md) — the full guide.
- [`AgentMemory.Sample.AgentWithMemory`](../AgentMemory.Sample.AgentWithMemory/) — the minimal MAF golden path.
- [Neo4j Memory Provider for Agent Framework (Learn)](https://learn.microsoft.com/en-us/agent-framework/integrations/neo4j-memory) — the official (Python) provider this ports.
