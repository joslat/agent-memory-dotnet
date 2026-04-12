# Abstractions Domain Model — Quick Reference

**Complete Design:** See `domain-design-v1.md`  
**Author:** Roy  
**Date:** 2025-01-27

---

## Package Contents Summary

### Domain Models (31 types)

**Short-Term Memory (3)**
- Conversation
- Message
- SessionInfo

**Long-Term Memory (4)**
- Entity
- Fact
- Preference
- Relationship

**Reasoning Memory (4)**
- ReasoningTrace
- ReasoningStep
- ToolCall
- ToolCallStatus (enum)

**Context & Recall (4)**
- MemoryContext
- MemoryContextSection<T>
- RecallRequest
- RecallResult

**Extraction (7)**
- ExtractionRequest
- ExtractionResult
- ExtractedEntity
- ExtractedRelationship
- ExtractedFact
- ExtractedPreference
- ExtractionTypes (flags enum)

**GraphRAG (5)**
- GraphRagContextRequest
- GraphRagContextResult
- GraphRagContextItem
- GraphRagSearchMode (enum)

**Configuration (9)**
- MemoryOptions
- ShortTermMemoryOptions
- LongTermMemoryOptions
- ReasoningMemoryOptions
- RecallOptions
- ContextBudget
- SessionStrategy (enum)
- RetrievalBlendMode (enum)
- TruncationStrategy (enum)

---

### Service Interfaces (11)

1. **IMemoryService** — main facade
2. **IShortTermMemoryService** — conversations, messages
3. **ILongTermMemoryService** — entities, facts, preferences, relationships
4. **IReasoningMemoryService** — traces, steps, tool calls
5. **IMemoryContextAssembler** — context orchestration
6. **IMemoryExtractionPipeline** — extraction coordination
7. **IEntityExtractor** — entity extraction
8. **IRelationshipExtractor** — relationship extraction
9. **IPreferenceExtractor** — preference extraction
10. **IFactExtractor** — fact extraction
11. **IEmbeddingProvider** — vector embeddings
12. **IEntityResolver** — entity deduplication
13. **IGraphRagContextSource** — GraphRAG integration
14. **IClock** — testable time
15. **IIdGenerator** — testable ID generation

---

### Repository Interfaces (10)

1. **IConversationRepository**
2. **IMessageRepository**
3. **IEntityRepository**
4. **IPreferenceRepository**
5. **IFactRepository**
6. **IRelationshipRepository**
7. **IReasoningTraceRepository**
8. **IReasoningStepRepository**
9. **IToolCallRepository**
10. **ISchemaRepository**

---

## Key Patterns

### All Domain Models
- C# records with init-only properties
- `required` for spec-mandated fields
- Nullable types for optional fields
- `IReadOnlyList<T>` and `IReadOnlyDictionary<K,V>` for collections
- Default empty collections instead of null
- XML doc comments on all public members

### All Service Interfaces
- Async-first with `Task<T>` returns
- `CancellationToken` parameter on all methods (with default)
- Descriptive method names (Add, Get, Search, List, Record, etc.)

### All Repository Interfaces
- `UpsertAsync` for add-or-update
- `GetByIdAsync` for single lookups
- `GetByXAsync` for filtered queries
- `SearchByVectorAsync` for semantic searches
- Scored results return `(T, double Score)` tuples

### Embedding Fields
- All embedding fields: `float[]?` (nullable array)
- Dimensions not enforced in domain models (provider-specific)

### Timestamps
- All timestamps: `DateTimeOffset` with `Utc` suffix
- CreatedAt required, UpdatedAt/CompletedAt optional

### Provenance
- `SourceMessageIds` on extracted long-term memory
- `Metadata` dictionaries throughout for extensibility

---

## Namespace Organization

```
Neo4j.AgentMemory.Abstractions.Domain
  - All domain models (records)
  - All enums

Neo4j.AgentMemory.Abstractions.Services
  - All service interfaces

Neo4j.AgentMemory.Abstractions.Repositories
  - All repository interfaces

Neo4j.AgentMemory.Abstractions.Options
  - All configuration models
```

---

## Dependencies

**Abstractions package has ZERO external dependencies:**
- ✅ .NET 9 BCL only
- ❌ No Neo4j.Driver
- ❌ No Microsoft.Agents.*
- ❌ No GraphRAG SDKs
- ❌ No MCP SDKs

---

## Stats

- **Total Types:** ~50
- **Total Interfaces:** 21
- **Total Records:** ~30
- **Total Enums:** 6
- **Lines of Code (design doc):** ~2,800

---

## Review Status

- ✅ Specification compliance verified
- ✅ Implementation plan alignment verified
- ⏳ Awaiting Deckard architectural review
- ⏳ Pending team consensus on open questions

---

**Next:** Scaffold package after review approval.
