# Deckard — History

## Project Context
- **Project:** Agent Memory for .NET — native .NET Neo4j Memory Provider for AI agents
- **User:** Jose Luis Latorre Millas
- **Stack:** .NET 9, C#, Neo4j, Microsoft Agent Framework, GraphRAG
- **Architecture:** Layered ports-and-adapters (Abstractions → Core → Neo4j → Adapters)
- **Spec:** Agent-Memory-for-DotNet-Specification.md
- **Plan:** Agent-memory-for-dotnet-implementation-plan.md

## Learnings

### 2025-01-28 — Phase 1 Onboarding Analysis

**Key Architecture Decisions Identified:**
1. Framework-agnostic core: Memory engine must not depend on MAF types
2. Adapters on top: MAF and GraphRAG are separate adapter layers
3. GraphRAG is required but separate: Not the memory provider itself, but required for interoperability
4. No Python runtime: Use LLM-based structured extraction first
5. Ports-and-adapters: Strict layering with dependency inversion
6. Neo4j persistence: Direct driver usage, no ORM
7. MCP excluded from Phase 1: External access layer built later

**Package Boundaries (Phase 1):**
- `Neo4j.AgentMemory.Abstractions` — domain contracts only, zero external dependencies
- `Neo4j.AgentMemory.Core` — orchestration and domain logic, depends only on Abstractions
- `Neo4j.AgentMemory.Neo4j` — repositories and Cypher, depends on Abstractions + Core + Neo4j.Driver

**Critical Patterns:**
- Testcontainers for Neo4j integration tests
- Contracts-first design to enforce dependency direction
- Stubbing strategy: `IEmbeddingProvider` and `IExtractionService` stubbed in Phase 1
- Schema bootstrap and migration runner as first-class concerns
- Three distinct memory layers with separate repositories

**Risks Identified:**
1. Embedding provider integration complexity (deferred to Phase 2)
2. Extraction pipeline complexity (deferred to Phase 2)
3. Graph schema evolution and migration strategy
4. Context assembly token budget enforcement
5. Transaction boundary definition across memory operations

**Dependencies:**
- Neo4j.Driver (official .NET driver)
- xUnit, FluentAssertions (testing)
- Testcontainers for .NET
- Docker + Neo4j image

**Bootstrap Order:**
1. Abstractions → contracts
2. Neo4j infrastructure → driver factory, schema installer, transaction runner
3. Short-term → messages, conversations
4. Long-term → entities, facts, preferences, relationships
5. Reasoning → traces, steps, tool calls
6. Context assembler → memory recall and assembly

**Phase 1 Exit Criteria:**
- All repositories implemented with Neo4j persistence
- All services unit tested
- All repositories integration tested with real Neo4j
- Context assembler functional with configurable budgets
- No MAF or GraphRAG dependencies in Core or Abstractions
- Schema bootstrap creates all constraints and indexes
- Docker Compose harness functional
