# Cypher Query Analysis: Python ↔ .NET 1:1 Mapping

> **Generated from source analysis of both codebases**
> Python source: `Neo4j/agent-memory/src/neo4j_agent_memory/`
> .NET source: `src/Neo4j.AgentMemory.Neo4j/`, `src/Neo4j.AgentMemory.McpServer/`

---

## 1. Executive Summary

### Total Counts

| Codebase | Static Queries | Dynamic/Generated | Grand Total |
|----------|---------------|-------------------|-------------|
| **Python** | 83 constants in `queries.py` | 15 (7 query functions + 8 builder functions) | **98** |
| **.NET** | 101 repository queries | 36 DDL + 5 service/retrieval | **142** |

### Why .NET Has MORE Queries Than Python (142 vs 98)

1. **Decomposed operations**: Where Python uses a single monolithic query (e.g., `CREATE_ENTITY` with embedded location, embedding, and labels), .NET splits these into separate Cypher statements for null-safety. A single Python `CREATE_ENTITY` becomes 4–5 .NET queries (MERGE + SET location + SET embedding + SET labels + EXTRACTED_FROM).

2. **Explicit batch operations**: .NET has dedicated `UpsertBatchAsync` methods for entities and facts that include UNWIND-based merge queries plus separate SET statements for embeddings, dynamic labels, and EXTRACTED_FROM relationships.

3. **Additional retrieval patterns**: .NET adds `SearchByNameAsync` (fuzzy text), `FindByTripleAsync` (case-insensitive fact lookup), `GetBySourceEntityAsync`/`GetByTargetEntityAsync` (directional relationship queries), and `GetRecentBySessionAsync`.

4. **Fulltext indexes and retrievers**: .NET adds 3 fulltext index DDL statements and dedicated `FulltextRetriever` and `HybridRetriever` with BM25 search queries that Python doesn't have.

5. **Explicit delete operations**: .NET has `DeleteAsync` on Entity, Fact, Preference, and Conversation repositories. Python relies on the generic `delete_node_by_id()` in `client.py`.

6. **Additional relationship types**: .NET adds `HAS_FACT`, `HAS_PREFERENCE`, `HAS_TRACE`, `IN_SESSION` relationships not present in Python.

### Why the Earlier "67" Count Was Wrong

The initial audit only counted `const string cypher = ...` declarations in repository files. This methodology missed:
- Inline Cypher strings passed directly to `RunAsync()` without a named constant (e.g., `SET e.embedding = $embedding`)
- Dynamic Cypher built via string interpolation (e.g., label SET clauses, metadata filters)
- Queries in `SchemaBootstrapper.cs` (static arrays of DDL strings)
- Queries in `MigrationRunner.cs`, retrieval classes, and MCP resources

### Why the "207+" Count Exists

The 207+ figure came from counting individual Cypher *keywords* (`MATCH`, `MERGE`, `SET`, `CREATE`, `WHERE`, etc.) across multi-statement queries. A single `UpsertAsync` method that runs MERGE + SET embedding + SET labels generates 3 keyword-level "queries" but is functionally 1 logical operation with 3 Cypher statements.

### Overall Parity Assessment

- **Core parity is strong**: 55 of 98 Python queries have full or equivalent .NET implementations
- **Schema/introspection gap is by design**: 16 Python schema queries use a runtime-editable `Schema` node pattern; .NET uses `SchemaBootstrapper` with static DDL arrays
- **Decided omissions**: ~17 queries (graph export, migration utilities, background enrichment, extraction orchestration)
- **Genuine gaps**: ~10 queries that should be considered for implementation (message deletion, session listing, provenance queries, dedup statistics)

---

## 2. Domain-by-Domain Comparison

### 2.1 Short-Term Memory — Conversations

| # | Python Query | Python Location | .NET Equivalent | .NET Location | Status | Notes |
|---|---|---|---|---|---|---|
| 3 | `CREATE_CONVERSATION` | queries.py:30 | `UpsertAsync` | ConversationRepo:25 | ✅ Full | .NET uses MERGE (idempotent); Python uses CREATE |
| 4 | `GET_CONVERSATION` | queries.py:41 | `GetByIdAsync` | ConversationRepo:65 | ✅ Full | Identical MATCH pattern |
| 5 | `GET_CONVERSATION_BY_SESSION` | queries.py:46 | `GetBySessionAsync` | ConversationRepo:79 | 🔄 Equiv | Python returns most recent single; .NET returns full list |
| 6 | `LIST_CONVERSATIONS` | queries.py:53 | `GetBySessionAsync` | ConversationRepo:79 | ✅ Full | Same result set, both ORDER BY updated_at DESC |
| — | *(no Python equiv)* | — | `DeleteAsync` | ConversationRepo:96 | ➕ Extra | .NET adds explicit conversation delete |

### 2.2 Short-Term Memory — Messages

| # | Python Query | Python Location | .NET Equivalent | .NET Location | Status | Notes |
|---|---|---|---|---|---|---|
| 1 | `GET_LAST_MESSAGE` | queries.py:7 | inline in `AddAsync` | MessageRepo:41-46 | 🔄 Equiv | .NET finds previous msg via ORDER BY timestamp DESC LIMIT 1 |
| 2 | `MIGRATE_MESSAGE_LINKS` | queries.py:14 | — | — | ❌ Missing | Decided omission: migration utility; .NET uses MigrationRunner |
| 7 | `CREATE_MESSAGE` | queries.py:60 | `AddAsync` | MessageRepo:26 | ✅ Full | Both create message + HAS_MESSAGE + linked list |
| 8 | `CREATE_MESSAGES_BATCH` | queries.py:83 | `AddBatchAsync` | MessageRepo:97 | ✅ Full | Both use UNWIND for batch creation |
| 9 | `CREATE_MESSAGE_LINKS` | queries.py:100 | inline in `AddAsync` | MessageRepo:41,77 | 🔄 Equiv | .NET handles FIRST_MESSAGE + NEXT_MESSAGE inline |
| 10 | `UPDATE_MESSAGE_EMBEDDING` | queries.py:138 | inline in `AddAsync` | MessageRepo:72 | 🔄 Equiv | .NET sets embedding at creation time |
| 11 | `GET_MESSAGES_WITHOUT_EMBEDDINGS` | queries.py:144 | — | — | ❌ Missing | Decided omission: .NET sets embeddings eagerly |
| 12 | `GET_CONVERSATION_MESSAGES` | queries.py:151 | `GetByConversationAsync` | MessageRepo:200 | ✅ Full | Identical: MATCH via HAS_MESSAGE, ORDER BY timestamp |
| 13 | `SEARCH_MESSAGES_BY_EMBEDDING` | queries.py:158 | `SearchByVectorAsync` | MessageRepo:253 | ✅ Full | Both use db.index.vector.queryNodes; .NET adds metadata filters |
| 14 | `DELETE_MESSAGE` | queries.py:166 | — | — | ❌ Missing | **Genuine gap**: no individual message delete |
| 15 | `DELETE_MESSAGE_NO_CASCADE` | queries.py:173 | — | — | ❌ Missing | **Genuine gap**: no individual message delete |
| 16 | `LIST_SESSIONS` | queries.py:179 | — | — | ❌ Missing | **Genuine gap**: no session listing with stats |
| — | *(no Python equiv)* | — | `GetByIdAsync` | MessageRepo:184 | ➕ Extra | Individual message retrieval by ID |
| — | *(no Python equiv)* | — | `GetRecentBySessionAsync` | MessageRepo:221 | ➕ Extra | Recent messages for session with limit |
| — | *(no Python equiv)* | — | `DeleteBySessionAsync` | MessageRepo:287 | ➕ Extra | Bulk delete messages for a session |
| — | *(no Python equiv)* | — | AddBatch: SET embeddings | MessageRepo:134 | ➕ Extra | Separate embedding SET in batch path |
| — | *(no Python equiv)* | — | AddBatch: NEXT_MESSAGE chain | MessageRepo:142 | ➕ Extra | Within-batch linked list creation |
| — | *(no Python equiv)* | — | AddBatch: link to existing | MessageRepo:150 | ➕ Extra | Connect batch start to prior last message |
| — | *(no Python equiv)* | — | AddBatch: re-read | MessageRepo:165 | ➕ Extra | Read back created messages in order |

### 2.3 Long-Term Memory — Entities

| # | Python Query | Python Location | .NET Equivalent | .NET Location | Status | Notes |
|---|---|---|---|---|---|---|
| 17 | `CREATE_ENTITY` | queries.py:222 | `UpsertAsync` | EntityRepo:25 | ✅ Full | Both MERGE with ON CREATE/ON MATCH |
| 18 | `GET_ENTITY` | queries.py:242 | `GetByIdAsync` | EntityRepo:117 | ✅ Full | Identical MATCH by id |
| 19 | `GET_ENTITY_BY_NAME` | queries.py:247 | `GetByNameAsync` | EntityRepo:133 | ✅ Full | Both check name and aliases |
| 20 | `SEARCH_ENTITIES_BY_EMBEDDING` | queries.py:254 | `SearchByVectorAsync` | EntityRepo:157 | ✅ Full | Both use vector.queryNodes |
| 21 | `SEARCH_ENTITIES_BY_TYPE` | queries.py:262 | `GetByTypeAsync` | EntityRepo:186 | ✅ Full | Both MATCH on type property |
| 22 | `UPDATE_ENTITY_EMBEDDING` | queries.py:269 | `UpdateEmbeddingAsync` | EntityRepo:599 | ✅ Full | Both MATCH + SET embedding |
| 23 | `GET_ENTITIES_WITHOUT_EMBEDDINGS` | queries.py:275 | `GetPageWithoutEmbeddingAsync` | EntityRepo:575 | ✅ Full | Both WHERE embedding IS NULL |
| 24 | `COUNT_ENTITIES_WITHOUT_EMBEDDINGS` | queries.py:284 | — | — | ❌ Missing | Minor gap: count can be inferred from page result |
| — | *(no Python equiv)* | — | `SearchByNameAsync` | EntityRepo:204 | ➕ Extra | Fuzzy text search using toLower + CONTAINS |
| — | *(no Python equiv)* | — | `AddMentionsBatchAsync` | EntityRepo:240 | ➕ Extra | Batch MENTIONS relationship creation |
| — | *(no Python equiv)* | — | `UpsertBatchAsync` | EntityRepo:299 | ➕ Extra | Batch entity UNWIND + MERGE |
| — | *(no Python equiv)* | — | `RefreshEntitySearchFieldsAsync` | EntityRepo:450 | ➕ Extra | Clean aliases + update timestamp |
| — | *(no Python equiv)* | — | `DeleteAsync` | EntityRepo:608 | ➕ Extra | DETACH DELETE entity with success flag |
| — | *(no Python equiv)* | — | UpsertAsync: SET location | EntityRepo:79 | ➕ Extra | Separate geospatial point SET |
| — | *(no Python equiv)* | — | UpsertAsync: SET embedding | EntityRepo:85 | ➕ Extra | Separate embedding SET (null-safe) |
| — | *(no Python equiv)* | — | UpsertAsync: SET labels | EntityRepo:94 | ➕ Extra | Dynamic POLE+O label SET |
| — | *(no Python equiv)* | — | UpsertAsync: EXTRACTED_FROM | EntityRepo:101 | ➕ Extra | Auto-create provenance links |

### 2.4 Long-Term Memory — Preferences

| # | Python Query | Python Location | .NET Equivalent | .NET Location | Status | Notes |
|---|---|---|---|---|---|---|
| 25 | `CREATE_PREFERENCE` | queries.py:290 | `UpsertAsync` | PreferenceRepo:25 | ✅ Full | .NET uses MERGE (idempotent) |
| 26 | `SEARCH_PREFERENCES_BY_EMBEDDING` | queries.py:304 | `SearchByVectorAsync` | PreferenceRepo:126 | ✅ Full | Both use vector.queryNodes |
| 27 | `SEARCH_PREFERENCES_BY_CATEGORY` | queries.py:312 | `GetByCategoryAsync` | PreferenceRepo:104 | ✅ Full | Both MATCH on category |
| 36 | `LINK_PREFERENCE_TO_ENTITY` | queries.py:418 | `CreateAboutRelationshipAsync` | PreferenceRepo:182 | ✅ Full | Both MERGE ABOUT relationship |
| — | *(no Python equiv)* | — | `GetByIdAsync` | PreferenceRepo:88 | ➕ Extra | Individual preference retrieval |
| — | *(no Python equiv)* | — | `DeleteAsync` | PreferenceRepo:158 | ➕ Extra | DETACH DELETE preference |
| — | *(no Python equiv)* | — | `CreateExtractedFromRelationshipAsync` | PreferenceRepo:169 | ➕ Extra | Manual EXTRACTED_FROM link |
| — | *(no Python equiv)* | — | `CreateConversationPreferenceRelationshipAsync` | PreferenceRepo:195 | ➕ Extra | HAS_PREFERENCE from Conversation |
| — | *(no Python equiv)* | — | `GetPageWithoutEmbeddingAsync` | PreferenceRepo:230 | ➕ Extra | Background embedding management |
| — | *(no Python equiv)* | — | `UpdateEmbeddingAsync` | PreferenceRepo:253 | ➕ Extra | Background embedding update |
| — | *(no Python equiv)* | — | UpsertAsync: SET embedding | PreferenceRepo:64 | ➕ Extra | Separate embedding SET |
| — | *(no Python equiv)* | — | UpsertAsync: EXTRACTED_FROM | PreferenceRepo:72 | ➕ Extra | Auto-create provenance links |

### 2.5 Long-Term Memory — Facts

| # | Python Query | Python Location | .NET Equivalent | .NET Location | Status | Notes |
|---|---|---|---|---|---|---|
| 28 | `CREATE_FACT` | queries.py:319 | `UpsertAsync` | FactRepo:25 | ✅ Full | .NET adds valid_from/valid_until temporal windows |
| 30 | `GET_FACTS_BY_SUBJECT` | queries.py:349 | `GetBySubjectAsync` | FactRepo:190 | ✅ Full | Both MATCH on subject |
| 31 | `SEARCH_FACTS_BY_EMBEDDING` | queries.py:357 | `SearchByVectorAsync` | FactRepo:212 | ✅ Full | Both use vector.queryNodes |
| — | *(no Python equiv)* | — | `GetByIdAsync` | FactRepo:174 | ➕ Extra | Individual fact retrieval |
| — | *(no Python equiv)* | — | `UpsertBatchAsync` | FactRepo:94 | ➕ Extra | Batch fact UNWIND + MERGE |
| — | *(no Python equiv)* | — | `FindByTripleAsync` | FactRepo:360 | ➕ Extra | Case-insensitive triple lookup |
| — | *(no Python equiv)* | — | `CreateAboutRelationshipAsync` | FactRepo:256 | ➕ Extra | ABOUT relationship to entity |
| — | *(no Python equiv)* | — | `CreateConversationFactRelationshipAsync` | FactRepo:269 | ➕ Extra | HAS_FACT from Conversation |
| — | *(no Python equiv)* | — | `DeleteAsync` | FactRepo:343 | ➕ Extra | DETACH DELETE with success flag |
| — | *(no Python equiv)* | — | `GetPageWithoutEmbeddingAsync` | FactRepo:310 | ➕ Extra | Background embedding management |
| — | *(no Python equiv)* | — | `UpdateEmbeddingAsync` | FactRepo:333 | ➕ Extra | Background embedding update |
| — | *(no Python equiv)* | — | `CreateExtractedFromRelationshipAsync` | FactRepo:243 | ➕ Extra | Manual EXTRACTED_FROM link |

### 2.6 Long-Term Memory — Entity Relationships

| # | Python Query | Python Location | .NET Equivalent | .NET Location | Status | Notes |
|---|---|---|---|---|---|---|
| 29 | `CREATE_ENTITY_RELATIONSHIP` | queries.py:335 | `UpsertAsync` | RelationshipRepo:26 | ✅ Full | Both MERGE RELATED_TO |
| 32 | `GET_ENTITY_RELATIONSHIPS` | queries.py:365 | `GetByEntityAsync` | RelationshipRepo:99 | ✅ Full | Both match bidirectionally |
| 33 | `LINK_MESSAGE_TO_ENTITY` | queries.py:370 | `AddMentionAsync` | EntityRepo:224 | ✅ Full | Both MERGE MENTIONS relationship |
| 34 | `CREATE_ENTITY_RELATION_BY_NAME` | queries.py:383 | — | — | ❌ Missing | Decided omission: .NET uses IDs for type safety |
| 35 | `CREATE_ENTITY_RELATION_BY_ID` | queries.py:404 | `UpsertAsync` | RelationshipRepo:26 | ✅ Full | Same MERGE by entity IDs |
| — | *(no Python equiv)* | — | `GetByIdAsync` | RelationshipRepo:84 | ➕ Extra | Relationship retrieval by ID |
| — | *(no Python equiv)* | — | `GetBySourceEntityAsync` | RelationshipRepo:116 | ➕ Extra | Outgoing relationships only |
| — | *(no Python equiv)* | — | `GetByTargetEntityAsync` | RelationshipRepo:130 | ➕ Extra | Incoming relationships only |

### 2.7 Reasoning Memory — Traces

| # | Python Query | Python Location | .NET Equivalent | .NET Location | Status | Notes |
|---|---|---|---|---|---|---|
| 37 | `CREATE_REASONING_TRACE` | queries.py:429 | `AddAsync` | TraceRepo:25 | ✅ Full | Both CREATE with task metadata |
| 38 | `UPDATE_REASONING_TRACE` | queries.py:444 | `UpdateAsync` | TraceRepo:60 | ✅ Full | Both MATCH + SET outcome/success |
| 44 | `SEARCH_TRACES_BY_EMBEDDING` | queries.py:553 | `SearchByTaskVectorAsync` | TraceRepo:140 | ✅ Full | Both use vector.queryNodes with success filter |
| 45 | `GET_TRACE_WITH_STEPS` | queries.py:561 | `GetByIdAsync` + `GetByTraceAsync` | TraceRepo:93 + StepRepo:72 | 🔄 Equiv | .NET decomposes into 2 queries |
| 46 | `LIST_TRACES` | queries.py:570 | `ListBySessionAsync` | TraceRepo:109 | ✅ Full | Both filter by session, ORDER BY started_at DESC |
| 47 | `LINK_CONVERSATION_TO_TRACE` | queries.py:596 | `CreateConversationTraceRelationshipsAsync` | TraceRepo:187 | ✅ Full | .NET adds IN_SESSION reverse relationship |
| — | *(no Python equiv)* | — | `GetByIdAsync` | TraceRepo:93 | ➕ Extra | Individual trace retrieval |
| — | *(no Python equiv)* | — | AddAsync: SET task_embedding | TraceRepo:47 | ➕ Extra | Separate embedding SET |
| — | *(no Python equiv)* | — | UpdateAsync: SET task_embedding | TraceRepo:80 | ➕ Extra | Separate embedding SET on update |

### 2.8 Reasoning Memory — Steps

| # | Python Query | Python Location | .NET Equivalent | .NET Location | Status | Notes |
|---|---|---|---|---|---|---|
| 39 | `CREATE_REASONING_STEP` | queries.py:452 | `AddAsync` | StepRepo:25 | ✅ Full | Both MATCH trace + CREATE step + HAS_STEP |
| — | *(no Python equiv)* | — | `GetByTraceAsync` | StepRepo:72 | ➕ Extra | Get all steps for a trace (ordered) |
| — | *(no Python equiv)* | — | `GetByIdAsync` | StepRepo:93 | ➕ Extra | Individual step retrieval |
| — | *(no Python equiv)* | — | AddAsync: SET embedding | StepRepo:59 | ➕ Extra | Separate step embedding SET |

### 2.9 Reasoning Memory — Tool Calls

| # | Python Query | Python Location | .NET Equivalent | .NET Location | Status | Notes |
|---|---|---|---|---|---|---|
| 40 | `CREATE_TOOL_CALL` | queries.py:468 | `AddAsync` | ToolCallRepo:25 | ✅ Full | Both create ToolCall + Tool node with stats |
| 41 | `GET_TOOL_STATS` | queries.py:499 | — | — | ❌ Missing | Decided omission: stats maintained inline in AddAsync |
| 42 | `GET_TOOL_STATS_COMPUTED` | queries.py:522 | — | — | ❌ Missing | Decided omission: fallback not needed |
| 43 | `MIGRATE_TOOL_STATS` | queries.py:538 | — | — | ❌ Missing | Decided omission: migration utility |
| — | *(no Python equiv)* | — | `UpdateAsync` | ToolCallRepo:78 | ➕ Extra | Update tool call result/status |
| — | *(no Python equiv)* | — | `GetByStepAsync` | ToolCallRepo:103 | ➕ Extra | Get tool calls for a step |
| — | *(no Python equiv)* | — | `GetByIdAsync` | ToolCallRepo:120 | ➕ Extra | Individual tool call retrieval |
| — | *(no Python equiv)* | — | AddAsync: INSTANCE_OF + Tool stats | ToolCallRepo:50 | ➕ Extra | Separate Tool node management |

### 2.10 Cross-Memory Linking

| # | Python Query | Python Location | .NET Equivalent | .NET Location | Status | Notes |
|---|---|---|---|---|---|---|
| 48 | `LINK_TRACE_TO_MESSAGE` | queries.py:603 | `CreateInitiatedByRelationshipAsync` | TraceRepo:174 | ✅ Full | Both MERGE INITIATED_BY |
| 49 | `LINK_TOOL_CALL_TO_MESSAGE` | queries.py:610 | `CreateTriggeredByRelationshipAsync` | ToolCallRepo:177 | ✅ Full | Both MERGE TRIGGERED_BY |
| 50 | `GET_SESSION_CONTEXT` | queries.py:617 | MemoryContextAssembler | Core/Services | 🔄 Equiv | .NET decomposes into per-repo searches |

### 2.11 Utility & Statistics

| # | Python Query | Python Location | .NET Equivalent | .NET Location | Status | Notes |
|---|---|---|---|---|---|---|
| 51 | `DELETE_SESSION_DATA` | queries.py:631 | `DeleteBySessionAsync` | MessageRepo:287 | 🔶 Partial | Python deletes conversations+messages+traces; .NET only messages |
| 52 | `GET_MEMORY_STATS` | queries.py:640 | `GetMemoryStatus` | MemoryStatusResource:20 | ✅ Full | Both count all node types via OPTIONAL MATCH chain |

### 2.12 Graph Export

| # | Python Query | Python Location | .NET Equivalent | .NET Location | Status | Notes |
|---|---|---|---|---|---|---|
| 53 | `GET_GRAPH_SHORT_TERM` | queries.py:654 | — | — | ❌ Missing | Decided omission: visualization feature |
| 54 | `GET_GRAPH_LONG_TERM` | queries.py:667 | — | — | ❌ Missing | Decided omission: visualization feature |
| 55 | `GET_GRAPH_REASONING` | queries.py:682 | — | — | ❌ Missing | Decided omission: visualization feature |
| 56 | `GET_GRAPH_ALL` | queries.py:699 | — | — | ❌ Missing | Decided omission: visualization feature |

### 2.13 Geospatial

| # | Python Query | Python Location | .NET Equivalent | .NET Location | Status | Notes |
|---|---|---|---|---|---|---|
| 57 | `UPDATE_ENTITY_LOCATION` | queries.py:725 | inline in `UpsertAsync` | EntityRepo:79 | 🔄 Equiv | .NET sets point during entity upsert |
| 58 | `GET_LOCATIONS_WITHOUT_COORDINATES` | queries.py:731 | — | — | ❌ Missing | Decided omission: no background geocoding |
| 59 | `SEARCH_LOCATIONS_NEAR` | queries.py:738 | `SearchByLocationAsync` | EntityRepo:510 | ✅ Full | Both use point.distance |
| 60 | `SEARCH_LOCATIONS_IN_BOUNDING_BOX` | queries.py:748 | `SearchInBoundingBoxAsync` | EntityRepo:547 | ✅ Full | Both use point.withinBBox |
| 61 | `GET_LOCATION_COORDINATES` | queries.py:761 | part of `GetByIdAsync` | EntityRepo:117 | 🔄 Equiv | .NET returns lat/lon as entity properties |

### 2.14 Provenance Tracking

| # | Python Query | Python Location | .NET Equivalent | .NET Location | Status | Notes |
|---|---|---|---|---|---|---|
| 62 | `CREATE_EXTRACTOR` | queries.py:772 | `UpsertAsync` | ExtractorRepo:31 | ✅ Full | Both MERGE on name |
| 63 | `CREATE_EXTRACTED_FROM_RELATIONSHIP` | queries.py:786 | `CreateExtractedFromRelationshipAsync` | EntityRepo:395 | ✅ Full | Both MERGE EXTRACTED_FROM with metadata |
| 64 | `CREATE_EXTRACTED_BY_RELATIONSHIP` | queries.py:802 | `CreateExtractedByRelationshipAsync` | ExtractorRepo:94 | ✅ Full | Both MERGE EXTRACTED_BY |
| 65 | `GET_ENTITY_PROVENANCE` | queries.py:814 | — | — | ❌ Missing | **Genuine gap**: no provenance chain query |
| 66 | `GET_ENTITIES_FROM_MESSAGE` | queries.py:824 | — | — | ❌ Missing | **Genuine gap**: no inverse extraction query |
| 67 | `GET_ENTITIES_BY_EXTRACTOR` | queries.py:831 | `GetEntitiesByExtractorAsync` | ExtractorRepo:121 | ✅ Full | Both query via EXTRACTED_BY relationship |
| 68 | `GET_EXTRACTION_STATS` | queries.py:839 | — | — | ❌ Missing | **Genuine gap**: no extraction statistics |
| 69 | `GET_EXTRACTOR_STATS` | queries.py:850 | — | — | ❌ Missing | **Genuine gap**: no per-extractor statistics |
| 70 | `LIST_EXTRACTORS` | queries.py:861 | `ListAsync` | ExtractorRepo:74 | ✅ Full | Both list all extractors |
| 71 | `DELETE_ENTITY_PROVENANCE` | queries.py:869 | — | — | ❌ Missing | **Genuine gap**: no provenance cleanup |

### 2.15 Entity Deduplication

| # | Python Query | Python Location | .NET Equivalent | .NET Location | Status | Notes |
|---|---|---|---|---|---|---|
| 72 | `FIND_SIMILAR_ENTITIES_BY_EMBEDDING` | queries.py:882 | — | — | ❌ Missing | **Genuine gap**: no dedup candidate search |
| 73 | `CREATE_SAME_AS_RELATIONSHIP` | queries.py:891 | `AddSameAsRelationshipAsync` | EntityRepo:258 | ✅ Full | .NET uses MERGE (idempotent); Python uses CREATE |
| 74 | `GET_POTENTIAL_DUPLICATES` | queries.py:905 | — | — | ❌ Missing | **Genuine gap**: no pending review query |
| 75 | `GET_SAME_AS_CLUSTER` | queries.py:914 | `GetSameAsEntitiesAsync` | EntityRepo:275 | 🔄 Equiv | .NET returns direct neighbors; Python traverses full cluster |
| 76 | `MERGE_ENTITIES` | queries.py:923 | `MergeEntitiesAsync` | EntityRepo:408 | ✅ Full | Both use CALL subqueries to transfer relationships |
| 77 | `GET_ENTITIES_WITH_EMBEDDINGS` | queries.py:957 | — | — | ❌ Missing | Can use SearchByVectorAsync with self-search |
| 78 | `UPDATE_SAME_AS_STATUS` | queries.py:966 | part of `AddSameAsRelationshipAsync` | EntityRepo:258 | 🔄 Equiv | .NET handles via MERGE ON MATCH |
| 79 | `GET_DEDUPLICATION_STATS` | queries.py:973 | — | — | ❌ Missing | **Genuine gap**: no dedup monitoring |

### 2.16 Schema Persistence

| # | Python Query | Python Location | .NET Equivalent | .NET Location | Status | Notes |
|---|---|---|---|---|---|---|
| 80 | `CREATE_SCHEMA` | queries.py:988 | — | — | ❌ Missing | Decided omission: .NET uses SchemaBootstrapper |
| 81 | `GET_SCHEMA_BY_NAME` | queries.py:1002 | — | — | ❌ Missing | Decided omission: no schema nodes |
| 82 | `GET_SCHEMA_BY_NAME_VERSION` | queries.py:1010 | — | — | ❌ Missing | Decided omission |
| 83 | `GET_SCHEMA_BY_ID` | queries.py:1016 | — | — | ❌ Missing | Decided omission |
| 84 | `LIST_SCHEMAS` | queries.py:1021 | — | — | ❌ Missing | Decided omission |
| 85 | `LIST_SCHEMA_VERSIONS` | queries.py:1030 | — | — | ❌ Missing | Decided omission |
| 86 | `UPDATE_SCHEMA_ACTIVE` | queries.py:1036 | — | — | ❌ Missing | Decided omission |
| 87 | `DELETE_SCHEMA` | queries.py:1045 | — | — | ❌ Missing | Decided omission |
| 88 | `DELETE_SCHEMA_BY_NAME` | queries.py:1051 | — | — | ❌ Missing | Decided omission |
| 89 | `DEACTIVATE_SCHEMA_VERSIONS` | queries.py:1057 | — | — | ❌ Missing | Decided omission |
| 90 | `CREATE_SCHEMA_NAME_INDEX` | queries.py:1064 | SchemaBootstrapper | SchemaBootstrapper:48 | 🔄 Equiv | Static index creation |
| 91 | `CREATE_SCHEMA_ID_INDEX` | queries.py:1070 | SchemaBootstrapper | SchemaBootstrapper:49 | 🔄 Equiv | Static index creation |

> **Architecture note**: Python stores schema configuration as `(:Schema)` nodes in the graph, allowing runtime editing. .NET uses `SchemaBootstrapper.cs` with hardcoded DDL arrays executed at startup. This is a deliberate architectural difference, not a missing feature.

### 2.17 Schema Introspection

| # | Python Query | Python Location | .NET Equivalent | .NET Location | Status | Notes |
|---|---|---|---|---|---|---|
| 92 | `SHOW_CONSTRAINTS` | queries.py:1190 | — | — | ❌ Missing | .NET has no constraint inspection |
| 93 | `SHOW_INDEXES` | queries.py:1191 | — | — | ❌ Missing | .NET has no index inspection |
| 94 | `SHOW_CONSTRAINTS_DETAIL` | queries.py:1192 | — | — | ❌ Missing | .NET has no constraint detail |
| 95 | `SHOW_INDEXES_DETAIL` | queries.py:1193 | — | — | ❌ Missing | .NET has no index detail |

> **Note**: .NET's `SchemaInfoResource` provides different introspection via `db.labels()`, `db.relationshipTypes()`, and `db.propertyKeys()` — these are .NET extras, not equivalents.

### 2.18 Entity Extraction Support

| # | Python Query | Python Location | .NET Equivalent | .NET Location | Status | Notes |
|---|---|---|---|---|---|---|
| 96 | `GET_MESSAGES_FOR_ENTITY_EXTRACTION` | queries.py:1200 | — | — | ❌ Missing | Decided omission: extraction orchestration not in .NET |
| 97 | `GET_ALL_MESSAGES_FOR_SESSION` | queries.py:1208 | `GetRecentBySessionAsync` | MessageRepo:221 | 🔄 Equiv | .NET returns recent N; Python returns all |
| 98 | `GET_SUMMARY_ENTITIES` | queries.py:1215 | — | — | ❌ Missing | Decided omission: summarization not in .NET |

### 2.19 Dynamic Queries

| # | Python Function | Python Location | .NET Equivalent | .NET Location | Status | Notes |
|---|---|---|---|---|---|---|
| D1 | `build_metadata_search_query()` | queries.py:1237 | `MetadataFilterBuilder` | Queries/MetadataFilterBuilder.cs | ✅ Full | .NET adds $ne, $contains, $in, $exists operators |
| D2 | `create_constraint_query()` | queries.py:1084 | Constraints array | SchemaBootstrapper:13 | 🔄 Equiv | .NET uses static array; Python generates dynamically |
| D3 | `create_index_query()` | queries.py:1102 | PropertyIndexes array | SchemaBootstrapper:34 | 🔄 Equiv | Same approach difference |
| D4 | `create_vector_index_query()` | queries.py:1120 | `BuildVectorIndexes` | SchemaBootstrapper:99 | ✅ Full | Both generate based on dimensions parameter |
| D5 | `create_point_index_query()` | queries.py:1147 | PropertyIndexes array | SchemaBootstrapper:50 | 🔄 Equiv | Static in .NET (entity_location_idx) |
| D6 | `drop_constraint_query()` | queries.py:1165 | — | — | ❌ Missing | Decided omission: .NET uses IF NOT EXISTS |
| D7 | `drop_index_query()` | queries.py:1177 | — | — | ❌ Missing | Decided omission: .NET uses IF NOT EXISTS |
| D8 | `build_label_set_clause()` | query_builder.py:207 | `BuildDynamicLabels` | EntityRepo:635 | ✅ Full | Both validate and generate label SET clauses |
| D9 | `build_create_entity_query()` | query_builder.py:237 | `UpsertAsync` | EntityRepo:25 | ✅ Full | .NET uses decomposed approach |
| D10 | `to_pascal_case()` | query_builder.py:76 | `SanitizeLabel` | EntityRepo:650 | 🔄 Equiv | Different normalization logic |
| D11 | `sanitize_label()` | query_builder.py:107 | `SanitizeLabel` | EntityRepo:650 | ✅ Full | Both validate for Neo4j label safety |
| D12 | `is_poleo_type()` | query_builder.py:134 | `ValidEntityLabels` | EntityRepo:627 | ✅ Full | Both validate against POLE+O types |
| D13 | `is_poleo_subtype()` | query_builder.py:146 | `ValidEntityLabels` | EntityRepo:627 | ✅ Full | Same set covers subtypes |
| D14 | `validate_entity_type()` | query_builder.py:162 | `BuildDynamicLabels` | EntityRepo:635 | 🔄 Equiv | Validation built into label builder |
| D15 | `validate_subtype()` | query_builder.py:177 | `BuildDynamicLabels` | EntityRepo:635 | 🔄 Equiv | Validation built into label builder |

### 2.20 Python Inline Queries

| # | Python Query | Python Location | .NET Equivalent | .NET Location | Status | Notes |
|---|---|---|---|---|---|---|
| I1 | `vector_search()` | client.py:182 | VectorRetriever + FulltextRetriever | Retrieval/Internal/ | ✅ Full | .NET adds fulltext + hybrid modes |
| I2 | `get_node_by_id()` | client.py:217 | per-repo `GetByIdAsync` | each repo | ✅ Full | .NET distributes to typed repositories |
| I3 | `delete_node_by_id()` | client.py:243 | per-repo `DeleteAsync` | each repo | ✅ Full | .NET distributes to typed repositories |
| I4 | `add_step()` count | short_term.py:447 | step_number from domain | — | 🔄 Equiv | .NET passes step number explicitly |
| I5 | `add_observation()` | short_term.py:298 | set at step creation | StepRepo:25 | 🔄 Equiv | .NET includes observation in initial CREATE |
| I6 | `search_locations_near()` | long_term.py:1932 | `SearchByLocationAsync` | EntityRepo:510 | ✅ Full | Both use point.distance |
| I7 | `search_locations_in_bounding_box()` | long_term.py:1994 | `SearchInBoundingBoxAsync` | EntityRepo:547 | ✅ Full | Both use point.withinBBox |
| I8 | `_add_alias_to_entity()` | long_term.py:1129 | `RefreshEntitySearchFieldsAsync` | EntityRepo:450 | 🔄 Equiv | .NET cleans + updates aliases |
| I9 | `record_tool_call()` step SET | reasoning.py:566 | step observation in CREATE | StepRepo:25 | 🔄 Equiv | .NET sets observation at creation |
| I10 | `_generate_step_embeddings_batch()` get | reasoning.py:688 | — | — | ❌ Missing | Background embedding generation |
| I11 | `_generate_step_embeddings_batch()` set | reasoning.py:723 | — | — | ❌ Missing | Background embedding generation |
| I12 | background.py enrichment update | background.py:N/A | — | — | ❌ Missing | Decided omission: enrichment pipeline |

---

## 3. Gap Analysis

### Genuine Gaps (should be considered for implementation)

| Python Query | What It Does | Priority | Implementation Effort |
|---|---|---|---|
| `DELETE_MESSAGE` (#14) | Delete individual message + cascade MENTIONS | Medium | Low — add `DeleteAsync(messageId)` to `IMessageRepository` |
| `DELETE_MESSAGE_NO_CASCADE` (#15) | Delete message without cascading relationships | Low | Low — MATCH + DELETE without DETACH |
| `LIST_SESSIONS` (#16) | List all sessions with message counts and preview text | Medium | Medium — aggregate query across Conversations + Messages |
| `GET_ENTITY_PROVENANCE` (#65) | Get complete provenance chain (sources + extractors) | Medium | Low — MATCH/OPTIONAL MATCH pattern |
| `GET_ENTITIES_FROM_MESSAGE` (#66) | Get all entities extracted from a specific message | Medium | Low — reverse EXTRACTED_FROM traversal |
| `GET_EXTRACTION_STATS` (#68) | Overall extraction statistics | Low | Low — count query |
| `GET_EXTRACTOR_STATS` (#69) | Per-extractor statistics with entity counts | Low | Low — aggregate query |
| `DELETE_ENTITY_PROVENANCE` (#71) | Remove provenance links from an entity | Low | Low — MATCH + DELETE relationships |
| `FIND_SIMILAR_ENTITIES_BY_EMBEDDING` (#72) | Find potential duplicate entities by vector similarity | Medium | Low — vector.queryNodes excluding self |
| `GET_POTENTIAL_DUPLICATES` (#74) | Get pending SAME_AS pairs for review | Medium | Low — MATCH on status='pending' |
| `GET_DEDUPLICATION_STATS` (#79) | Merged/pending/rejected counts | Low | Low — aggregate query |

### Decided Omissions (by design, not needed)

| Python Query | Why Omitted |
|---|---|
| `MIGRATE_MESSAGE_LINKS` (#2) | Migration utility; .NET uses `MigrationRunner` with `.cypher` files |
| `GET_MESSAGES_WITHOUT_EMBEDDINGS` (#11) | .NET sets embeddings eagerly at creation time |
| `COUNT_ENTITIES_WITHOUT_EMBEDDINGS` (#24) | Count inferable from `GetPageWithoutEmbeddingAsync` result |
| `CREATE_ENTITY_RELATION_BY_NAME` (#34) | .NET uses ID-based lookups for type safety |
| `GET_TOOL_STATS` / `COMPUTED` / `MIGRATE` (#41-43) | .NET maintains pre-aggregated Tool node stats inline during `AddAsync` |
| `GET_GRAPH_*` (#53-56) | Graph export/visualization not yet needed in .NET |
| `GET_LOCATIONS_WITHOUT_COORDINATES` (#58) | No background geocoding pipeline in .NET |
| `GET_LOCATION_COORDINATES` (#61) | Coordinates returned as entity properties via `GetByIdAsync` |
| Schema CRUD (#80-89) | .NET uses `SchemaBootstrapper` with static arrays instead of graph-stored schemas |
| `SHOW_CONSTRAINTS/INDEXES` (#92-95) | .NET manages schema declaratively; no runtime inspection needed |
| `GET_MESSAGES_FOR_ENTITY_EXTRACTION` (#96) | Extraction orchestration not in .NET repos |
| `GET_SUMMARY_ENTITIES` (#98) | Summarization not implemented in .NET |
| `drop_constraint_query()` / `drop_index_query()` (D6-D7) | .NET uses `IF NOT EXISTS` on all DDL, making drops unnecessary |
| background.py enrichment (I12) | Enrichment pipeline not ported to .NET |

---

## 4. .NET Extras

### Repository Extras (no Python equivalent)

| .NET Query | Location | Why It Exists |
|---|---|---|
| `ConversationRepository.DeleteAsync` | ConversationRepo:96 | Explicit delete; Python uses generic `delete_node_by_id` |
| `MessageRepository.GetByIdAsync` | MessageRepo:184 | Individual message retrieval; Python uses generic `get_node_by_id` |
| `MessageRepository.GetRecentBySessionAsync` | MessageRepo:221 | Session-scoped recent messages with limit |
| `MessageRepository.DeleteBySessionAsync` | MessageRepo:287 | Bulk message cleanup for a session |
| `EntityRepository.SearchByNameAsync` | EntityRepo:204 | Fuzzy text search using toLower + CONTAINS |
| `EntityRepository.AddMentionsBatchAsync` | EntityRepo:240 | Batch MENTIONS creation via UNWIND |
| `EntityRepository.UpsertBatchAsync` | EntityRepo:299 | Batch entity MERGE via UNWIND |
| `EntityRepository.RefreshEntitySearchFieldsAsync` | EntityRepo:450 | Post-merge alias cleanup |
| `EntityRepository.DeleteAsync` | EntityRepo:608 | Explicit entity delete with boolean return |
| `FactRepository.UpsertBatchAsync` | FactRepo:94 | Batch fact MERGE via UNWIND |
| `FactRepository.FindByTripleAsync` | FactRepo:360 | Case-insensitive triple lookup (dedup support) |
| `FactRepository.DeleteAsync` | FactRepo:343 | Explicit fact delete with boolean return |
| `FactRepository.CreateAboutRelationshipAsync` | FactRepo:256 | ABOUT relationship: Fact → Entity |
| `FactRepository.CreateConversationFactRelationshipAsync` | FactRepo:269 | HAS_FACT relationship: Conversation → Fact |
| `PreferenceRepository.GetByIdAsync` | PreferenceRepo:88 | Individual preference retrieval |
| `PreferenceRepository.DeleteAsync` | PreferenceRepo:158 | Explicit preference delete |
| `PreferenceRepository.CreateExtractedFromRelationshipAsync` | PreferenceRepo:169 | Manual EXTRACTED_FROM for preferences |
| `PreferenceRepository.CreateConversationPreferenceRelationshipAsync` | PreferenceRepo:195 | HAS_PREFERENCE: Conversation → Preference |
| `PreferenceRepository.GetPageWithoutEmbeddingAsync` | PreferenceRepo:230 | Background embedding management for preferences |
| `PreferenceRepository.UpdateEmbeddingAsync` | PreferenceRepo:253 | Background embedding update for preferences |
| `FactRepository.GetPageWithoutEmbeddingAsync` | FactRepo:310 | Background embedding management for facts |
| `FactRepository.UpdateEmbeddingAsync` | FactRepo:333 | Background embedding update for facts |
| `RelationshipRepository.GetByIdAsync` | RelationshipRepo:84 | Individual relationship retrieval |
| `RelationshipRepository.GetBySourceEntityAsync` | RelationshipRepo:116 | Outgoing-only relationship query |
| `RelationshipRepository.GetByTargetEntityAsync` | RelationshipRepo:130 | Incoming-only relationship query |
| `ToolCallRepository.UpdateAsync` | ToolCallRepo:78 | Update tool call result/status after execution |
| `ToolCallRepository.GetByStepAsync` | ToolCallRepo:103 | Get tool calls for a reasoning step |
| `ToolCallRepository.GetByIdAsync` | ToolCallRepo:120 | Individual tool call retrieval |

### Infrastructure Extras

| .NET Query | Location | Why It Exists |
|---|---|---|
| 3 fulltext indexes | SchemaBootstrapper:27-31 | BM25 search on messages, entities, facts — Python has no fulltext |
| 15 property indexes (incl. POINT) | SchemaBootstrapper:34-51 | More comprehensive indexing strategy |
| 6 vector indexes | SchemaBootstrapper:99-107 | Same domains as Python but statically defined |
| Migration constraint | MigrationRunner:66 | Migration version tracking |
| Migration check | MigrationRunner:75 | Applied migration detection |
| Migration record | MigrationRunner:90 | Record applied migration |

### Retrieval Extras

| .NET Query | Location | Why It Exists |
|---|---|---|
| VectorRetriever (standard) | VectorRetriever:45 | vector.queryNodes with standard result |
| VectorRetriever (custom) | VectorRetriever:36 | vector.queryNodes with custom retrieval query |
| FulltextRetriever (standard) | FulltextRetriever:47 | fulltext.queryNodes with standard result |
| FulltextRetriever (custom) | FulltextRetriever:38 | fulltext.queryNodes with custom retrieval query |
| GraphRag default traversal | GraphRagContextSource:133 | `MATCH (node)-[:RELATED_TO*1..2]-(related)` graph expansion |

### MCP Extras

| .NET Query | Location | Why It Exists |
|---|---|---|
| `CALL db.labels()` | SchemaInfoResource:20 | Graph schema introspection |
| `CALL db.relationshipTypes()` | SchemaInfoResource:21 | Graph schema introspection |
| `CALL db.propertyKeys()` | SchemaInfoResource:22 | Graph schema introspection |
| Memory stats count query | MemoryStatusResource:20 | Node count statistics |

---

## 5. Structural Differences

### Query Organization

| Aspect | Python | .NET |
|---|---|---|
| **Location** | Centralized in `graph/queries.py` (83 constants) + `query_builder.py` (8 functions) | Distributed across 11 repository files + infrastructure |
| **Naming** | SCREAMING_SNAKE_CASE constants (`CREATE_ENTITY`, `SEARCH_MESSAGES_BY_EMBEDDING`) | Inline `const string cypher` or string literals inside methods |
| **Reuse** | Constants imported and reused across modules | Queries defined and used within the method that needs them |
| **Dynamic generation** | Separate builder functions return query strings | String interpolation inline (e.g., label SET, metadata filters) |

### MERGE / CREATE Patterns

| Aspect | Python | .NET |
|---|---|---|
| **Entity creation** | Monolithic MERGE + ON CREATE/ON MATCH including location, embedding, labels | Decomposed: MERGE base → SET location → SET embedding → SET labels |
| **Message creation** | Single CREATE with FOREACH for linked list | CREATE + separate FIRST_MESSAGE + NEXT_MESSAGE queries |
| **Relationship creation** | MERGE with inline entity lookup | MERGE with separate entity MERGEs for referential safety |

### Parameterization

| Aspect | Python | .NET |
|---|---|---|
| **Style** | `$param_name` (snake_case) | `$paramName` (camelCase) in most repos |
| **Embedding handling** | Included in main query, may be null | Always separate `SET embedding` query to handle null |
| **Metadata** | Stored as properties or JSON string | Always serialized as JSON string |

### Batch Operations

| Aspect | Python | .NET |
|---|---|---|
| **Messages** | `CREATE_MESSAGES_BATCH` + `CREATE_MESSAGE_LINKS` (2 queries) | `AddBatchAsync` with 5 sequential queries (create, embeddings, chain, link, re-read) |
| **Entities** | No explicit batch in `queries.py`; `build_create_entity_query()` for one at a time | `UpsertBatchAsync` with UNWIND + per-entity SET loops |
| **Facts** | No batch in Python | `UpsertBatchAsync` with UNWIND + per-fact SET loops |

### Null Handling

Python's monolithic queries can pass null parameters directly. .NET decomposes operations to avoid null issues:

```csharp
// .NET pattern: separate null-guarded queries
if (entity.Embedding is not null)
{
    await runner.RunAsync("MATCH (e:Entity {id: $id}) SET e.embedding = $embedding", ...);
}
```

vs. Python's single-query approach:

```python
# Python: null handled in the query itself
MERGE (e:Entity {id: $id})
ON CREATE SET e.embedding = $embedding  // null if not provided
```

---

## 6. Cypher Validation

### No .NET Cypher Parser/Validator Package

There is **no NuGet package** that provides offline Cypher syntax validation. The options are:

1. **`Neo4j.Driver`** — execution-only, no parse-without-execute API
2. **`openCypher` grammar** — available as ANTLR4 grammar but no maintained .NET binding
3. **No CypherDSL for .NET** — unlike Java's `neo4j-cypher-dsl`

### Recommended Validation Approach

Use **EXPLAIN-based validation** in integration tests:

```csharp
// Validate without executing: EXPLAIN prefix causes Neo4j to parse + plan but not run
await session.RunAsync($"EXPLAIN {cypher}", parameters);
```

This should be added to integration test suites for all repository methods to catch:
- Syntax errors in Cypher strings
- Missing parameter bindings
- Invalid property references
- Schema constraint violations

### Observations from Source Analysis

1. **All queries are syntactically well-formed** — no obviously malformed Cypher found during this analysis.

2. **Consistent use of `IF NOT EXISTS`** in all DDL statements (SchemaBootstrapper), preventing runtime errors on re-bootstrap.

3. **Dynamic label injection** in `EntityRepository.UpsertAsync` (line 94-95) uses `SanitizeLabel()` to prevent Cypher injection. The validation is sound: it strips non-alphanumeric characters and validates against a whitelist of POLE+O types.

4. **MetadataFilterBuilder** properly parameterizes all filter values, preventing injection via `$paramName` bindings.

5. **`MergeEntitiesAsync`** uses `CALL (source, target) { ... }` subquery syntax (Neo4j 5.0+). This is correct for the target Neo4j version but would fail on Neo4j 4.x.

---

## 7. Parity Score

### Raw Parity

| Status | Count | Queries |
|--------|-------|---------|
| ✅ Full Match | 44 | #3,4,6,7,8,12,13,17,18,19,20,21,22,23,25,26,27,28,29,30,31,32,33,35,36,37,38,39,40,44,46,47,48,49,52,59,60,62,63,64,67,70,73,76 |
| 🔄 Equivalent | 11 | #1,5,9,10,45,50,57,61,75,78,97 |
| 🔶 Partial | 1 | #51 |
| ❌ Missing | 42 | #2,11,14,15,16,24,34,41,42,43,53,54,55,56,58,65,66,68,69,71,72,74,77,79,80-95,96,98 |

### Dynamic Query Parity

| Status | Count |
|--------|-------|
| ✅ Full Match | 8 | D1, D4, D8, D9, D11, D12, D13 + inline equivalents |
| 🔄 Equivalent | 5 | D2, D3, D5, D10, D14, D15 |
| ❌ Missing | 2 | D6, D7 |

### Inline Query Parity

| Status | Count |
|--------|-------|
| ✅ Full Match | 5 | I1, I2, I3, I6, I7 |
| 🔄 Equivalent | 4 | I4, I5, I8, I9 |
| ❌ Missing | 3 | I10, I11, I12 |

### Parity Calculations

**Raw parity** (static queries only):
```
(44 matched + 11 equivalent) / 98 total = 56.1%
```

**Adjusted parity** — excluding architectural omissions (16 schema persistence + 4 schema introspection + 4 graph export = 24):
```
(44 + 11) / (98 - 24) = 55 / 74 = 74.3%
```

**Functional parity** — excluding all decided omissions (31 total):
```
(44 + 11) / (98 - 31) = 55 / 67 = 82.1%
```

### Summary

| Metric | Score |
|--------|-------|
| **Raw parity** (all 98 Python queries) | **56.1%** |
| **Adjusted parity** (excl. architectural differences) | **74.3%** |
| **Functional parity** (excl. all decided omissions) | **82.1%** |
| **Genuine gaps remaining** | **11 queries** |
| **.NET extras beyond Python** | **~47 queries** |

The .NET port achieves strong functional parity on core memory operations (CRUD, vector search, relationships, reasoning). The gaps are concentrated in monitoring/statistics queries (provenance stats, dedup stats), individual message lifecycle management (delete, session listing), and visualization features (graph export) — all of which are low-to-medium effort additions if needed.
