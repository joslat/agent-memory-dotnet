# ADR 0006 - Three-Layer Memory Model

Status: Accepted

Date: 2026-07-09

## Context

Agent memory is not one kind of data. Conversation history, durable extracted knowledge, and reasoning/tool execution traces have different lifecycles and retrieval patterns. Collapsing them into one table or one node type would blur semantics and make retrieval less useful.

The Python reference also uses a multi-layer memory concept, and .NET consumers need clear APIs for each layer.

## Decision

Model memory as three layers:

1. Short-term memory: conversations and messages.
2. Long-term memory: entities, facts, preferences, and relationships.
3. Reasoning memory: traces, steps, tool calls, and tool aggregate nodes.

Expose services and repositories that preserve these distinctions while allowing `IMemoryService` to assemble a unified recall context.

## Consequences

Positive consequences:

- Each memory type has the right schema and lifecycle.
- Recall can blend memory sources without losing provenance.
- Reasoning traces can be searched separately from user facts and preferences.
- Extraction can transform messages into long-term memory without mutating the message history.

Tradeoffs:

- The schema is larger than a simple transcript store.
- Retrieval assembly has to budget and rank several context sources.
- Documentation must define which memory layer owns which behavior.

## Alternatives Considered

### Transcript-only memory

Rejected. It cannot represent durable facts, preferences, entity relationships, or reasoning traces in a structured way.

### Single generic Memory node

Rejected. It would reduce schema size but move type-specific behavior into metadata conventions and query branching.

### Separate databases per memory layer

Rejected. Cross-layer relationships, provenance, and recall benefit from a shared graph.

## Verification Anchors

- `src/AgentMemory.Abstractions/Domain/` contains models for conversations/messages, long-term memory, extraction, GraphRAG, and reasoning.
- `src/AgentMemory.Core/ServiceCollectionExtensions.cs` registers short-term, long-term, and reasoning services.
- `src/AgentMemory.Abstractions/Schema/SchemaConstants.cs` declares labels and relationships for all three layers.
- `docs/schema.md` documents the three-layer graph model.
