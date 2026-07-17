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
| **ShoppingAssistant** | The **.NET reimplementation of the official Neo4j retail-assistant example** — a shopping assistant that learns preferences and recommends products via graph traversal: `Neo4jMemoryContextProvider` + memory tools + **custom product tools** over a Neo4j product graph, a retail prompt, and durable cross-session recall. The agent itself decides when to call the memory/product tools — nothing is scripted. |
| **NamsAgent** | The NAMS-backed sibling of AgentWithMemory — the same golden-path shape, but memory lives in the real [NAMS](https://memory.neo4jlabs.com) SaaS via `NamsMemoryContextProvider` (`AgentMemory.AgentFramework.Nams`) instead of a direct Neo4j connection: multi-turn session, session serialize/restore, and durable cross-session recall, all against the live service. |
| **RealAgent** | A real `ChatClientAgent` with `Neo4jMemoryContextProvider` (long-term memory) **and** the memory tools, multi-turn `AgentSession`, and native MAF `UseOpenTelemetry()`. |
| **MemoryToolsAgent** | The memory tools (`MemoryToolFactory.CreateAIFunctions()`, the `create_memory_tools` equivalent): registered on an agent and invoked directly against Neo4j. |
| **ChatHistoryProvider** | `Neo4jChatHistoryProvider` wired via `ChatClientAgentOptions.ChatHistoryProvider` — per-session conversation history (distinct from long-term memory). |
| **BlendedAgent** | Blended persistent memory + GraphRAG retrieval, with OpenTelemetry. |
| **MinimalAgent** | The four MAF integration points (pre-run context, post-run persist, memory tools, reasoning traces) via the facade. |
| **McpHost** | Hosting the AgentMemory MCP server. |
| **AspireDemo** | A .NET Aspire AppHost orchestrating Neo4j + a scripted demo app. |

**AgentWithMemory, RealAgent, MemoryToolsAgent, ChatHistoryProvider, ShoppingAssistant, and NamsAgent call
a REAL Azure OpenAI chat model — there is no mock `IChatClient` and no offline fallback.** Each fails fast
with setup instructions if credentials are missing. The model decides on its own when to call the memory
(and, for ShoppingAssistant, product) tools — nothing is scripted. Live tool calls and any memory the
context provider recalls are printed to the console (memory in light blue) via the shared
`AgentMemory.Samples.Shared` helper (`RealAzureOpenAI`/`MemoryTraceChatClient`/`SampleConsole`). See
`AgentMemory.Sample.AgentWithMemory/README.md` for the identity/provider seams. Memory operations degrade
gracefully when no live Neo4j is available. All of these except **NamsAgent** also use a REAL Azure OpenAI
**embedding** model — NAMS performs embedding/extraction server-side, so `NamsAgent` needs no local
embedding generator at all, and instead calls the **real, live NAMS SaaS** for memory. BlendedAgent,
MinimalAgent, and McpHost don't drive a chat model at all — they exercise the facade/tool layer directly —
but they too now use a **real** Azure OpenAI embedding model via the same shared `RealAzureOpenAI` helper;
no sample in this repo uses `StubEmbeddingGenerator` anymore.

## Running

```bash
# A local Neo4j (samples bootstrap the schema and fall back gracefully without one)
docker run -d --name neo4j -p 7474:7474 -p 7687:7687 -e NEO4J_AUTH=neo4j/password neo4j:5.26

# Required for every sample below (chat deployment only matters for the first five):
export AZURE_OPENAI_ENDPOINT=https://<resource>.openai.azure.com/
export AZURE_OPENAI_API_KEY=...
export AZURE_OPENAI_DEPLOYMENT=gpt-4o-mini                     # optional, this is the default
export AZURE_OPENAI_EMBEDDING_DEPLOYMENT=text-embedding-ada-002 # optional, this is the default

# Defaults: bolt://localhost:7687, neo4j/password (override via Neo4j__Uri / Neo4j__Username / Neo4j__Password)
dotnet run --project samples/AgentMemory.Sample.AgentWithMemory
dotnet run --project samples/AgentMemory.Sample.RealAgent
dotnet run --project samples/AgentMemory.Sample.MemoryToolsAgent
dotnet run --project samples/AgentMemory.Sample.ChatHistoryProvider
dotnet run --project samples/AgentMemory.Sample.ShoppingAssistant
dotnet run --project samples/AgentMemory.Sample.BlendedAgent
dotnet run --project samples/AgentMemory.Sample.MinimalAgent
dotnet run --project samples/AgentMemory.Sample.McpHost

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
