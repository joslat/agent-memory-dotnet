# Feature Record — Agent Memory for .NET

> **Generated:** 2026-04-17 | **Updated:** 2026-04-17 (Post Gap Closure Sprint — Waves A/B/C)
> **Author:** Sebastian (GraphRAG Interop Engineer) | **Reviewer:** Deckard (Lead Architect)
> **Project:** Neo4j.AgentMemory for .NET 9
> **Total Unit Tests:** 1059 | **Integration Tests:** 71+ | **Test Files:** 111+ test class files

---

## Top-Level Feature Table

| # | Feature | Sub-Features | Description | Value Score | Why Valuable | Test Coverage |
|---|---------|:---:|-------------|---:|-------------|:---:|
| 1 | **Short-Term Memory** | 6 | Conversations, messages, sessions, batch add, vector search, session clearing | 95 | Core conversational context — agents can't function without message history | 12 tests |
| 2 | **Long-Term Memory** | 8 | Entities, facts, preferences, relationships, vector search, embedding gen, deletion, batch ops | 95 | Persistent knowledge graph — the differentiating value of this library | 17 tests |
| 3 | **Entity Resolution** | 5 | Exact match, fuzzy match, semantic match, auto-merge, SAME_AS flagging | 90 | Prevents duplicate entities; quality of knowledge graph depends on this | 33 tests |
| 4 | **Extraction Pipeline** | 7 | Entity/fact/preference/relationship extraction, validation, parallel execution, provenance | 90 | Converts unstructured text into structured knowledge automatically | 16 tests |
| 5 | **LLM Extraction** | 4 | LLM entity, fact, preference, relationship extractors via IChatClient | 85 | Production extraction — converts conversation to structured memory via AI | 31 tests |
| 6 | **Reasoning Traces** | 4 | Trace recording, steps, tool calls, similar trace search | 80 | Enables agent self-reflection and learning from past tasks | 10 tests |
| 7 | **Graph Schema** | 4 | Constraints, indexes, vector indexes, migration runner | 85 | Data integrity + query performance — foundation for all persistence | 14 tests |
| 8 | **Vector Search** | 3 | Embedding generation, similarity search, configurable dimensions | 90 | Semantic retrieval across all memory layers — core to context assembly | 7 tests |
| 9 | **MCP Server** | 10 | 28 tools, 6 resources (Conversations, Entities, Preferences, Context, MemoryStatus, Schema), 3 prompts | 85 | Primary integration surface — any MCP-compatible agent can use memory | 53+ tests |
| 10 | **MAF Integration** | 6 | Context provider, chat message store, trace recorder, memory facade, tool factory, type mapper | 80 | Microsoft Agent Framework interop — enterprise agent platform support | 52 tests |
| 11 | **GraphRAG Adapter** | 5 | Vector/fulltext/hybrid/graph retrievers, stop word filter, context source | 85 | External knowledge graph retrieval — complements internal memory | 19 tests |
| 12 | **Azure Language Extraction** | 3 | Named entity extraction, fact extraction, relationship extraction via Azure AI | 70 | Cloud-native extraction alternative — no LLM needed | 22 tests |
| 13 | **Observability** | 4 | OpenTelemetry tracing, counters, histograms, instrumented wrappers | 75 | Production monitoring — essential for debugging and performance tuning | 19 tests |
| 14 | **Geocoding** | 3 | Nominatim service, caching, rate limiting | 50 | Location enrichment for LOCATION entities — niche but useful | 11 tests |
| 15 | **Enrichment** | 3 | Wikipedia service, caching, HTTP resilience | 55 | Entity enrichment from external knowledge — adds context to entities | 12 tests |
| 16 | **Configuration** | 6 | MemoryOptions, RecallOptions, ExtractionOptions, ContextBudget, DI registration, options hierarchy | 85 | Flexible configuration — users can tune every aspect of memory behavior | 6 tests |
| 17 | **Cross-Memory Relationships** | 8 | EXTRACTED_FROM, MENTIONS, SAME_AS, ABOUT, HAS_FACT, HAS_PREFERENCE, HAS_TRACE, TRIGGERED_BY | 80 | Graph connectivity — enables traversal-based context assembly | 20 tests |
| 18 | **Context Assembly** | 4 | Multi-layer retrieval, token budgeting, truncation strategies, GraphRAG blending | 90 | The "recall" brain — assembles optimal context from all memory layers | 10 tests |
| 19 | **Entity Validation** | 4 | Min length, numeric rejection, punctuation rejection, stopword filtering | 70 | Data quality — prevents garbage entities from polluting the knowledge graph | 14 tests |
| 20 | **Infrastructure Stubs** | 5 | Stub embedding, stub extractors, stub pipeline, GUID generator, system clock | 60 | Phase 1 testability — enables memory testing without AI dependencies | 21 tests |

---

## Feature 1: Short-Term Memory

**Value Score:** 95/100
**Description:** Manages conversational context — messages within sessions and conversations. Supports CRUD, batch operations, vector similarity search, and session lifecycle. This is the "working memory" that agents use to maintain conversation flow.
**Package:** Neo4j.AgentMemory.Core + Neo4j.AgentMemory.Neo4j

### Sub-Features

| Sub-Feature | Implementation | Interface | Status | Test(s) |
|-------------|---------------|-----------|--------|---------|
| Add conversation | `ShortTermMemoryService.AddConversationAsync` | `IShortTermMemoryService` | ✅ Complete | 3 |
| Add message (single) | `ShortTermMemoryService.AddMessageAsync` | `IShortTermMemoryService` | ✅ Complete | 3 |
| Add messages (batch) | `ShortTermMemoryService.AddMessagesAsync` | `IShortTermMemoryService` | ✅ Complete | 1 |
| Get recent messages | `ShortTermMemoryService.GetRecentMessagesAsync` | `IShortTermMemoryService` | ✅ Complete | 2 |
| Search messages (vector) | `ShortTermMemoryService.SearchMessagesAsync` | `IShortTermMemoryService` | ✅ Complete | 1 |
| Clear session | `ShortTermMemoryService.ClearSessionAsync` | `IShortTermMemoryService` | ✅ Complete | 1 |

### Test Coverage

| Test Class | Test Method | What It Verifies |
|------------|-------------|-----------------|
| ShortTermMemoryServiceTests | AddConversationAsync_CreatesConversationWithGeneratedId | Conversation creation with auto-generated ID |
| ShortTermMemoryServiceTests | AddConversationAsync_SetsTimestampsFromClock | Timestamps use injected clock |
| ShortTermMemoryServiceTests | AddConversationAsync_UpsertsDelegatesToRepository | Delegates to repository for persistence |
| ShortTermMemoryServiceTests | AddMessageAsync_GeneratesEmbeddingWhenEnabled | Auto-generates embedding when enabled |
| ShortTermMemoryServiceTests | AddMessageAsync_SkipsEmbeddingWhenDisabled | Respects embedding disabled config |
| ShortTermMemoryServiceTests | AddMessageAsync_SkipsEmbeddingWhenAlreadyProvided | Doesn't re-generate existing embeddings |
| ShortTermMemoryServiceTests | AddMessageAsync_DelegatesToRepository | Delegates to message repository |
| ShortTermMemoryServiceTests | AddMessagesAsync_EmbedsEachMessage | Batch embedding generation |
| ShortTermMemoryServiceTests | GetRecentMessagesAsync_DelegatesToRepository | Retrieval delegation |
| ShortTermMemoryServiceTests | GetRecentMessagesAsync_CapsAtMaxMessagesPerQuery | Enforces configurable limit |
| ShortTermMemoryServiceTests | SearchMessagesAsync_DelegatesToRepositoryAndStripsScores | Vector search with score stripping |
| ShortTermMemoryServiceTests | ClearSessionAsync_DeletesMessagesAndConversations | Full session cleanup |

---

## Feature 2: Long-Term Memory

**Value Score:** 95/100
**Description:** Persistent structured knowledge storage — entities (people, places, things), facts (subject-predicate-object triples), preferences (user likes/dislikes), and relationships between entities. Supports vector similarity search, automatic embedding generation, and preference deletion.
**Package:** Neo4j.AgentMemory.Core + Neo4j.AgentMemory.Neo4j

### Sub-Features

| Sub-Feature | Implementation | Interface | Status | Test(s) |
|-------------|---------------|-----------|--------|---------|
| Add/upsert entity | `LongTermMemoryService.AddEntityAsync` | `ILongTermMemoryService` | ✅ Complete | 3 |
| Search entities (vector) | `LongTermMemoryService.SearchEntitiesAsync` | `ILongTermMemoryService` | ✅ Complete | 1 |
| Get entities by name | `LongTermMemoryService.GetEntitiesByNameAsync` | `ILongTermMemoryService` | ✅ Complete | 1 |
| Add/upsert fact | `LongTermMemoryService.AddFactAsync` | `ILongTermMemoryService` | ✅ Complete | 1 |
| Search/get facts | `LongTermMemoryService.SearchFactsAsync` / `GetFactsBySubjectAsync` | `ILongTermMemoryService` | ✅ Complete | 2 |
| Add/upsert preference | `LongTermMemoryService.AddPreferenceAsync` | `ILongTermMemoryService` | ✅ Complete | 2 |
| Delete preference | `LongTermMemoryService.DeletePreferenceAsync` | `ILongTermMemoryService` | ✅ Complete | 3 |
| Add relationship | `LongTermMemoryService.AddRelationshipAsync` | `ILongTermMemoryService` | ✅ Complete | 2 |

### Test Coverage

| Test Class | Test Method | What It Verifies |
|------------|-------------|-----------------|
| LongTermMemoryServiceTests | AddEntityAsync_GeneratesEmbeddingWhenEnabled | Auto-embedding generation |
| LongTermMemoryServiceTests | AddEntityAsync_SkipsEmbeddingWhenAlreadyProvided | Skips pre-computed embeddings |
| LongTermMemoryServiceTests | AddEntityAsync_UpsertsToRepository | Repository persistence |
| LongTermMemoryServiceTests | GetEntitiesByNameAsync_DelegatesToRepository | Name lookup delegation |
| LongTermMemoryServiceTests | SearchEntitiesAsync_DelegatesToRepositoryAndStripsScores | Vector search |
| LongTermMemoryServiceTests | AddPreferenceAsync_GeneratesEmbeddingWhenEnabled | Preference embedding |
| LongTermMemoryServiceTests | AddPreferenceAsync_UpsertsToRepository | Preference persistence |
| LongTermMemoryServiceTests | GetPreferencesByCategoryAsync_DelegatesToRepository | Category lookup |
| LongTermMemoryServiceTests | SearchPreferencesAsync_StripsScores | Preference search |
| LongTermMemoryServiceTests | AddFactAsync_GeneratesEmbedding | Fact embedding |
| LongTermMemoryServiceTests | GetFactsBySubjectAsync_DelegatesToRepository | Subject lookup |
| LongTermMemoryServiceTests | SearchFactsAsync_StripsScores | Fact search |
| LongTermMemoryServiceTests | AddRelationshipAsync_UpsertsToRepository | Relationship persistence |
| LongTermMemoryServiceTests | GetEntityRelationshipsAsync_DelegatesToRepository | Relationship lookup |
| LongTermMemoryServiceTests | DeletePreferenceAsync_DelegatesToRepositoryWithCorrectId | Preference deletion |
| LongTermMemoryServiceTests | DeletePreferenceAsync_DelegatesToRepositoryWithAnyId | Deletion with any ID |
| LongTermMemoryServiceTests | DeletePreferenceAsync_RepositoryIsCalled | Deletion delegation |

---

## Feature 3: Entity Resolution

**Value Score:** 90/100
**Description:** Multi-stage entity deduplication pipeline: Exact match → Fuzzy match (FuzzySharp token-sort ratio) → Semantic match (cosine similarity on embeddings). Above auto-merge threshold (0.95), entities are merged. In the SAME_AS confidence band (0.85–0.95), a SAME_AS relationship is created. Below threshold, a new entity is created. Prevents knowledge graph pollution from duplicate mentions.
**Package:** Neo4j.AgentMemory.Core

### Sub-Features

| Sub-Feature | Implementation | Interface | Status | Test(s) |
|-------------|---------------|-----------|--------|---------|
| Exact match | `ExactMatchEntityMatcher` | `IEntityMatcher` (internal) | ✅ Complete | 7 |
| Fuzzy match | `FuzzyMatchEntityMatcher` | `IEntityMatcher` (internal) | ✅ Complete | 7 |
| Semantic match | `SemanticMatchEntityMatcher` | `IEntityMatcher` (internal) | ✅ Complete | 7 |
| Composite resolution | `CompositeEntityResolver` | `IEntityResolver` | ✅ Complete | 12 |
| Find duplicates | `CompositeEntityResolver.FindPotentialDuplicatesAsync` | `IEntityResolver` | ✅ Complete | 2 |

### Test Coverage

| Test Class | Test Method | What It Verifies |
|------------|-------------|-----------------|
| ExactMatchEntityMatcherTests | TryMatchAsync_ExactNameMatch_ReturnsConfidence1 | Perfect name match returns 1.0 |
| ExactMatchEntityMatcherTests | TryMatchAsync_CaseInsensitiveNameMatch_ReturnsResult | Case-insensitive matching |
| ExactMatchEntityMatcherTests | TryMatchAsync_MatchOnCanonicalName_ReturnsResult | Canonical name matching |
| ExactMatchEntityMatcherTests | TryMatchAsync_MatchOnAlias_ReturnsResult | Alias-based matching |
| ExactMatchEntityMatcherTests | TryMatchAsync_NoMatch_ReturnsNull | No match returns null |
| ExactMatchEntityMatcherTests | TryMatchAsync_EmptyCandidates_ReturnsNull | Empty candidates handled |
| ExactMatchEntityMatcherTests | MatchType_IsExact | Match type constant |
| FuzzyMatchEntityMatcherTests | TryMatchAsync_TokenSortMatch_JohnSmithVsSmithJohn_ReturnsResult | Token-sort ratio matching |
| FuzzyMatchEntityMatcherTests | TryMatchAsync_BelowThreshold_ReturnsNull | Threshold enforcement |
| FuzzyMatchEntityMatcherTests | TryMatchAsync_AboveThreshold_ReturnsCorrectConfidence | Confidence scoring |
| FuzzyMatchEntityMatcherTests | TryMatchAsync_AliasFuzzyMatch_ReturnsResult | Alias fuzzy matching |
| FuzzyMatchEntityMatcherTests | TryMatchAsync_EmptyCandidates_ReturnsNull | Empty handling |
| FuzzyMatchEntityMatcherTests | MatchType_IsFuzzy | Match type constant |
| FuzzyMatchEntityMatcherTests | TryMatchAsync_ConfidenceIsIn0To1Range | Confidence range validation |
| SemanticMatchEntityMatcherTests | TryMatchAsync_HighSimilarityEmbeddings_ReturnsResult | High-similarity matching |
| SemanticMatchEntityMatcherTests | TryMatchAsync_LowSimilarityEmbeddings_ReturnsNull | Low-similarity rejection |
| SemanticMatchEntityMatcherTests | TryMatchAsync_EntitiesWithoutEmbeddingsAreSkipped | Missing embedding handling |
| SemanticMatchEntityMatcherTests | TryMatchAsync_EmbeddingProviderCalledOnce | Embedding generation efficiency |
| SemanticMatchEntityMatcherTests | CosineSimilarity_IdenticalVectors_ReturnsOne | Cosine similarity math |
| SemanticMatchEntityMatcherTests | CosineSimilarity_OrthogonalVectors_ReturnsZero | Orthogonal vector math |
| SemanticMatchEntityMatcherTests | MatchType_IsSemantic | Match type constant |
| CompositeEntityResolverTests | ResolveEntityAsync_ExactMatch_ReturnsExisting_WithoutCallingEmbeddingProvider | Exact match short-circuits |
| CompositeEntityResolverTests | ResolveEntityAsync_FuzzyMatchWhenExactFails_ReturnsExisting | Fuzzy fallback |
| CompositeEntityResolverTests | ResolveEntityAsync_SemanticMatchWhenFuzzyFails_ReturnsExisting | Semantic fallback |
| CompositeEntityResolverTests | ResolveEntityAsync_NoMatch_CreatesNewEntity | New entity creation |
| CompositeEntityResolverTests | ResolveEntityAsync_TypeStrictFiltering_FetchesCandidatesOfCorrectType | Type filtering |
| CompositeEntityResolverTests | ResolveEntityAsync_ExactMatchAboveAutoMergeThreshold_CallsUpsert | Auto-merge above threshold |
| CompositeEntityResolverTests | ResolveEntityAsync_ConfidenceInSameAsRange_ReturnsExistingWithoutUpsert | SAME_AS band |
| CompositeEntityResolverTests | FindPotentialDuplicatesAsync_ReturnsMatchedEntities | Duplicate detection |
| CompositeEntityResolverTests | FindPotentialDuplicatesAsync_NoMatches_ReturnsEmpty | No duplicates |
| CompositeEntityResolverTests | ResolveEntityAsync_AutoMerge_AliasAdded_RegeneratesEmbedding | Re-embedding after merge |
| CompositeEntityResolverTests | ResolveEntityAsync_AutoMerge_AliasAlreadyPresent_DoesNotRegenerateEmbedding | Merge optimization |
| CompositeEntityResolverTests | ResolveEntityAsync_AutoMerge_EmbeddingTextContainsNameAndNewAlias | Embedding text content |

---

## Feature 4: Extraction Pipeline

**Value Score:** 90/100
**Description:** Five-stage pipeline: (1) Extract entities/facts/preferences/relationships in parallel, (2) Validate entities, (3) Resolve entities against existing graph, (4) Generate embeddings, (5) Persist to repositories with EXTRACTED_FROM provenance. Supports selective extraction via `ExtractionTypes` flags and graceful degradation on errors.
**Package:** Neo4j.AgentMemory.Core

### Sub-Features

| Sub-Feature | Implementation | Interface | Status | Test(s) |
|-------------|---------------|-----------|--------|---------|
| Parallel extraction | `MemoryExtractionPipeline.ExtractAsync` | `IMemoryExtractionPipeline` | ✅ Complete | 2 |
| Entity validation | Uses `EntityValidator` | — | ✅ Complete | 1 |
| Confidence filtering | Configurable `MinConfidenceThreshold` | `ExtractionOptions` | ✅ Complete | 1 |
| Embedding generation | Auto-generates for entities, facts, preferences | — | ✅ Complete | 1 |
| Provenance tracking | Creates EXTRACTED_FROM relationships | — | ✅ Complete | 4 |
| Error resilience | Catches extraction/persistence errors | — | ✅ Complete | 3 |
| Type flag filtering | `ExtractionTypes` flags (Entities, Facts, Preferences, Relationships) | — | ✅ Complete | 1 |

### Test Coverage

| Test Class | Test Method | What It Verifies |
|------------|-------------|-----------------|
| MemoryExtractionPipelineTests | ExtractAsync_WithEntities_ExtractsValidatesResolvesAndPersists | Full entity pipeline |
| MemoryExtractionPipelineTests | ExtractAsync_FiltersLowConfidenceEntities | Confidence threshold |
| MemoryExtractionPipelineTests | ExtractAsync_FiltersInvalidEntityNames | Entity validation |
| MemoryExtractionPipelineTests | ExtractAsync_WithFacts_PersistsToRepository | Fact persistence |
| MemoryExtractionPipelineTests | ExtractAsync_WithPreferences_PersistsToRepository | Preference persistence |
| MemoryExtractionPipelineTests | ExtractAsync_WithRelationships_ResolvesEntityIdsAndPersists | Relationship with entity linking |
| MemoryExtractionPipelineTests | ExtractAsync_SkipsRelationshipsWithUnknownEntities | Unknown entity handling |
| MemoryExtractionPipelineTests | ExtractAsync_RespectsExtractionTypeFlags | Flag-based filtering |
| MemoryExtractionPipelineTests | ExtractAsync_GeneratesEmbeddings | Embedding generation |
| MemoryExtractionPipelineTests | ExtractAsync_EmptyMessages_ReturnsEmptyResult | Empty input handling |
| MemoryExtractionPipelineTests | ExtractAsync_ExtractionError_ContinuesGracefully | Extraction error resilience |
| MemoryExtractionPipelineTests | ExtractAsync_PersistenceError_ContinuesGracefully | Persistence error resilience |
| MemoryExtractionPipelineTests | ExtractAsync_Entity_CreatesExtractedFromRelationshipForEachSourceMessage | Entity provenance |
| MemoryExtractionPipelineTests | ExtractAsync_Fact_CreatesExtractedFromRelationshipForEachSourceMessage | Fact provenance |
| MemoryExtractionPipelineTests | ExtractAsync_Preference_CreatesExtractedFromRelationshipForEachSourceMessage | Preference provenance |
| MemoryExtractionPipelineTests | ExtractAsync_ExtractedFromFailure_DoesNotFailExtraction | Provenance error resilience |

---

## Feature 5: LLM Extraction

**Value Score:** 85/100
**Description:** Production extractors using `IChatClient` (Microsoft.Extensions.AI) to extract structured knowledge from conversation text via LLM prompting. Four granular extractors (entity, fact, preference, relationship) with JSON parsing, type normalization, confidence mapping, and configurable model selection.
**Package:** Neo4j.AgentMemory.Extraction.Llm

### Sub-Features

| Sub-Feature | Implementation | Interface | Status | Test(s) |
|-------------|---------------|-----------|--------|---------|
| Entity extraction | `LlmEntityExtractor` | `IEntityExtractor` | ✅ Complete | 9 |
| Fact extraction | `LlmFactExtractor` | `IFactExtractor` | ✅ Complete | 7 |
| Preference extraction | `LlmPreferenceExtractor` | `IPreferenceExtractor` | ✅ Complete | 7 |
| Relationship extraction | `LlmRelationshipExtractor` | `IRelationshipExtractor` | ✅ Complete | 8 |

### Test Coverage

| Test Class | Test Method | What It Verifies |
|------------|-------------|-----------------|
| LlmEntityExtractorTests | ExtractAsync_EmptyMessages_ReturnsEmpty | Empty input handling |
| LlmEntityExtractorTests | ExtractAsync_ValidJson_ReturnsEntities | Successful extraction |
| LlmEntityExtractorTests | ExtractAsync_MalformedJson_ReturnsEmpty | JSON error resilience |
| LlmEntityExtractorTests | ExtractAsync_MultipleMessages_ConcatenatesContent | Multi-message handling |
| LlmEntityExtractorTests | ExtractAsync_TypeNormalization_ConceptBecomesObject | Type normalization |
| LlmEntityExtractorTests | ExtractAsync_TypeNormalization_AllMappings | All type mappings (Theory) |
| LlmEntityExtractorTests | ExtractAsync_NullOptionalFields_HandledGracefully | Null field handling |
| LlmEntityExtractorTests | ExtractAsync_ClientThrows_ReturnsEmpty | Error handling |
| LlmEntityExtractorTests | ExtractAsync_ModelIdPropagated_WhenConfigured | Model ID config |
| LlmFactExtractorTests | ExtractAsync_EmptyMessages_ReturnsEmpty | Empty input |
| LlmFactExtractorTests | ExtractAsync_ValidJson_ReturnsFacts | Successful extraction |
| LlmFactExtractorTests | ExtractAsync_MalformedJson_ReturnsEmpty | JSON error resilience |
| LlmFactExtractorTests | ExtractAsync_MultipleMessages_ConcatenatesContent | Multi-message handling |
| LlmFactExtractorTests | ExtractAsync_ConfidenceValues_MappedCorrectly | Confidence mapping |
| LlmFactExtractorTests | ExtractAsync_ClientThrows_ReturnsEmpty | Error handling |
| LlmFactExtractorTests | ExtractAsync_EmptyFactsArray_ReturnsEmpty | Empty array |
| LlmPreferenceExtractorTests | ExtractAsync_EmptyMessages_ReturnsEmpty | Empty input |
| LlmPreferenceExtractorTests | ExtractAsync_ValidJson_ReturnsPreferences | Successful extraction |
| LlmPreferenceExtractorTests | ExtractAsync_MalformedJson_ReturnsEmpty | JSON error resilience |
| LlmPreferenceExtractorTests | ExtractAsync_MultipleMessages_ConcatenatesContent | Multi-message handling |
| LlmPreferenceExtractorTests | ExtractAsync_ConfidenceValues_MappedCorrectly | Confidence mapping |
| LlmPreferenceExtractorTests | ExtractAsync_NullContext_HandledGracefully | Null context |
| LlmPreferenceExtractorTests | ExtractAsync_ClientThrows_ReturnsEmpty | Error handling |
| LlmRelationshipExtractorTests | ExtractAsync_EmptyMessages_ReturnsEmpty | Empty input |
| LlmRelationshipExtractorTests | ExtractAsync_ValidJson_ReturnsRelationships | Successful extraction |
| LlmRelationshipExtractorTests | ExtractAsync_MalformedJson_ReturnsEmpty | JSON error resilience |
| LlmRelationshipExtractorTests | ExtractAsync_MultipleMessages_ConcatenatesContent | Multi-message handling |
| LlmRelationshipExtractorTests | ExtractAsync_ConfidenceValues_MappedCorrectly | Confidence mapping |
| LlmRelationshipExtractorTests | ExtractAsync_NullDescription_HandledGracefully | Null handling |
| LlmRelationshipExtractorTests | ExtractAsync_ClientThrows_ReturnsEmpty | Error handling |
| LlmRelationshipExtractorTests | ExtractAsync_EmptyRelationsArray_ReturnsEmpty | Empty array |

---

## Feature 6: Reasoning Traces

**Value Score:** 80/100
**Description:** Records agent reasoning processes as traces with sequential steps, tool call invocations, and outcomes. Supports vector similarity search on task embeddings for finding similar past reasoning patterns. Enables agent self-reflection and learning.
**Package:** Neo4j.AgentMemory.Core + Neo4j.AgentMemory.Neo4j

### Sub-Features

| Sub-Feature | Implementation | Interface | Status | Test(s) |
|-------------|---------------|-----------|--------|---------|
| Start/complete trace | `ReasoningMemoryService.StartTraceAsync` / `CompleteTraceAsync` | `IReasoningMemoryService` | ✅ Complete | 4 |
| Add reasoning step | `ReasoningMemoryService.AddStepAsync` | `IReasoningMemoryService` | ✅ Complete | 2 |
| Record tool call | `ReasoningMemoryService.RecordToolCallAsync` | `IReasoningMemoryService` | ✅ Complete | 1 |
| Search similar traces | `ReasoningMemoryService.SearchSimilarTracesAsync` | `IReasoningMemoryService` | ✅ Complete | 1 |

### Test Coverage

| Test Class | Test Method | What It Verifies |
|------------|-------------|-----------------|
| ReasoningMemoryServiceTests | StartTraceAsync_CreatesTraceWithGeneratedId | Trace ID generation |
| ReasoningMemoryServiceTests | StartTraceAsync_SetsStartedAtFromClock | Timestamp from clock |
| ReasoningMemoryServiceTests | AddStepAsync_CreatesStepWithGeneratedId | Step ID generation |
| ReasoningMemoryServiceTests | AddStepAsync_LinksToTrace | Step-trace linking |
| ReasoningMemoryServiceTests | RecordToolCallAsync_CreatesToolCallWithAllProperties | Tool call recording |
| ReasoningMemoryServiceTests | CompleteTraceAsync_SetsOutcomeAndSuccess | Completion with outcome |
| ReasoningMemoryServiceTests | CompleteTraceAsync_SetsCompletedAtFromClock | Completion timestamp |
| ReasoningMemoryServiceTests | GetTraceWithStepsAsync_ReturnsBothTraceAndSteps | Full trace retrieval |
| ReasoningMemoryServiceTests | ListTracesAsync_DelegatesToRepository | Trace listing |
| ReasoningMemoryServiceTests | SearchSimilarTracesAsync_StripsScores | Similarity search |

---

## Feature 7: Graph Schema

**Value Score:** 85/100
**Description:** Database schema management for Neo4j — creates 10 unique constraints, 3 fulltext indexes, 6 vector indexes, 12 property indexes, and 2 schema persistence indexes. Supports configurable embedding dimensions and Cypher-based migrations. Ensures data integrity and query performance.
**Package:** Neo4j.AgentMemory.Neo4j

### Sub-Features

| Sub-Feature | Implementation | Interface | Status | Test(s) |
|-------------|---------------|-----------|--------|---------|
| Constraints | `SchemaBootstrapper.BootstrapAsync` | `ISchemaBootstrapper` | ✅ Complete | 2 |
| Fulltext indexes | `SchemaBootstrapper` (message, entity, fact) | `ISchemaBootstrapper` | ✅ Complete | 1 |
| Vector indexes | `SchemaBootstrapper.BuildVectorIndexes` | `ISchemaBootstrapper` | ✅ Complete | 5 |
| Schema persistence indexes | `SchemaBootstrapper` (schema_name_idx, schema_version_idx) | `ISchemaBootstrapper` | ✅ Complete (Gap Closure G2) | 0 |
| Migration runner | `MigrationRunner.RunMigrationsAsync` | `IMigrationRunner` | ✅ Complete | 3 |

### Test Coverage

| Test Class | Test Method | What It Verifies |
|------------|-------------|-----------------|
| SchemaBootstrapperTests | BootstrapAsync_ExecutesExpectedTotalNumberOfStatements | Total DDL count |
| SchemaBootstrapperTests | BootstrapAsync_ExecutesAllConstraints | 9 unique constraints |
| SchemaBootstrapperTests | BootstrapAsync_ExecutesAllFulltextIndexes | 3 fulltext indexes |
| SchemaBootstrapperTests | BootstrapAsync_ExecutesAllVectorIndexes | 6 vector indexes |
| SchemaBootstrapperTests | BootstrapAsync_ExecutesAllPropertyIndexes | 9 property indexes |
| SchemaBootstrapperTests | BuildVectorIndexes_EmbeddingDimensionAppearsInAllIndexes | Dimension config (Theory) |
| SchemaBootstrapperTests | BuildVectorIndexes_AllIndexesUseCosineFunction | Cosine similarity |
| SchemaBootstrapperTests | BuildVectorIndexes_AllIndexesTargetEmbeddingProperty | Property targeting |
| SchemaBootstrapperTests | BuildVectorIndexes_AllIndexesAreIdempotent | IF NOT EXISTS |
| SchemaBootstrapperTests | Neo4jOptions_DefaultEmbeddingDimensionsIs1536 | Default dimensions |
| SchemaBootstrapperTests | BootstrapAsync_VectorIndexesUseConfiguredDimensions | Custom dimensions |
| MigrationRunnerTests | RunMigrationsAsync_NoMigrationFolder_DoesNotExecuteAnyTransactions | No-op for empty |
| MigrationRunnerTests | RunMigrationsAsync_NoMigrationFolder_CompletesWithoutThrowing | Error handling |
| MigrationRunnerTests | RunMigrationsAsync_CancellationAlreadyRequested_CompletesEarly | Cancellation |

---

## Feature 8: Vector Search

**Value Score:** 90/100
**Description:** Semantic vector search across all memory layers — messages, entities, facts, preferences, and reasoning traces. Uses configurable embedding dimensions (default 1536) and cosine similarity. The `IEmbeddingProvider` abstraction supports any embedding backend.
**Package:** Neo4j.AgentMemory.Abstractions + Neo4j.AgentMemory.Core + Neo4j.AgentMemory.Neo4j

### Sub-Features

| Sub-Feature | Implementation | Interface | Status | Test(s) |
|-------------|---------------|-----------|--------|---------|
| Embedding generation | `IEmbeddingProvider` (pluggable) | `IEmbeddingProvider` | ✅ Complete | 7 (stub) |
| Similarity search | `SearchByVectorAsync` on all repositories | Multiple repos | ✅ Complete | 0 (integration-level) |
| Configurable dimensions | `Neo4jOptions.EmbeddingDimensions` | — | ✅ Complete | 2 |

### Test Coverage

| Test Class | Test Method | What It Verifies |
|------------|-------------|-----------------|
| StubEmbeddingProviderTests | GenerateEmbeddingAsync_ReturnsDimensionMatchingProperty | Dimension matching |
| StubEmbeddingProviderTests | EmbeddingDimensions_DefaultsTo1536 | Default dimensions |
| StubEmbeddingProviderTests | GenerateEmbeddingAsync_IsDeterministic | Deterministic output |
| StubEmbeddingProviderTests | GenerateEmbeddingAsync_DifferentInputsProduceDifferentVectors | Input differentiation |
| StubEmbeddingProviderTests | GenerateEmbeddingsAsync_ReturnsSameCountAsInput | Batch count matching |
| StubEmbeddingProviderTests | GenerateEmbeddingsAsync_EachVectorHasCorrectDimension | Batch dimensions |
| StubEmbeddingProviderTests | GenerateEmbeddingAsync_ConfigurableDimension | Custom dimensions |

---

## Feature 9: MCP Server

**Value Score:** 85/100
**Description:** Model Context Protocol server exposing 21 tools for memory operations. Any MCP-compatible AI agent can search memory, store messages, add entities/facts/preferences, manage conversations, record reasoning traces, execute Cypher queries, find duplicate entities, trigger extraction, get observations, export graphs, and generate embeddings. Security-gated advanced features (graph query, export, duplicates).
**Package:** Neo4j.AgentMemory.McpServer

### Sub-Features

| Sub-Feature | Implementation | Interface | Status | Test(s) |
|-------------|---------------|-----------|--------|---------|
| Core tools (6) | `CoreMemoryTools` | MCP Tool Attributes | ✅ Complete | 18 |
| Conversation tools (2) | `ConversationTools` | MCP Tool Attributes | ✅ Complete | 6 |
| Entity tools (2) | `EntityTools` | MCP Tool Attributes | ✅ Complete | 6 |
| Reasoning tools (3) | `ReasoningTools` | MCP Tool Attributes | ✅ Complete | 8 |
| Graph query tool (1) | `GraphQueryTools` | MCP Tool Attributes | ✅ Complete | 5 |
| Advanced tools (6) | `AdvancedMemoryTools` | MCP Tool Attributes | ✅ Complete | 12 |
| Observation tools (1) | `ObservationTools` | MCP Tool Attributes | ✅ Complete | — |
| MCP Resources (6) | `ConversationListResource`, `EntityListResource`, `PreferenceListResource`, `ContextResource`, `MemoryStatusResource`, `SchemaInfoResource` | MCP Resource Attributes | ✅ Complete (Gap Closure G7/G8/G10/G11) | — |
| MCP Prompts (3) | Conversation, Reasoning, Review prompts | MCP Prompt Attributes | ✅ Complete | — |
| Configuration | `McpServerOptions` | — | ✅ Complete | 6 |

### Test Coverage

| Test Class | Test Method | What It Verifies |
|------------|-------------|-----------------|
| CoreMemoryToolsTests | MemorySearch_CallsRecallAsyncWithCorrectParameters | Search functionality |
| CoreMemoryToolsTests | MemorySearch_UsesDefaultSessionIdWhenNoneProvided | Default session |
| CoreMemoryToolsTests | MemorySearch_ReturnsJsonWithExpectedStructure | Response structure |
| CoreMemoryToolsTests | MemoryGetContext_CallsRecallAsyncWithCorrectParameters | Context retrieval |
| CoreMemoryToolsTests | MemoryGetContext_ReturnsSerializedResult | Serialization |
| CoreMemoryToolsTests | MemoryStoreMessage_CallsAddMessageAsyncWithCorrectParameters | Message storage |
| CoreMemoryToolsTests | MemoryStoreMessage_UsesDefaultSessionAndConversationIdWhenNoneProvided | Defaults |
| CoreMemoryToolsTests | MemoryStoreMessage_ReturnsJsonWithMessageProperties | JSON response |
| CoreMemoryToolsTests | MemoryAddEntity_CallsAddEntityAsyncWithCorrectProperties | Entity addition |
| CoreMemoryToolsTests | MemoryAddEntity_UsesDefaultConfidenceWhenNoneProvided | Default confidence |
| CoreMemoryToolsTests | MemoryAddEntity_UsesProvidedConfidenceWhenSpecified | Custom confidence |
| CoreMemoryToolsTests | MemoryAddEntity_ReturnsJsonWithEntityProperties | Entity response |
| CoreMemoryToolsTests | MemoryAddPreference_CallsAddPreferenceAsyncWithCorrectProperties | Preference addition |
| CoreMemoryToolsTests | MemoryAddPreference_UsesDefaultConfidenceWhenNoneProvided | Default confidence |
| CoreMemoryToolsTests | MemoryAddPreference_ReturnsJsonWithPreferenceProperties | Preference response |
| CoreMemoryToolsTests | MemoryAddFact_CallsAddFactAsyncWithCorrectProperties | Fact addition |
| CoreMemoryToolsTests | MemoryAddFact_UsesDefaultConfidenceWhenNoneProvided | Default confidence |
| CoreMemoryToolsTests | MemoryAddFact_ReturnsJsonWithFactProperties | Fact response |
| ConversationToolsTests | MemoryGetConversation_CallsGetConversationMessagesAsync | Conversation retrieval |
| ConversationToolsTests | MemoryGetConversation_ReturnsJsonArray | JSON format |
| ConversationToolsTests | MemoryGetConversation_ReturnsEmptyArrayWhenNoMessages | Empty handling |
| ConversationToolsTests | MemoryListSessions_CallsGetBySessionAsync | Session listing |
| ConversationToolsTests | MemoryListSessions_UsesDefaultSessionIdWhenNoneProvided | Default session |
| ConversationToolsTests | MemoryListSessions_ReturnsJsonArrayOfConversations | JSON format |
| EntityToolsTests | MemoryGetEntity_CallsGetEntitiesByNameAsyncWithIncludeAliasesTrue | Entity retrieval |
| EntityToolsTests | MemoryGetEntity_ReturnsJsonArray | JSON format |
| EntityToolsTests | MemoryGetEntity_ReturnsEmptyArrayWhenNoEntitiesFound | Empty handling |
| EntityToolsTests | MemoryCreateRelationship_CallsAddRelationshipAsyncWithCorrectProperties | Relationship creation |
| EntityToolsTests | MemoryCreateRelationship_UsesDefaultConfidence | Default confidence |
| EntityToolsTests | MemoryCreateRelationship_ReturnsJsonWithRelationshipProperties | Response format |
| ReasoningToolsTests | MemoryStartTrace_CallsStartTraceAsyncWithCorrectParameters | Trace start |
| ReasoningToolsTests | MemoryStartTrace_UsesDefaultSessionId | Default session |
| ReasoningToolsTests | MemoryStartTrace_ReturnsJsonWithTraceProperties | Response format |
| ReasoningToolsTests | MemoryRecordStep_CallsAddStepAsyncWithCorrectParameters | Step recording |
| ReasoningToolsTests | MemoryRecordStep_ReturnsJsonWithStepProperties | Response format |
| ReasoningToolsTests | MemoryRecordStep_HandlesNullOptionalFields | Null handling |
| ReasoningToolsTests | MemoryCompleteTrace_CallsCompleteTraceAsyncWithCorrectParameters | Completion |
| ReasoningToolsTests | MemoryCompleteTrace_ReturnsJsonWithTraceProperties | Response format |
| GraphQueryToolsTests | GraphQuery_ThrowsMcpExceptionWhenEnableGraphQueryIsFalse | Feature gate |
| GraphQueryToolsTests | GraphQuery_ThrowsMcpExceptionWithDescriptiveMessage | Error message |
| GraphQueryToolsTests | GraphQuery_CallsQueryAsyncWhenEnabled | Query execution |
| GraphQueryToolsTests | GraphQuery_ReturnsJsonWithRowCountAndRows | Response format |
| GraphQueryToolsTests | GraphQuery_ReturnsEmptyResultsCorrectly | Empty results |
| AdvancedMemoryToolsTests | MemoryRecordToolCall_CallsRecordToolCallAsyncWithCorrectParameters | Tool call recording |
| AdvancedMemoryToolsTests | MemoryRecordToolCall_ReturnsJsonWithToolCallProperties | JSON response |
| AdvancedMemoryToolsTests | MemoryRecordToolCall_ParsesStatusEnum | Status parsing |
| AdvancedMemoryToolsTests | MemoryExportGraph_ThrowsMcpExceptionWhenGraphQueryDisabled | Feature gate |
| AdvancedMemoryToolsTests | MemoryExportGraph_CallsQueryAsyncForNodesAndRelationships | Graph export |
| AdvancedMemoryToolsTests | MemoryExportGraph_ReturnsJsonWithNodeAndRelationshipCount | Response format |
| AdvancedMemoryToolsTests | MemoryFindDuplicates_ThrowsMcpExceptionWhenGraphQueryDisabled | Feature gate |
| AdvancedMemoryToolsTests | MemoryFindDuplicates_CallsQueryAsyncWithThresholdParameter | Duplicate detection |
| AdvancedMemoryToolsTests | MemoryFindDuplicates_ReturnsJsonWithPairCount | Result format |
| AdvancedMemoryToolsTests | ExtractAndPersist_CallsExtractAndPersistAsyncWithBuiltMessage | Extraction |
| AdvancedMemoryToolsTests | ExtractAndPersist_UsesDefaultSessionIdFromOptions | Default session |
| AdvancedMemoryToolsTests | ExtractAndPersist_ReturnsJsonWithExtractedCounts | Extraction counts |
| McpServerOptionsTests | DefaultServerName_IsNeo4jAgentMemory | Default name |
| McpServerOptionsTests | DefaultServerVersion_Is100 | Default version |
| McpServerOptionsTests | DefaultEnableGraphQuery_IsFalse | Feature gate default |
| McpServerOptionsTests | DefaultSessionId_IsDefault | Session default |
| McpServerOptionsTests | DefaultConfidence_Is09 | Confidence default |
| McpServerOptionsTests | Properties_CanBeOverridden | Configuration override |

---

## Feature 10: MAF Integration

**Value Score:** 80/100
**Description:** Microsoft Agent Framework adapter — provides `AIContextProvider` for injecting memory into agent runs, chat message store for MAF-compatible persistence, reasoning trace recorder, memory facade for simplified usage, tool factory creating 6 callable memory tools, and bidirectional type mapper between MAF/MEAI and internal types.
**Package:** Neo4j.AgentMemory.AgentFramework

### Sub-Features

| Sub-Feature | Implementation | Interface | Status | Test(s) |
|-------------|---------------|-----------|--------|---------|
| Context provider | `Neo4jMemoryContextProvider` | `AIContextProvider` (MAF) | ✅ Complete | 11 |
| Chat message store | `Neo4jChatMessageStore` | — | ✅ Complete | 7 |
| Agent trace recorder | `AgentTraceRecorder` | — | ✅ Complete | 8 |
| Memory facade | `Neo4jMicrosoftMemoryFacade` | — | ✅ Complete | 8 |
| Tool factory | `MemoryToolFactory` | — | ✅ Complete | 10 |
| Type mapper | `MafTypeMapper` | — (internal static) | ✅ Complete | 14 |

### Test Coverage

| Test Class | Test Method | What It Verifies |
|------------|-------------|-----------------|
| AgentTraceRecorderTests | StartTrace_CreatesTraceWithCorrectFields | Trace creation |
| AgentTraceRecorderTests | RecordStep_AddsStepToTrace | Step recording |
| AgentTraceRecorderTests | RecordToolCall_AddsToolCallToStep | Tool call recording |
| AgentTraceRecorderTests | CompleteTrace_SetsOutcomeAndDuration | Trace completion |
| AgentTraceRecorderTests | StartTrace_GeneratesUniqueIds | Unique IDs |
| AgentTraceRecorderTests | RecordStep_IncrementsStepNumber | Step numbering |
| AgentTraceRecorderTests | RecordToolCall_WithError_SetsErrorStatus | Error status |
| AgentTraceRecorderTests | CompleteTrace_NonExistentTrace_LogsWarning | Warning logging |
| MafTypeMapperTests | ToInternalMessage_UserMessage_MapsCorrectly | User message mapping |
| MafTypeMapperTests | ToInternalMessage_AssistantMessage_MapsRole | Assistant role |
| MafTypeMapperTests | ToInternalMessage_SystemMessage_MapsRole | System role |
| MafTypeMapperTests | ToInternalMessage_NullText_MapsToEmptyContent | Null handling |
| MafTypeMapperTests | ToChatMessage_UserRole_MapsCorrectly | Reverse mapping |
| MafTypeMapperTests | ToChatMessage_AssistantRole_MapsCorrectly | Reverse mapping |
| MafTypeMapperTests | ToChatMessage_SystemRole_MapsCorrectly | Reverse mapping |
| MafTypeMapperTests | ToChatMessage_UnknownRole_MapsToCustomChatRole | Custom roles |
| MafTypeMapperTests | ToContextMessages_EmptyContext_ReturnsOnlyPrefix | Empty context |
| MafTypeMapperTests | ToContextMessages_WithMessages_IncludesMessages | Message inclusion |
| MafTypeMapperTests | ToContextMessages_WithEntities_IncludesEntityMessage | Entity inclusion |
| MafTypeMapperTests | ToContextMessages_EntitiesDisabled_ExcludesEntityMessage | Feature toggle |
| MafTypeMapperTests | ToContextMessages_RespectsMaxContextMessages | Message limit |
| MafTypeMapperTests | ToInternalRole_RoundTrips | Role round-trip (Theory) |
| MemoryToolFactoryTests | CreateTools_Returns6Tools | Tool count |
| MemoryToolFactoryTests | SearchMemory_WithQuery_CallsEmbeddingAndSearch | Search tool |
| MemoryToolFactoryTests | RememberPreference_CreatesAndPersists | Preference tool |
| MemoryToolFactoryTests | RememberFact_CreatesAndPersists | Fact tool |
| MemoryToolFactoryTests | RecallPreferences_WithCategory_FiltersResults | Category filter |
| MemoryToolFactoryTests | RecallPreferences_NoCategory_ReturnsAll | No filter |
| MemoryToolFactoryTests | SearchKnowledge_CallsEntitySearch | Knowledge search |
| MemoryToolFactoryTests | FindSimilarTasks_CallsTraceSearch | Task search |
| MemoryToolFactoryTests | Tool_OnError_ReturnsFailureResponse | Error handling |
| MemoryToolFactoryTests | Tool_EmptyQuery_ReturnsFailure | Validation |
| Neo4jChatMessageStoreTests | (7 tests) | Message storage, role mapping, error handling |
| Neo4jMemoryContextProviderTests | (11 tests) | Context assembly, recall, auto-extraction, error handling |
| Neo4jMicrosoftMemoryFacadeTests | (8 tests) | Facade operations, error isolation |

---

## Feature 11: GraphRAG Adapter

**Value Score:** 85/100
**Description:** Retrieval-Augmented Generation adapter for external Neo4j knowledge graphs. Supports four search modes: Vector (embedding similarity), Fulltext (BM25), Hybrid (concurrent vector + fulltext with max-score merge), and Graph (traversal-based). Includes stop word filtering for fulltext queries.
**Package:** Neo4j.AgentMemory.GraphRagAdapter

### Sub-Features

| Sub-Feature | Implementation | Interface | Status | Test(s) |
|-------------|---------------|-----------|--------|---------|
| Vector retriever | `AdapterVectorRetriever` | `IRetriever` | ✅ Complete | via context source |
| Fulltext retriever | `AdapterFulltextRetriever` | `IRetriever` | ✅ Complete | via context source |
| Hybrid retriever | `AdapterHybridRetriever` | `IRetriever` | ✅ Complete | via context source |
| Context source | `Neo4jGraphRagContextSource` | `IGraphRagContextSource` | ✅ Complete | 11 |
| Stop word filter | `StopWordFilter` | — (internal static) | ✅ Complete | 8 |

### Test Coverage

| Test Class | Test Method | What It Verifies |
|------------|-------------|-----------------|
| Neo4jGraphRagContextSourceTests | GetContext_MapsContentToText | Result mapping |
| Neo4jGraphRagContextSourceTests | GetContext_MapsScoreFromMetadata | Score extraction |
| Neo4jGraphRagContextSourceTests | GetContext_MapsAdditionalMetadata | Metadata preservation |
| Neo4jGraphRagContextSourceTests | GetContext_NullMetadata_ScoreIsZero | Null handling |
| Neo4jGraphRagContextSourceTests | GetContext_RespectsTopKFromRequest | Top-K parameter |
| Neo4jGraphRagContextSourceTests | GetContext_UsesOptionsTopK_WhenRequestTopKIsZero | Defaults |
| Neo4jGraphRagContextSourceTests | GetContext_EmptyResults_ReturnsEmptyItems | Empty handling |
| Neo4jGraphRagContextSourceTests | GetContext_RetrieverThrows_ReturnsEmptyWithoutRethrow | Error resilience |
| Neo4jGraphRagContextSourceTests | GetContext_ForwardsQueryTextToRetriever | Query forwarding |
| Neo4jGraphRagContextSourceTests | GetContext_MultipleItems_AllMapped | Batch mapping |
| Neo4jGraphRagContextSourceTests | Options_DefaultsAreCorrect | Config defaults |
| StopWordFilterTests | ExtractKeywords_EmptyString_ReturnsEmpty | Empty input |
| StopWordFilterTests | ExtractKeywords_OnlyStopWords_ReturnsEmpty | Full filtering |
| StopWordFilterTests | ExtractKeywords_MixedContent_RemovesStopWordsPreservesKeywords | Mixed content |
| StopWordFilterTests | ExtractKeywords_NoStopWords_ReturnsAllWords | Pass-through |
| StopWordFilterTests | ExtractKeywords_SingleCharWords_AreFiltered | Character filtering |
| StopWordFilterTests | ExtractKeywords_IsCaseInsensitive_ForStopWords | Case handling |
| StopWordFilterTests | ExtractKeywords_OutputIsLowercase | Output normalization |
| StopWordFilterTests | ExtractKeywords_ComplexSentence_ExtractsOnlyMeaningfulTerms | Complex input |

---

## Feature 12: Azure Language Extraction

**Value Score:** 70/100
**Description:** Cloud-native extraction alternative using Azure AI Language service. Extracts named entities (NER), facts (key phrases + linked entities), and relationships (co-occurrence). No LLM required — uses deterministic NLP models. Useful for cost-sensitive or low-latency scenarios.
**Package:** Neo4j.AgentMemory.Extraction.AzureLanguage

### Sub-Features

| Sub-Feature | Implementation | Interface | Status | Test(s) |
|-------------|---------------|-----------|--------|---------|
| Entity extraction | `AzureLanguageEntityExtractor` | `IEntityExtractor` | ✅ Complete | 10 |
| Fact extraction | `AzureLanguageFactExtractor` | `IFactExtractor` | ✅ Complete | 7 |
| Relationship extraction | `AzureLanguageRelationshipExtractor` | `IRelationshipExtractor` | ✅ Complete | 5 |

### Test Coverage

| Test Class | Test Method | What It Verifies |
|------------|-------------|-----------------|
| AzureLanguageEntityExtractorTests | Extract_EmptyMessages_ReturnsEmpty | Empty input |
| AzureLanguageEntityExtractorTests | Extract_SingleMessage_ReturnsMappedEntities | Entity mapping |
| AzureLanguageEntityExtractorTests | Extract_PersonCategory_MapsToPersonType | Person type mapping |
| AzureLanguageEntityExtractorTests | Extract_OrganizationCategory_MapsToOrganizationType | Organization mapping |
| AzureLanguageEntityExtractorTests | Extract_LocationCategory_MapsToLocationType | Location mapping |
| AzureLanguageEntityExtractorTests | Extract_EventCategory_MapsToEventType | Event mapping |
| AzureLanguageEntityExtractorTests | Extract_UnknownCategory_MapsToObjectType | Unknown fallback |
| AzureLanguageEntityExtractorTests | Extract_DuplicateEntities_Deduplicated | Deduplication |
| AzureLanguageEntityExtractorTests | Extract_ClientError_ReturnsEmpty_LogsWarning | Error handling |
| AzureLanguageEntityExtractorTests | MapCategory_AllKnownMappings | All category mappings (Theory) |
| AzureLanguageFactExtractorTests | Extract_EmptyMessages_ReturnsEmpty | Empty input |
| AzureLanguageFactExtractorTests | Extract_KeyPhrases_ReturnsFacts | Key phrase facts |
| AzureLanguageFactExtractorTests | Extract_LinkedEntities_ReturnsFacts | Linked entity facts |
| AzureLanguageFactExtractorTests | Extract_EmptyResponse_ReturnsEmpty | Empty response |
| AzureLanguageFactExtractorTests | Extract_ClientError_ReturnsEmpty | Error handling |
| AzureLanguageFactExtractorTests | Extract_MapsFieldsCorrectly | Field mapping |
| AzureLanguageFactExtractorTests | Extract_MultipleMessages_CombinesResults | Multi-message |
| AzureLanguageRelationshipExtractorTests | Extract_EmptyMessages_ReturnsEmpty | Empty input |
| AzureLanguageRelationshipExtractorTests | Extract_CoOccurringEntities_ReturnsRelationships | Co-occurrence |
| AzureLanguageRelationshipExtractorTests | Extract_SingleEntity_NoRelationships | Single entity |
| AzureLanguageRelationshipExtractorTests | Extract_ClientError_ReturnsEmpty | Error handling |
| AzureLanguageRelationshipExtractorTests | Extract_SetsConfidenceScore | Confidence scoring |

---

## Feature 13: Observability

**Value Score:** 75/100
**Description:** OpenTelemetry instrumentation for production monitoring. Provides distributed tracing (spans) for all memory operations and counters/histograms for metrics. Decorator pattern wraps `IMemoryService` and `IGraphRagContextSource` transparently.
**Package:** Neo4j.AgentMemory.Observability

### Sub-Features

| Sub-Feature | Implementation | Interface | Status | Test(s) |
|-------------|---------------|-----------|--------|---------|
| Memory service tracing | `InstrumentedMemoryService` | `IMemoryService` (decorator) | ✅ Complete | 9 |
| GraphRAG tracing | `InstrumentedGraphRagContextSource` | `IGraphRagContextSource` (decorator) | ✅ Complete | 5 |
| Metric counters | `MemoryMetrics` (7 counters) | — | ✅ Complete | 2 |
| Metric histograms | `MemoryMetrics` (5 histograms) | — | ✅ Complete | 2 |

### Test Coverage

| Test Class | Test Method | What It Verifies |
|------------|-------------|-----------------|
| InstrumentedMemoryServiceTests | RecallAsync_CreatesActivity_WithSessionTag | Activity creation |
| InstrumentedMemoryServiceTests | RecallAsync_RecordsMetrics | Metric recording |
| InstrumentedMemoryServiceTests | RecallAsync_OnError_SetsErrorStatus | Error status |
| InstrumentedMemoryServiceTests | AddMessageAsync_CreatesActivity | Message activity |
| InstrumentedMemoryServiceTests | ExtractAndPersist_RecordsExtractionDuration | Duration tracking |
| InstrumentedMemoryServiceTests | ExtractAndPersist_IncrementsCounters | Counter increment |
| InstrumentedMemoryServiceTests | ExtractAndPersist_OnError_IncrementsErrorCounter | Error counting |
| InstrumentedMemoryServiceTests | ClearSession_CreatesActivity | Clear activity |
| InstrumentedMemoryServiceTests | AllMethods_DelegateToInner | Delegation |
| InstrumentedGraphRagContextSourceTests | GetContext_CreatesActivity_WithSearchModeTag | Activity + tag |
| InstrumentedGraphRagContextSourceTests | GetContext_RecordsGraphRagDuration | Duration |
| InstrumentedGraphRagContextSourceTests | GetContext_OnError_SetsErrorStatus | Error status |
| InstrumentedGraphRagContextSourceTests | GetContext_IncrementsQueryCounter | Counter |
| InstrumentedGraphRagContextSourceTests | GetContext_DelegatesToInner | Delegation |
| MemoryMetricsTests | MeterName_IsCorrect | Meter naming |
| MemoryMetricsTests | Constructor_CreatesAllCounters | Counter creation |
| MemoryMetricsTests | Constructor_CreatesAllHistograms | Histogram creation |
| MemoryMetricsTests | Counters_CanBeIncremented | Counter increment |
| MemoryMetricsTests | Histograms_CanRecordValues | Histogram recording |

---

## Feature 14: Geocoding

**Value Score:** 50/100
**Description:** Location string to geographic coordinate resolution using OpenStreetMap's Nominatim API. Includes rate limiting (1 req/sec per Nominatim policy) and in-memory caching (24-hour TTL). Used for enriching LOCATION entities with coordinates.
**Package:** Neo4j.AgentMemory.Enrichment

### Sub-Features

| Sub-Feature | Implementation | Interface | Status | Test(s) |
|-------------|---------------|-----------|--------|---------|
| Nominatim geocoding | `NominatimGeocodingService` | `IGeocodingService` | ✅ Complete | 7 |
| Geocoding cache | `CachedGeocodingService` | `IGeocodingService` (decorator) | ✅ Complete | 4 |
| Rate limiting | `RateLimitedGeocodingService` | `IGeocodingService` (decorator) | ✅ Complete | 4 |

### Test Coverage

| Test Class | Test Method | What It Verifies |
|------------|-------------|-----------------|
| NominatimGeocodingServiceTests | Geocode_ValidLocation_ReturnsResult | Successful geocoding |
| NominatimGeocodingServiceTests | Geocode_EmptyResponse_ReturnsNull | Missing location |
| NominatimGeocodingServiceTests | Geocode_HttpError_ReturnsNull_LogsWarning | HTTP error |
| NominatimGeocodingServiceTests | Geocode_SetsCorrectUserAgent | User-Agent |
| NominatimGeocodingServiceTests | Geocode_SetsCorrectUrl | URL construction |
| NominatimGeocodingServiceTests | Geocode_CancellationToken_Honored | Cancellation |
| NominatimGeocodingServiceTests | Geocode_NullOrWhitespace_ReturnsNull | Empty input |
| CachedGeocodingServiceTests | GetCached_Miss_DelegatesToInner | Cache miss |
| CachedGeocodingServiceTests | GetCached_Hit_ReturnsFromCache | Cache hit |
| CachedGeocodingServiceTests | GetCached_InnerError_NotCached | Error caching |
| CachedGeocodingServiceTests | GetCached_TTL_Respected | Cache expiration |
| RateLimitedGeocodingServiceTests | RateLimit_SingleRequest_PassesThrough | Single request |
| RateLimitedGeocodingServiceTests | RateLimit_BurstRequests_ThrottlesSecondRequest | Rate limiting |
| RateLimitedGeocodingServiceTests | RateLimit_Configurable | Config |
| RateLimitedGeocodingServiceTests | RateLimit_Dispose_DoesNotThrow | Cleanup |

---

## Feature 15: Enrichment

**Value Score:** 55/100
**Description:** Entity enrichment from external knowledge sources. Currently Wikipedia-only via Wikimedia REST API — fetches summary, description, image URL, and Wikipedia link. Includes in-memory caching with configurable TTL (default 24 hours).
**Package:** Neo4j.AgentMemory.Enrichment

### Sub-Features

| Sub-Feature | Implementation | Interface | Status | Test(s) |
|-------------|---------------|-----------|--------|---------|
| Wikipedia enrichment | `WikimediaEnrichmentService` | `IEnrichmentService` | ✅ Complete | 7 |
| Enrichment cache | `CachedEnrichmentService` | `IEnrichmentService` (decorator) | ✅ Complete | 5 |
| DI registration | `ServiceCollectionExtensions.AddEnrichmentServices` | — | ✅ Complete | 0 |

### Test Coverage

| Test Class | Test Method | What It Verifies |
|------------|-------------|-----------------|
| WikimediaEnrichmentServiceTests | Enrich_ValidEntity_ReturnsResult | Successful enrichment |
| WikimediaEnrichmentServiceTests | Enrich_NotFound_ReturnsNull | Missing entity |
| WikimediaEnrichmentServiceTests | Enrich_HttpError_ReturnsNull_LogsWarning | HTTP error |
| WikimediaEnrichmentServiceTests | Enrich_MapsAllFields | Field mapping |
| WikimediaEnrichmentServiceTests | Enrich_SetsCorrectUrl | URL construction |
| WikimediaEnrichmentServiceTests | Enrich_CancellationToken_Honored | Cancellation |
| WikimediaEnrichmentServiceTests | Enrich_NullOrWhitespace_ReturnsNull | Empty input |
| CachedEnrichmentServiceTests | EnrichEntity_CacheMiss_DelegatesToInner | Cache miss |
| CachedEnrichmentServiceTests | EnrichEntity_CacheHit_ReturnsFromCacheWithoutCallingInner | Cache hit |
| CachedEnrichmentServiceTests | EnrichEntity_NullResult_NotCached | Null caching |
| CachedEnrichmentServiceTests | EnrichEntity_DifferentEntityTypes_CachedSeparately | Type isolation |
| CachedEnrichmentServiceTests | EnrichEntity_KeyNormalization_CaseInsensitiveHit | Case normalization |

---

## Feature 16: Configuration

**Value Score:** 85/100
**Description:** Hierarchical configuration via `IOptions<T>` pattern. Root `MemoryOptions` contains sub-options for all memory layers. Each package has its own `ServiceCollectionExtensions` for DI registration. Supports full customization of thresholds, limits, strategies, and feature flags.
**Package:** Neo4j.AgentMemory.Abstractions + all packages

### Sub-Features

| Sub-Feature | Implementation | Interface | Status | Test(s) |
|-------------|---------------|-----------|--------|---------|
| MemoryOptions (root) | `MemoryOptions` record | `IOptions<MemoryOptions>` | ✅ Complete | 0 |
| ShortTermMemoryOptions | `ShortTermMemoryOptions` record | `IOptions<>` | ✅ Complete | 0 |
| LongTermMemoryOptions | `LongTermMemoryOptions` record | `IOptions<>` | ✅ Complete | 0 |
| ReasoningMemoryOptions | `ReasoningMemoryOptions` record | `IOptions<>` | ✅ Complete | 0 |
| RecallOptions | `RecallOptions` record | `IOptions<>` | ✅ Complete | 0 |
| DI Registration | `AddAgentMemoryCore`, `AddNeo4jAgentMemory`, etc. | `IServiceCollection` | ✅ Complete | 6 (McpServerOptions) |

### Test Coverage

| Test Class | Test Method | What It Verifies |
|------------|-------------|-----------------|
| McpServerOptionsTests | DefaultServerName_IsNeo4jAgentMemory | Default server name |
| McpServerOptionsTests | DefaultServerVersion_Is100 | Default version |
| McpServerOptionsTests | DefaultEnableGraphQuery_IsFalse | Feature gate default |
| McpServerOptionsTests | DefaultSessionId_IsDefault | Session default |
| McpServerOptionsTests | DefaultConfidence_Is09 | Confidence default |
| McpServerOptionsTests | Properties_CanBeOverridden | Override capability |

---

## Feature 17: Cross-Memory Relationships

**Value Score:** 80/100
**Description:** Graph relationships connecting all memory layers. These relationships enable traversal-based context assembly and full provenance tracking — you can trace from a fact back to the message it was extracted from, to the entities it's about, to similar entities via SAME_AS.
**Package:** Neo4j.AgentMemory.Neo4j (repositories)

### Sub-Features

| Sub-Feature | Implementation | Interface | Status | Test(s) |
|-------------|---------------|-----------|--------|---------|
| EXTRACTED_FROM | Entity/Fact/Preference repos (with confidence, start_pos, end_pos, context, created_at) | Multiple repos | ✅ Complete | 6 |
| MENTIONS | `Neo4jEntityRepository.AddMentionAsync/BatchAsync` (with confidence, start_pos, end_pos) | `IEntityRepository` | ✅ Complete | 4 |
| SAME_AS | `Neo4jEntityRepository.AddSameAsRelationshipAsync` (with status, updated_at) | `IEntityRepository` | ✅ Complete | 3 |
| ABOUT | Fact/Preference repos | `IFactRepository`, `IPreferenceRepository` | ✅ Complete | 2 |
| HAS_FACT | `Neo4jFactRepository.CreateConversationFactRelationshipAsync` | `IFactRepository` | ✅ Complete | 0 |
| HAS_PREFERENCE | `Neo4jPreferenceRepository.CreateConversationPreferenceRelationshipAsync` | `IPreferenceRepository` | ✅ Complete | 0 |
| HAS_TRACE / IN_SESSION | `Neo4jReasoningTraceRepository` | `IReasoningTraceRepository` | ✅ Complete | 3 |
| TRIGGERED_BY | `Neo4jToolCallRepository.CreateTriggeredByRelationshipAsync` | `IToolCallRepository` | ✅ Complete | 2 |
| EXTRACTED_BY | `Neo4jExtractorRepository.CreateExtractedByRelationshipAsync` (with confidence, extraction_time_ms) | `IExtractorRepository` | ✅ Complete (P1 Sprint) | 2+ |

### Test Coverage

| Test Class | Test Method | What It Verifies |
|------------|-------------|-----------------|
| Neo4jEntityRepositoryBatchTests | CreateExtractedFromRelationshipAsync_SendsCorrectCypher | EXTRACTED_FROM Cypher |
| Neo4jEntityRepositoryBatchTests | CreateExtractedFromRelationshipAsync_PassesCorrectParameters | Parameters |
| Neo4jEntityRepositoryExtensionsTests | AddMentionAsync_SendsCorrectCypher | MENTIONS Cypher |
| Neo4jEntityRepositoryExtensionsTests | AddMentionAsync_PassesCorrectParameters | Parameters |
| Neo4jEntityRepositoryExtensionsTests | AddMentionsBatchAsync_SendsCorrectCypher | Batch MENTIONS |
| Neo4jEntityRepositoryExtensionsTests | AddMentionsBatchAsync_PassesEntityIdsList | Batch parameters |
| Neo4jEntityRepositoryExtensionsTests | AddSameAsRelationshipAsync_SendsCorrectCypher | SAME_AS Cypher |
| Neo4jEntityRepositoryExtensionsTests | AddSameAsRelationshipAsync_PassesAllParameters | Confidence/type |
| Neo4jEntityRepositoryExtensionsTests | GetSameAsEntitiesAsync_SendsCorrectCypher | SAME_AS query |
| Neo4jEntityRepositoryExtensionsTests | MergeEntitiesAsync_SendsCorrectCypher | Merge Cypher |
| Neo4jEntityRepositoryExtensionsTests | MergeEntitiesAsync_TransfersSameAsRelationships | Relationship transfer |
| Neo4jFactRepositoryTests | CreateAboutRelationshipAsync_SendsCorrectCypher | ABOUT Cypher |
| Neo4jFactRepositoryTests | CreateExtractedFromRelationshipAsync_SendsCorrectCypher | EXTRACTED_FROM |
| Neo4jPreferenceRepositoryTests | CreateAboutRelationshipAsync_SendsCorrectCypher | ABOUT |
| Neo4jPreferenceRepositoryTests | CreateExtractedFromRelationshipAsync_SendsCorrectCypher | EXTRACTED_FROM |
| Neo4jReasoningTraceRepositoryTests | CreateInitiatedByRelationshipAsync_SendsCorrectCypher | INITIATED_BY |
| Neo4jReasoningTraceRepositoryTests | CreateConversationTraceRelationshipsAsync_SendsHasTraceCypher | HAS_TRACE |
| Neo4jReasoningTraceRepositoryTests | CreateConversationTraceRelationshipsAsync_SendsInSessionCypher | IN_SESSION |
| Neo4jToolCallRepositoryTests | CreateTriggeredByRelationshipAsync_SendsCorrectCypher | TRIGGERED_BY |
| Neo4jToolCallRepositoryTests | CreateTriggeredByRelationshipAsync_PassesCorrectParameters | Parameters |

---

## Feature 18: Context Assembly

**Value Score:** 90/100
**Description:** The "recall brain" — assembles optimal context from all memory layers for an agent run. Retrieves recent messages, semantically relevant messages, entities, facts, preferences, reasoning traces, and GraphRAG context in parallel. Applies configurable token/character budgets with four truncation strategies (OldestFirst, LowestScoreFirst, Proportional, Fail).
**Package:** Neo4j.AgentMemory.Core

### Sub-Features

| Sub-Feature | Implementation | Interface | Status | Test(s) |
|-------------|---------------|-----------|--------|---------|
| Multi-layer retrieval | `MemoryContextAssembler.AssembleContextAsync` | `IMemoryContextAssembler` | ✅ Complete | 3 |
| Token budgeting | Truncation via `ContextBudget` | `ContextBudget` | ✅ Complete | 3 |
| GraphRAG blending | Optional `IGraphRagContextSource` | `IGraphRagContextSource` | ✅ Complete | 3 |
| MemoryService facade | `MemoryService.RecallAsync` | `IMemoryService` | ✅ Complete | 1 |

### Test Coverage

| Test Class | Test Method | What It Verifies |
|------------|-------------|-----------------|
| MemoryContextAssemblerTests | AssembleContextAsync_GeneratesEmbeddingWhenNotProvided | Auto-embedding |
| MemoryContextAssemblerTests | AssembleContextAsync_UsesProvidedEmbedding | Pre-computed embedding |
| MemoryContextAssemblerTests | AssembleContextAsync_RetrievesFromAllMemoryLayers | Multi-layer retrieval |
| MemoryContextAssemblerTests | AssembleContextAsync_SkipsGraphRagWhenDisabled | Feature toggle |
| MemoryContextAssemblerTests | AssembleContextAsync_SkipsGraphRagWhenSourceIsNull | Null safety |
| MemoryContextAssemblerTests | AssembleContextAsync_IncludesGraphRagWhenEnabled | GraphRAG inclusion |
| MemoryContextAssemblerTests | AssembleContextAsync_SetsAssembledTimestamp | Timestamp |
| MemoryContextAssemblerTests | AssembleContextAsync_EnforcesBudgetOldestFirst | OldestFirst truncation |
| MemoryContextAssemblerTests | AssembleContextAsync_EnforcesBudgetLowestScoreFirst | LowestScoreFirst truncation |
| MemoryContextAssemblerTests | AssembleContextAsync_ReportsEstimatedTokenCount | Token estimation |

---

## Feature 19: Entity Validation

**Value Score:** 70/100
**Description:** Static validation utility filtering extracted entities before persistence. Rejects entities with names that are too short, numeric-only, punctuation-only, or common stop words (221 words ported from Python reference). All rules are independently configurable.
**Package:** Neo4j.AgentMemory.Core

### Sub-Features

| Sub-Feature | Implementation | Interface | Status | Test(s) |
|-------------|---------------|-----------|--------|---------|
| Minimum length check | `EntityValidator.IsValid` | — (static) | ✅ Complete | 2 |
| Numeric-only rejection | `EntityValidator.IsValid` | — (static) | ✅ Complete | 2 |
| Punctuation-only rejection | `EntityValidator.IsValid` | — (static) | ✅ Complete | 2 |
| Stopword filtering | `EntityValidator.IsValid` | — (static) | ✅ Complete | 2 |

### Test Coverage

| Test Class | Test Method | What It Verifies |
|------------|-------------|-----------------|
| EntityValidatorTests | IsValid_StopwordName_ReturnsFalse | Stopword rejection (Theory: 10 words) |
| EntityValidatorTests | IsValid_StopwordIsCaseInsensitive | Case-insensitive stopwords |
| EntityValidatorTests | IsValid_NameShorterThanMinLength_ReturnsFalse | Min length |
| EntityValidatorTests | IsValid_EmptyName_ReturnsFalse | Empty name |
| EntityValidatorTests | IsValid_SingleCharName_ReturnsFalse | Single char |
| EntityValidatorTests | IsValid_NumericOnlyName_ReturnsFalse | Numeric rejection (Theory: 4 values) |
| EntityValidatorTests | IsValid_NumericCheckDisabled_AllowsNumericOnly | Config toggle |
| EntityValidatorTests | IsValid_PunctuationOnlyName_ReturnsFalse | Punctuation rejection (Theory: 4 values) |
| EntityValidatorTests | IsValid_PunctuationCheckDisabled_AllowsPunctuationOnly | Config toggle |
| EntityValidatorTests | IsValid_ValidName_ReturnsTrue | Valid names (Theory: 5 names) |
| EntityValidatorTests | IsValid_StopwordFilterDisabled_AllowsStopword | Config toggle |
| EntityValidatorTests | ValidateEntities_FiltersOutInvalidEntities | Bulk validation |
| EntityValidatorTests | ValidateEntities_EmptyList_ReturnsEmpty | Empty list |
| EntityValidatorTests | ValidateEntities_AllValid_ReturnsAll | All valid |

---

## Feature 20: Infrastructure Stubs

**Value Score:** 60/100
**Description:** Phase 1 placeholder implementations enabling full memory testing without AI dependencies. Stub extractors return empty results, stub embedding provider returns deterministic hash-based vectors, stub pipeline orchestrates stubs. GUID generator and system clock provide testable infrastructure abstractions.
**Package:** Neo4j.AgentMemory.Core

### Sub-Features

| Sub-Feature | Implementation | Interface | Status | Test(s) |
|-------------|---------------|-----------|--------|---------|
| Stub embedding | `StubEmbeddingProvider` | `IEmbeddingProvider` | ✅ Complete | 7 |
| Stub extractors (4) | `StubEntityExtractor`, etc. | `IEntityExtractor`, etc. | ✅ Complete | 0 (via pipeline) |
| Stub pipeline | `StubExtractionPipeline` | `IMemoryExtractionPipeline` | ✅ Complete | 7 |
| GUID generator | `GuidIdGenerator` | `IIdGenerator` | ✅ Complete | 4 |
| System clock | `SystemClock` | `IClock` | ✅ Complete | 3 |

### Test Coverage

| Test Class | Test Method | What It Verifies |
|------------|-------------|-----------------|
| GuidIdGeneratorTests | GenerateId_ReturnsNonEmptyString | Non-empty ID |
| GuidIdGeneratorTests | GenerateId_ReturnsUniqueValues | 100 unique IDs |
| GuidIdGeneratorTests | GenerateId_HasNoHyphens | No hyphens |
| GuidIdGeneratorTests | GenerateId_Has32Characters | 32 char length |
| StubEmbeddingProviderTests | (7 tests) | Dimension matching, determinism, batch, config |
| StubExtractionPipelineTests | ExtractAsync_ReturnsEmptyEntities | Empty entities |
| StubExtractionPipelineTests | ExtractAsync_ReturnsEmptyFacts | Empty facts |
| StubExtractionPipelineTests | ExtractAsync_ReturnsEmptyPreferences | Empty preferences |
| StubExtractionPipelineTests | ExtractAsync_ReturnsEmptyRelationships | Empty relationships |
| StubExtractionPipelineTests | ExtractAsync_PopulatesSourceMessageIds | Source tracking |
| StubExtractionPipelineTests | ExtractAsync_RespectsExtractionTypeFlags | Flag respect |
| StubExtractionPipelineTests | ExtractAsync_MetadataContainsStubFlag | Stub metadata |
| SystemClockTests | UtcNow_ReturnsCurrentTime | Current time |
| SystemClockTests | UtcNow_IsUtc | UTC offset |
| SystemClockTests | UtcNow_Advances | Time advances |

---

## Integration Tests

**Value Score:** N/A (infrastructure)
**Description:** Neo4j Testcontainers-based integration tests verifying real database connectivity and basic CRUD operations. Uses Neo4j 5.26 Docker image.
**Package:** Neo4j.AgentMemory.Tests.Integration

| Test Class | Test Method | What It Verifies |
|------------|-------------|-----------------|
| Neo4jConnectivityTests | CanConnectToNeo4j | Database connectivity via Testcontainers |
| Neo4jConnectivityTests | CanCreateAndQueryNode | Node CRUD operations |

---

## Gap Analysis

> **Updated:** 2026-04-17 by Deckard — Post Gap Closure Sprint (Waves A/B/C)

### Critical Gaps

| # | What's Missing | Why It Matters | Estimated Effort | Priority | Status |
|---|---------------|---------------|:---:|:---:|:---:|
| G1 | **Repository integration tests** | Only 2 connectivity tests exist. No integration tests for any repository (entity, fact, preference, message, etc.). Schema bootstrap, vector search, and complex Cypher queries are untested against real Neo4j. | 3–5 days | **HIGH** | ❌ Open |
| G2 | **Fact deduplication** | Decided omission — Python doesn't implement it either. | N/A | N/A | 🔜 **DECIDED OMISSION** |
| G3 | **Multi-extractor pipeline with merge strategies** | Python has 5 merge strategies. .NET now has `MultiExtractorPipeline` with parallel multi-extractor execution. | N/A | N/A | ✅ **CLOSED** |

### Important Gaps

| # | What's Missing | Why It Matters | Estimated Effort | Priority | Status |
|---|---------------|---------------|:---:|:---:|:---:|
| G4 | **Azure preference extraction** | `AzureLanguageExtraction` has entity, fact, and relationship extractors but no `AzureLanguagePreferenceExtractor`. Users must use LLM extraction for preferences. | 1–2 days | **MEDIUM** | ❌ Open |
| G5 | **Background enrichment queue** | Python has `BackgroundEnrichmentQueue`. .NET now has `BackgroundEnrichmentQueue` (Channel-based hosted service). | N/A | N/A | ✅ **CLOSED** |
| G6 | **MCP resources and prompts** | Python MCP server has 4 resources and 3 prompts. .NET now has 5+ resources and 3 prompts. | N/A | N/A | ✅ **CLOSED (G7/G8/G10/G11)** |
| G7 | **Streaming extraction** | Python has `extraction/streaming.py` for chunked large-document processing. .NET loads all messages into memory. Won't scale for long documents. | 2–3 days | **MEDIUM** | ✅ **CLOSED (Wave 4C)** — Streaming extraction pipeline implemented |
| G8 | **Options validation tests** | Configuration option records (`MemoryOptions`, `RecallOptions`, `LongTermMemoryOptions`, etc.) have no dedicated unit tests for defaults and constraints. | 1 day | **MEDIUM** | ❌ Open |

### Nice-to-Have Gaps

| # | What's Missing | Why It Matters | Estimated Effort | Priority | Status |
|---|---------------|---------------|:---:|:---:|:---:|
| G9 | **Re-embedding after entity merge** | `MergeEntitiesAsync` Cypher doesn't update target entity's embedding. Alias-form queries may miss merged entity. | 0.5 days | **LOW** | ❌ Open |
| G10 | **Entity index refresh hook** | No post-merge hook to re-index canonical entity text for fulltext search. | 0.5 days | **LOW** | ❌ Open |
| G11 | **MCP tool: memory_get_observations** | Python has observation compression tool for token-budget-aware retrieval. Useful for constrained contexts. | 1 day | **LOW** | ✅ **CLOSED** — `ObservationTools.MemoryGetObservations` implemented with `IContextCompressor` |
| G12 | **Diffbot enrichment provider** | Python supports both Wikipedia and Diffbot. .NET is Wikipedia-only. | 1–2 days | **LOW** | ✅ **CLOSED (Wave 4C)** — Diffbot enrichment provider implemented |
| G13 | **CLI entry point** | Python has `cli/` module for command-line operations. .NET has no CLI. | 2–3 days | **LOW** | 🔜 **DEFERRED** — Not needed for library-first package strategy |
| G14 | **Custom YAML/JSON schema support** | Python supports custom entity schemas via YAML/JSON config. .NET uses hardcoded entity types. | 2–3 days | **LOW** | ✅ **CLOSED (Wave 4C)** — Custom schema support implemented |
| G15 | **POLE+O entity type model** | Python uses `POLEOEntityType` (Person, Object, Location, Event, Organization) as a first-class concept. .NET uses free-form string types. | 1–2 days | **LOW** | ❌ Open |

### Gap Closure Sprint Results (Waves A/B/C)

| Gap | What | Wave | Status |
|-----|------|------|--------|
| G1 (datetime) | Migrated all 7 repos to native `datetime()` via `Neo4jDateTimeHelper` | Wave B | ✅ Closed |
| G2 (Schema indexes) | Added `schema_name_idx` + `schema_version_idx` to SchemaBootstrapper | Wave B | ✅ Closed |
| G3 (Tool.description) | Added description field to domain model + Neo4jToolCallRepository | Wave B | ✅ Closed |
| G4 (SessionIdGenerator) | `ISessionIdGenerator` with 3 strategies + 8 tests | Wave C | ✅ Closed |
| G5 (MetadataFilterBuilder) | 5 operators ($eq, $ne, $contains, $in, $exists) + 13 tests | Wave C | ✅ Closed |
| G7 (MCP camelCase bug) | Fixed Cypher in ConversationListResource + EntityListResource | Wave A | ✅ Closed |
| G8 (MemoryStatus counts) | Added ReasoningTrace count — now returns 6 counts matching Python | Wave A | ✅ Closed |
| G10 (Preferences resource) | Added `memory://preferences` MCP resource with category filter | Wave C | ✅ Closed |
| G11 (Context resource) | Added `memory://context/{session_id}` MCP resource using IMemoryContextAssembler | Wave C | ✅ Closed |

**G6 (fact dedup) was skipped — Python doesn't implement it either.**

### Gap Summary

| Status | Count | Gaps |
|--------|:---:|------|
| ✅ Closed | 12 | G1-G5, G7-G8, G10-G11, G12, G14, (Wave 4C items) |
| 🔜 Deferred/Omitted | 2 | G6 (fact dedup — decided omission), G13 (CLI) |
| ❌ Open | 5 | G1 (repo integration tests), G4 (Azure pref), G8 (options tests), G9, G10 (entity re-index), G11 (observations tool), G15 |

---

## Test Coverage Summary

| Category | Test Files | Test Count | Coverage Assessment |
|----------|:---------:|:----------:|:-------------------:|
| AgentFramework | 6 | ~58 | ✅ Excellent |
| Enrichment | 5 | ~28 | ✅ Excellent |
| Extraction (LLM) | 4 | ~31 | ✅ Excellent |
| Extraction (Azure) | 3 | ~22 | ✅ Good |
| GraphRAG Adapter | 2 | ~19 | ✅ Good |
| Infrastructure | 2 | ~14 | ✅ Good |
| MCP Server | 7 | ~59 | ✅ Excellent |
| Observability | 3 | ~19 | ✅ Good |
| Repositories | 6 | ~37 | ⚠️ Unit only (Cypher verification) |
| Resolution | 4 | ~33 | ✅ Excellent |
| Services | 7 | ~72 | ✅ Excellent |
| Stubs | 4 | ~21 | ✅ Good |
| Validation | 1 | ~14 | ✅ Good |
| Wave 4A/B/C additions | — | ~596 | ✅ Excellent (schema parity, streaming, enrichment, custom schema) |
| Gap Closure Sprint (Waves A/B/C) | — | ~21 | ✅ Good (SessionIdGenerator 8 + MetadataFilterBuilder 13) |
| **Integration** | **1** | **2** | **⚠️ Minimal** |
| **TOTAL** | **55+** | **1058** | **Strong unit, weak integration** |

---

*Document generated from full source analysis of all 10 packages and 55+ test files. Updated 2026-04-17 with Gap Closure Sprint (Waves A/B/C) audit results. 1058 tests pass, 0 failures.*
