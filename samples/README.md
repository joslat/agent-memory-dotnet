# AgentMemory for .NET — Samples

Runnable samples for **AgentMemory for .NET**, a Neo4j-backed agent-memory library for the
[Microsoft Agent Framework](https://learn.microsoft.com/en-us/agent-framework/) (MAF) and
Semantic Kernel.

> **Context:** Microsoft's official **Neo4j Memory Provider for Agent Framework**
> ([`neo4j-agent-memory`](https://github.com/neo4j-labs/agent-memory),
> [Learn docs](https://learn.microsoft.com/en-us/agent-framework/integrations/neo4j-memory)) is
> currently **Python-only** — the Learn page states it is *"not yet available for C#."* This library
> is the **.NET equivalent**: the same three-layer memory model (short-term conversation, long-term
> entities/preferences/facts, reasoning traces) backed by a Neo4j knowledge graph, surfaced to MAF
> through an `AIContextProvider` and a set of memory tools.

> **New to the MAF integration?** Start with the guide:
> [Using AgentMemory with the Microsoft Agent Framework](../docs/agent-framework.md) — how the
> `AIContextProvider` lifecycle works, the memory tools, identity/scoping, and the design rationale.

## API mapping (official Python ↔ this library)

The samples follow the canonical MAF memory pattern shown in the official
[`04_memory`](https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/01-get-started/04_memory)
sample and the Python [retail assistant](https://github.com/neo4j-labs/agent-memory/tree/main/examples/microsoft_agent_retail_assistant):
a memory **context provider** (injects memory before each run, persists after) plus optional memory
**tools** the agent can call.

| Official Python `neo4j-agent-memory` | This library (.NET) |
| --- | --- |
| `MemoryClient(MemorySettings(...))` | `services.AddNeo4jAgentMemory(...)` + `AddAgentMemoryCore(...)` |
| `Neo4jMicrosoftMemory.from_memory_client(...)` | `services.AddAgentMemoryFramework(...)` |
| `memory.context_provider` (an `AIContextProvider`) | `Neo4jMemoryContextProvider` (an `AIContextProvider`) |
| `create_memory_tools(memory)` | `MemoryToolFactory.CreateAIFunctions()` |
| `agent = Agent(client=..., context_providers=[memory.context_provider], tools=...)` | `chatClient.AsAIAgent(new ChatClientAgentOptions { AIContextProviders = [provider], ChatOptions = { Tools = [...] } })` |
| `session = agent.create_session()` / `agent.run(text, session=session)` | `await agent.CreateSessionAsync()` / `await agent.RunAsync(text, session)` |

## Samples

| Sample | Demonstrates |
| --- | --- |
| **AgentWithMemory** | The flagship golden path — the .NET equivalent of the official [`04_memory`](https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/01-get-started/04_memory) / [`AgentWithMemory`](https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/02-agents/AgentWithMemory) sample, backed by **durable Neo4j memory**: `Neo4jMemoryContextProvider` + memory tools, explicit `WithMemoryIdentity(...)` owner/application/session scope, multi-turn session, **session serialize/restore** (`SerializeSessionAsync`/`DeserializeSessionAsync`), and **durable cross-session recall**. |
| **ShoppingAssistant** | The **.NET reimplementation of the official Neo4j retail-assistant example** — a shopping assistant that learns preferences and recommends products via graph traversal: `Neo4jMemoryContextProvider` + memory tools + **custom product tools** over a Neo4j product graph, a retail prompt, and durable cross-session recall. Runs offline (scripted journey). |
| **RealAgent** | A real `ChatClientAgent` with `Neo4jMemoryContextProvider` (long-term memory) **and** the memory tools, multi-turn `AgentSession`, and native MAF `UseOpenTelemetry()`. |
| **MemoryToolsAgent** | The memory tools (`MemoryToolFactory.CreateAIFunctions()`, the `create_memory_tools` equivalent): registered on an agent and invoked directly against Neo4j. |
| **ChatHistoryProvider** | `Neo4jChatHistoryProvider` wired via `ChatClientAgentOptions.ChatHistoryProvider` — per-session conversation history (distinct from long-term memory). |
| **BlendedAgent** | Blended persistent memory + GraphRAG retrieval, with OpenTelemetry. |
| **MinimalAgent** | The four MAF integration points (pre-run context, post-run persist, memory tools, reasoning traces) via the facade. |
| **McpHost** | Hosting the AgentMemory MCP server. |
| **AspireDemo** | A .NET Aspire AppHost orchestrating Neo4j + a scripted demo app. |

All agent samples use a **mock `IChatClient`** so they run offline (no API key). The golden path registers the mock through DI, so production hosts can replace it with a real `IChatClient` (OpenAI/Azure OpenAI/Foundry) and a real `IEmbeddingGenerator<string, Embedding<float>>` without changing the memory wiring. See `AgentMemory.Sample.AgentWithMemory/README.md` for the production identity/provider seams. Memory operations degrade gracefully when no live Neo4j is available.

## Running

```bash
# Optional: a local Neo4j (samples bootstrap the schema and fall back gracefully without one)
docker run -d --name neo4j -p 7474:7474 -p 7687:7687 -e NEO4J_AUTH=neo4j/password neo4j:5.26

# Defaults: bolt://localhost:7687, neo4j/password (override via Neo4j__Uri / Neo4j__Username / Neo4j__Password)
dotnet run --project samples/AgentMemory.Sample.AgentWithMemory
dotnet run --project samples/AgentMemory.Sample.RealAgent
dotnet run --project samples/AgentMemory.Sample.MemoryToolsAgent
dotnet run --project samples/AgentMemory.Sample.ChatHistoryProvider

# Build the standalone Aspire demo solution
dotnet build samples/samples.sln

# Run the Aspire AppHost demo
dotnet run --project samples/AspireDemo/AspireDemo.AppHost
```

## References

- [Microsoft Agent Framework docs](https://learn.microsoft.com/en-us/agent-framework/)
- [Neo4j Memory Provider for Agent Framework (Learn)](https://learn.microsoft.com/en-us/agent-framework/integrations/neo4j-memory)
- [Context Providers (Learn)](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/context-providers)
- [`neo4j-agent-memory` (neo4j-labs)](https://github.com/neo4j-labs/agent-memory) — the Python library this project mirrors
- [Official .NET memory sample (`04_memory`)](https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/01-get-started/04_memory)
