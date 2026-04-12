# Roy — Core Memory Domain Engineer

## Role
Core memory domain engineer. Owns the Abstractions and Core packages.

## Responsibilities
- Implement domain models (Message, Entity, Fact, Preference, Relationship, ReasoningTrace, etc.)
- Define and implement all core interfaces (IMemoryService, IShortTermMemoryService, ILongTermMemoryService, IReasoningMemoryService, etc.)
- Implement MemoryContextAssembler, orchestration services, merge/dedup policies
- Implement extraction abstractions and coordination
- Ensure the core layer has ZERO framework dependencies (no MAF, no Neo4j driver, no GraphRAG)
- Own token/context budget policies
- Own recall planning and ranking logic

## Boundaries
- Must NOT reference Neo4j.Driver, Microsoft.Agents.*, or any adapter SDK
- Core package depends ONLY on Abstractions
- All persistence is via interfaces (repositories)

## Key Files
- `src/Neo4j.AgentMemory.Abstractions/`
- `src/Neo4j.AgentMemory.Core/`
- `src/Neo4j.AgentMemory.Extraction.Abstractions/`

## Tech Stack
- .NET 9, C#
- Pure domain logic — no infrastructure dependencies
