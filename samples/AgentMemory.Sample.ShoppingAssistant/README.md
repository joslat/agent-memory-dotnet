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

# Required — this sample calls a real Azure OpenAI model, there is no mock fallback
export AZURE_OPENAI_ENDPOINT=https://<resource>.openai.azure.com/
export AZURE_OPENAI_API_KEY=...
export AZURE_OPENAI_DEPLOYMENT=gpt-4o-mini                     # optional, this is the default
export AZURE_OPENAI_EMBEDDING_DEPLOYMENT=text-embedding-ada-002 # optional, this is the default

dotnet run --project samples/AgentMemory.Sample.ShoppingAssistant
# Overrides: Neo4j__Uri / Neo4j__Username / Neo4j__Password
```

## A real model drives the whole journey

This sample calls a **real** Azure OpenAI chat model and a **real** Azure OpenAI embedding model — no
mocks, no scripted tool calls. The agent decides for itself when to call `remember_preference`,
`search_products`, `get_recommendations`, and the rest of the memory + product tools, exactly as a
production integration would. The console prints every tool call it makes (product tools in gray,
memory tools in light blue) plus the `<recalled_memory>` context the `Neo4jMemoryContextProvider`
injects before each model call, so the whole recall → reasoning → tool-call loop is visible.

## Files

| File | Purpose |
|---|---|
| `Program.cs` | Host wiring, the agent, and the two-session retail conversation. |
| `ProductCatalog.cs` | The sample product graph (seed) and the retail tools (Cypher via `INeo4jTransactionRunner`), exposed as `AIFunction`s. |

## See also

- [Using AgentMemory with the Microsoft Agent Framework](../../docs/agent-framework.md) — the full guide.
- [`AgentMemory.Sample.AgentWithMemory`](../AgentMemory.Sample.AgentWithMemory/) — the minimal MAF golden path.
- [Neo4j Memory Provider for Agent Framework (Learn)](https://learn.microsoft.com/en-us/agent-framework/integrations/neo4j-memory) — the official (Python) provider this ports.
