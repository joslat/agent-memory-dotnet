# Roy — History

## Project Context
- **Project:** Agent Memory for .NET
- **User:** Jose Luis Latorre Millas
- **Stack:** .NET 9, C#, Neo4j, Microsoft Agent Framework, GraphRAG
- **Role focus:** Core memory domain — Abstractions + Core packages
- **Architecture:** Framework-agnostic core, ports-and-adapters

## Learnings

### 2025-01-27: Phase 1 Domain Model Design

**Task:** Designed complete domain model and interfaces for Neo4j.AgentMemory.Abstractions package.

**Key Design Decisions:**

1. **Domain Model Patterns**
   - Used C# records for all domain models (immutability, value semantics, concise syntax)
   - Applied `required` keyword for spec-mandated fields, nullable types for optional
   - Default empty collections instead of null (better API ergonomics)
   - IReadOnlyList/IReadOnlyDictionary for all collection properties

2. **Service Interface Architecture**
   - IMemoryService as main facade for all operations
   - Separate interfaces per memory layer (IShortTermMemoryService, ILongTermMemoryService, IReasoningMemoryService)
   - IMemoryContextAssembler for orchestrating recall from multiple sources
   - Individual extractor interfaces (IEntityExtractor, IFactExtractor, IPreferenceExtractor, IRelationshipExtractor)
   - IEmbeddingProvider abstraction for vector generation
   - IGraphRagContextSource for GraphRAG interop (defined in Abstractions for dependency inversion)

3. **Repository Pattern Consistency**
   - All repositories follow consistent naming: UpsertAsync, GetByXAsync, SearchByVectorAsync
   - Scored results return tuples: `(Entity, double Score)` for semantic searches
   - Batch operations where appropriate (AddBatchAsync for messages)
   - Separate repositories for each aggregate root per DDD principles

4. **Specification Compliance**
   - All required fields from spec section 3 mapped to domain models
   - Message: MessageId, SessionId, ConversationId, Role, Content, TimestampUtc, Metadata, Embedding
   - Entity: EntityId, Name, CanonicalName, Type, Subtype, Description, Confidence, Attributes
   - Fact: Subject, Predicate, Object, Confidence, ValidFrom/Until
   - Preference: Category, PreferenceText, Context, Confidence
   - Relationship: SourceEntityId, TargetEntityId, RelationshipType, Confidence
   - ReasoningTrace: TraceId, SessionId, Task, TaskEmbedding, Outcome, Success, StartedAt, CompletedAt
   - ReasoningStep: StepId, TraceId, StepNumber, Thought, Action, Observation
   - ToolCall: ToolCallId, StepId, ToolName, ArgumentsJson, ResultJson, Status, DurationMs, Error

5. **Context and Recall Design**
   - MemoryContext as assembled context container with typed sections
   - MemoryContextSection<T> for generic section handling with metadata
   - RecallRequest/RecallResult for recall operations
   - RecallOptions with configurable limits per memory type
   - ContextBudget for token/character limits with truncation strategies

6. **Extraction Pipeline**
   - ExtractionRequest with configurable ExtractionTypes flags enum
   - Separate "Extracted*" types (ExtractedEntity, ExtractedFact, etc.) before persistence
   - ExtractionResult with collections per type
   - Provenance via SourceMessageIds throughout

7. **Configuration Model**
   - MemoryOptions as root configuration with nested options
   - ShortTermMemoryOptions, LongTermMemoryOptions, ReasoningMemoryOptions
   - RecallOptions with Default singleton
   - ContextBudget with TruncationStrategy enum
   - All options use records with init-only properties

8. **Zero Framework Dependencies**
   - No Neo4j.Driver types in Abstractions
   - No Microsoft.Agents.* references
   - No GraphRAG SDK types
   - Pure .NET 9 with nullable reference types enabled

9. **Async and Cancellation**
   - All async methods accept CancellationToken (default parameter)
   - Consistent Task<T> return types
   - Batch operations return IReadOnlyList<T>

10. **Utility Abstractions**
    - IClock for testable time operations
    - IIdGenerator for testable ID generation
    - IEntityResolver for deduplication logic
    - ISchemaRepository for schema versioning and migrations

**Artifacts:**
- Created `.squad/agents/roy/domain-design-v1.md` — 70KB complete design document

**Next Steps:**
- Await Deckard's architectural review
- Address any feedback on interface boundaries or patterns
- Scaffold Neo4j.AgentMemory.Abstractions package
- Begin Core layer design (orchestration services)
