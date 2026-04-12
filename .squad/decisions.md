# Squad Decisions

## Active Decisions

### D1: Package Structure (Deckard, 2025-01-28)

**Status:** Approved  
**Scope:** Phase 1 (Core Memory Engine)

We commit to the following package structure for Phase 1:

```
src/
  Neo4j.AgentMemory.Abstractions/
  Neo4j.AgentMemory.Core/
  Neo4j.AgentMemory.Neo4j/

tests/
  Neo4j.AgentMemory.Tests.Unit/
  Neo4j.AgentMemory.Tests.Integration/

deploy/
  docker-compose.dev.yml
```

**Rationale:** Establishes clear layering with abstractions as contracts, core as business logic, and Neo4j as persistence adapter. Testcontainers support enables real integration testing.

---

### D2: Dependency Direction (Deckard, 2025-01-28)

**Status:** Approved  
**Scope:** Phase 1 (Core Memory Engine)

Strictly enforce:
- Core → Abstractions only
- Neo4j → Abstractions + Core
- No reverse dependencies
- No MAF/GraphRAG types in Phase 1

**Rationale:** Maintains clean architecture boundaries. Contracts-first design ensures dependency direction is correct from day one.

---

### D3: Test Harness (Deckard, 2025-01-28)

**Status:** Approved  
**Scope:** Phase 1 (Core Memory Engine)

Use Testcontainers for .NET with Neo4j for all integration tests. Every repository and service must have integration test coverage before Phase 1 completion.

**Rationale:** Ensures real Neo4j integration testing without manual infrastructure setup. All Phase 1 tests must run in CI with Testcontainers.

---

### D4: Bootstrap Order (Deckard, 2025-01-28)

**Status:** Approved  
**Scope:** Phase 1 (Core Memory Engine)

Build in this sequence:
1. Abstractions (contracts first)
2. Neo4j infrastructure (driver, schema, transactions)
3. Short-term memory (messages, conversations)
4. Long-term memory (entities, facts, preferences, relationships)
5. Reasoning memory (traces, steps, tool calls)
6. Context assembler

**Rationale:** Enables parallel work after abstractions stabilize. Lower layers unblock higher layers.

---

### D5: Stubbing Strategy (Deckard, 2025-01-28)

**Status:** Approved  
**Scope:** Phase 1 (Core Memory Engine)

Stub `IEmbeddingProvider` and `IExtractionService` in Phase 1. Implement in Phase 2.

**Rationale:** Extraction workflows testable with stub implementations until Phase 2. Allows memory core to progress independently.

---

### D6: Domain Models and Interface Design (Roy, 2025-01-27)

**Status:** Approved  
**Scope:** Abstractions Package Foundation

#### 6.1 Domain Models as C# Records

All domain models use C# records for immutability, value semantics, and concise init-only property syntax.

**Rationale:** Records provide structural equality, prevent accidental mutation, improve readability, and fit naturally for data transfer between layers.

---

#### 6.2 Repository Pattern with Consistent Naming

All repositories follow standard naming:
- `UpsertAsync` for add-or-update operations
- `GetByXAsync` for lookups
- `SearchByVectorAsync` for semantic searches
- Return tuples `(Entity, double Score)` for scored results

**Rationale:** Consistency reduces cognitive load. Tuple returns avoid extra wrapper types. Clear semantics and testability.

---

#### 6.3 Layered Service Interfaces

- **IMemoryService** — facade for high-level operations
- **IShortTermMemoryService** — conversation and message operations
- **ILongTermMemoryService** — entities, facts, preferences, relationships
- **IReasoningMemoryService** — traces, steps, tool calls
- **IMemoryContextAssembler** — orchestrates recall across layers
- **IMemoryExtractionPipeline** — coordinates extraction
- Individual extractors: IEntityExtractor, IFactExtractor, etc.

**Rationale:** Clear separation of concerns. Each layer independently testable. Facade simplifies common operations. Pipeline pattern allows composition.

---

#### 6.4 Zero Framework Dependencies in Abstractions

Abstractions package has NO dependencies on:
- Neo4j.Driver
- Microsoft.Agents.*
- GraphRAG SDKs
- Any infrastructure concerns

**Rationale:** Maintains clean architecture boundaries. Core logic remains portable. Adapters evolve independently. Easier to test in isolation.

---

#### 6.5 Provenance and Metadata Throughout

All extracted long-term memory includes:
- `SourceMessageIds` for traceability
- `Metadata` dictionaries for extensibility
- `CreatedAtUtc` timestamps

**Rationale:** Debugging and auditing support. Enables future features (expiration, user corrections). Meets spec requirement for provenance.

---

#### 6.6 Strong Typing with Enums

Use enums for all status/strategy/mode values:
- `ToolCallStatus` (Pending, Success, Error, Cancelled)
- `SessionStrategy` (PerConversation, PerDay, PersistentPerUser)
- `RetrievalBlendMode` (MemoryOnly, GraphRagOnly, Blended, etc.)
- `TruncationStrategy` (OldestFirst, LowestScoreFirst, Proportional, Fail)
- `ExtractionTypes` (flags enum)

**Rationale:** Compile-time safety, IntelliSense support, avoids stringly-typed APIs, clear intent.

---

#### 6.7 GraphRAG Types in Abstractions

Define `IGraphRagContextSource`, `GraphRagContextRequest`, `GraphRagContextResult` in Abstractions, not adapter.

**Rationale:** Dependency inversion principle. Core depends on abstraction. Adapter implements abstraction. Enables testing with mocks.

---

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
