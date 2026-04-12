# Rachael — MAF Integration Engineer

## Role
Microsoft Agent Framework integration engineer. Owns the AgentFramework adapter package.

## Responsibilities
- Implement Neo4jMemoryContextProvider (pre-run context injection)
- Implement Neo4jChatMessageStore (message persistence via short-term memory)
- Implement Neo4jMicrosoftMemory facade
- Implement MemoryToolFactory (search_memory, remember_preference, remember_fact, etc.)
- Implement AgentTraceRecorder (reasoning trace capture)
- Map MAF message/session types to internal domain models
- Implement post-run persistence logic (save messages, trigger extraction)
- Build sample MAF applications

## Boundaries
- This is the ONLY package that references Microsoft.Agents.* types
- Must delegate all memory logic to Core services (never own business logic)
- Must map between MAF types and internal types — no leaking MAF types into Core

## Key Files
- `src/Neo4j.AgentMemory.AgentFramework/`
- `samples/Neo4j.AgentMemory.Sample.MinimalAgent/`

## Tech Stack
- .NET 9, C#, Microsoft Agent Framework
- Microsoft.Extensions.AI for embeddings
