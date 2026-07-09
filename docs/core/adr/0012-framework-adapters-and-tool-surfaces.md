# ADR 0012 - Framework Adapters and Tool Surfaces

Status: Accepted

Date: 2026-07-09

## Context

The project is intended for real .NET agent applications, not only direct library calls. The .NET agent ecosystem includes Microsoft Agent Framework, Semantic Kernel, and MCP clients. Each surface has different conventions for context injection, tool calls, identity, and persistence lifecycle.

## Decision

Ship dedicated adapters for Microsoft Agent Framework, Semantic Kernel, and MCP, while keeping additional framework integrations demand-driven.

The accepted surfaces are:

- Direct API/DI usage through `IMemoryService` and role interfaces.
- MAF adapter for context providers, chat/history persistence, memory tools, and trace recording.
- SK adapter for plugin-style memory access.
- MCP server for external clients via tools/resources/prompts.

Additional adapters such as AutoGen.NET, LangChain.NET, or Semantic Router may be added later if demand justifies them.

## Consequences

Positive consequences:

- The project supports the major .NET agent usage paths.
- Framework-specific identity and lifecycle mapping live close to the framework adapter.
- The core memory model remains shared across adapters.
- Future adapters can reuse the same facade and query surface.

Tradeoffs:

- Adapter packages require ongoing tracking of framework API changes.
- Documentation must state which adapter does what.
- Extra ecosystem breadth is deferred rather than assumed complete.

## Alternatives Considered

### Direct API only

Rejected. Agent-framework integration is a primary adoption path.

### Implement every Python ecosystem adapter equivalent

Rejected. Python-specific integrations are not automatically relevant to .NET users.

### Put all adapters into the meta-package

Rejected. Framework dependencies should stay optional.

## Verification Anchors

- `src/AgentMemory.AgentFramework/` implements the MAF surface.
- `src/AgentMemory.SemanticKernel/` implements the SK surface.
- `src/AgentMemory.McpServer/` implements the MCP surface.
- `docs/getting-started.md` documents MAF and SK usage plus MCP samples.
- `docs/Improvement-Ideas-Backlog.md` keeps additional framework integrations as deferred ideas.
