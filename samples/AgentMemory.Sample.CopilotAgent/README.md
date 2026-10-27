# AgentMemory — CopilotAgent Sample

Durable memory for an agent whose model loop you do not own: the Microsoft Agent Framework's `GitHubCopilotAgent`
(package `Microsoft.Agents.AI.GitHub.Copilot`) drives the GitHub Copilot CLI, which runs its own tool loop in another
process. The model is **BYOK**: the Copilot runtime calls your OpenAI-compatible provider, so no Copilot subscription
is used.

## What it shows

| Step | How | Why |
|---|---|---|
| Memory around each turn | `copilot.AsAIAgent(config).AsBuilder().UseAIContextProviders(new MemoryMessages(provider))` | `GitHubCopilotAgent` is an `AIAgent`, not a `ChatClientAgent`, so it takes only message-level context (`MessageAIContextProvider`). `MemoryMessages` adapts `Neo4jMemoryContextProvider`: the same recall before the turn, the same persistence and extraction after it. |
| Memory inside Copilot's loop | `SessionHooks.OnUserPromptSubmitted` returns the recalled memory as `AdditionalContext` | The hook fires for each prompt the session takes. It runs on the SDK's callback, outside the agent's async flow, so it opens the owner scope itself. |
| Durable recall | A second, new Copilot session | A new session has no history of its own; it knows the user's preference only from memory in Neo4j. |
| Owner isolation | `StrictMultiTenant`, `WithMemoryOwnerScoping` | As in every sample. |
| No tools | `OnPermissionRequest` refuses everything | Copilot's file, shell and web tools never run on your machine. |

The sample prints how often each path ran. On a run of three turns: three recalls and three stores around the turns,
and three prompt hooks inside the loop. With no tool calls, Copilot's loop takes one prompt per turn, so both paths
recall once per turn. Use one or the other for recall; keep `MemoryMessages` for persistence.

What does not reach this agent: instructions and tools that a context provider adds. Memory tools have to be offered
another way, for example as an MCP server in `SessionConfig.McpServers`.

## Prerequisites

- .NET 10 SDK.
- The GitHub Copilot CLI on `PATH` (`copilot --version`), or `COPILOT_CLI_PATH`.
- One inference provider, auto-detected as in every sample; `BITDEER_API_KEY` is the shortest
  (see `docs/configuration/inference-providers.md`).
- Neo4j: `Neo4j__Uri`, `Neo4j__Username`, `Neo4j__Password` (defaults `bolt://localhost:7687`, `neo4j`, `password`).

## Run

```bash
dotnet run --project samples/AgentMemory.Sample.CopilotAgent
```

An integration test runs it end to end when asked: `COPILOT_SAMPLE_LIVE=1 dotnet test tests/AgentMemory.Tests.Integration
--filter CopilotSampleIntegrationTests` (it spends a little on the model).
