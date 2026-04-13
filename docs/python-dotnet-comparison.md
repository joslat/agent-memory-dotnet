# Python neo4j-agent-memory vs .NET agent-memory-dotnet — Comprehensive Comparison

> **Document authored by:** Sebastian (GraphRAG Interop Engineer)  
> **Date:** 2025-07-12  
> **Scope:** Exhaustive feature-by-feature comparison of the Python `neo4j-agent-memory` library
> (`Neo4j/agent-memory/`) and the .NET `agent-memory-dotnet` project (`src/`).

---

## 1. Executive Summary

Both projects solve the same core problem: give AI agents persistent, structured memory backed by
Neo4j. They share the same three-tier architecture (short-term conversation → long-term knowledge →
reasoning traces), the same graph schema primitives, and the same MCP tool surface at the surface
level.

**Python** (`neo4j-agent-memory`) is a mature, full-featured library:
- Multiple extraction backends: LLM (OpenAI/Anthropic), GLiNER2 (zero-shot NER), spaCy NER,
  multi-stage pipeline with merge strategies.
- Async-streaming extraction for large documents.
- Background entity enrichment (Wikipedia/Diffbot).
- Automatic geocoding for Location entities.
- 7+ framework integrations (LangChain, OpenAI Agents, Pydantic AI, LlamaIndex, AWS AgentCore,
  CrewAI, Google ADK, Microsoft Agent Framework).
- Observability: OpenTelemetry + Opik tracer, auto-decorating every operation.
- MCP resources + prompts in addition to tools.
- Configurable via environment variables (`NAM_*`) using pydantic-settings.
- POLE+O schema with subtypes, custom schema files (JSON/YAML).
- Geospatial point index for Location coordinates.

**.NET** (`agent-memory-dotnet`) is a production-grade, idiomatically C# implementation:
- LLM extraction (Azure OpenAI / any `IChatClient`) + Azure Language extraction (NER pipeline).
- Entity resolution chain (exact → fuzzy via FuzzySharp → semantic).
- Full provenance tracking: every entity/fact/preference carries `EXTRACTED_FROM` relationships.
- OpenTelemetry instrumentation via `ActivitySource` and `MeterProvider`.
- Microsoft Agent Framework (MAF) integration: `Neo4jMemoryContextProvider`,
  `Neo4jChatMessageStore`, `AgentTraceRecorder`.
- GraphRAG adapter: vector / fulltext / hybrid retrieval over external Neo4j knowledge graphs.
- MCP server via `ModelContextProtocol.Server`, 13 tools across 6 tool-type classes.
- DI-first design throughout; everything is `IOptions<T>`-configured.

**Key differences at a glance:**

| Dimension | Python | .NET |
|-----------|--------|------|
| Extractor types | LLM, GLiNER2, spaCy, pipeline | LLM (`IChatClient`), Azure Language |
| Schema model | POLE+O + legacy + custom YAML/JSON | Fixed entity types (string); no schema model enum |
| Entity enrichment | Wikipedia, Diffbot (background) | Wikipedia only (synchronous) |
| Geocoding | Nominatim / Google Maps → Point index | ❌ Not implemented |
| Streaming extraction | Chunked async streaming | ❌ Not implemented |
| Framework integrations | LangChain, OpenAI Agents, Pydantic AI, LlamaIndex, CrewAI, Google ADK, AWS AgentCore, MAF | MAF only |
| MCP resources / prompts | ✅ Resources + prompts + tools | ❌ Tools only |
| Session strategies | per_conversation, per_day, persistent | Session ID parameter |
| Observability | Opik + OpenTelemetry (decorator-based) | OpenTelemetry via `ActivitySource` |
| Deduplication | Inline with configurable thresholds | `memory_find_duplicates` tool + `SAME_AS` rel |
| Config source | Env vars (`NAM_*`) + .env | `IOptions<T>` + `appsettings.json` |

---

## 2. Architecture Comparison

### 2.1 High-Level Architecture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                        PYTHON (neo4j-agent-memory)                          │
│                                                                             │
│  Integrations ──────────────────────────────────────────────────────────── │
│  LangChain / OpenAI Agents / Pydantic AI / LlamaIndex / CrewAI /           │
│  Google ADK / AWS AgentCore / MAF / MCP Server                             │
│                          │                                                  │
│              MemoryClient (core/memory.py)                                  │
│         ┌────────┬────────┬────────┐                                       │
│  ShortTermMemory LongTermMemory ReasoningMemory                            │
│  (short_term.py) (long_term.py) (reasoning.py)                             │
│         │         │              │                                          │
│  ExtractionPipeline  ──► Resolvers ──► Neo4jClient (graph/)               │
│  (spaCy / GLiNER / LLM)  (composite)   SchemaManager                      │
│                                                                             │
│  Enrichment (background): Wikimedia / Diffbot                              │
│  Observability: Opik / OpenTelemetry                                       │
└─────────────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────────────┐
│                      .NET (agent-memory-dotnet)                             │
│                                                                             │
│  Integrations ──────────────────────────────────────────────────────────── │
│  MAF (AgentFramework) / MCP Server (McpServer) / GraphRAG (GraphRagAdapter)│
│                          │                                                  │
│           IMemoryService (Core/Services/MemoryService.cs)                  │
│      ┌────────┬────────────────┬──────────────────┐                       │
│  IShortTermMemory  ILongTermMemory  IReasoningMemory  IGraphRagContextSource│
│      │               │                 │                    │              │
│  IMemoryExtractionPipeline  IEntityResolver  INeo4jTransactionRunner       │
│  Extractors: LLM / AzureLanguage  IEmbeddingProvider                      │
│                                                                             │
│  Enrichment: WikimediaEnrichmentService (sync)                              │
│  Observability: ActivitySource + MetricProvider (OpenTelemetry API)        │
└─────────────────────────────────────────────────────────────────────────────┘
```

### 2.2 Package / Module Structure Comparison

| Python Module | .NET Project | Notes |
|---------------|-------------|-------|
| `neo4j_agent_memory/core/memory.py` | `Neo4j.AgentMemory.Core/Services/MemoryService.cs` | Facade; core entry point |
| `neo4j_agent_memory/memory/short_term.py` | `Neo4j.AgentMemory.Core/Services/ShortTermMemoryService.cs` | Conversation/message ops |
| `neo4j_agent_memory/memory/long_term.py` | `Neo4j.AgentMemory.Core/Services/LongTermMemoryService.cs` | Entities/facts/preferences |
| `neo4j_agent_memory/memory/reasoning.py` | `Neo4j.AgentMemory.Core/Services/ReasoningMemoryService.cs` | Traces/steps/tool calls |
| `neo4j_agent_memory/extraction/` | `Neo4j.AgentMemory.Extraction.Llm/` + `Neo4j.AgentMemory.Extraction.AzureLanguage/` | Extraction backends |
| `neo4j_agent_memory/extraction/pipeline.py` | `Neo4j.AgentMemory.Core/Services/MemoryExtractionPipeline.cs` | Pipeline orchestration |
| `neo4j_agent_memory/resolution/` | `Neo4j.AgentMemory.Core/Resolution/` | Entity resolution chain |
| `neo4j_agent_memory/enrichment/` | `Neo4j.AgentMemory.Enrichment/` | External entity enrichment |
| `neo4j_agent_memory/graph/` | `Neo4j.AgentMemory.Neo4j/` | Neo4j infra + repositories |
| `neo4j_agent_memory/graph/schema.py` | `Neo4j.AgentMemory.Neo4j/Schema/` | Index/constraint creation |
| `neo4j_agent_memory/config/settings.py` | All `*Options.cs` files in `Abstractions/Options/` | Configuration |
| `neo4j_agent_memory/schema/models.py` | (No equivalent) | POLE+O schema model config |
| `neo4j_agent_memory/mcp/` | `Neo4j.AgentMemory.McpServer/` | MCP server |
| `neo4j_agent_memory/observability/` | `Neo4j.AgentMemory.Observability/` | Tracing/metrics |
| `neo4j_agent_memory/integrations/` | `Neo4j.AgentMemory.AgentFramework/` + `GraphRagAdapter/` | Framework adapters |
| `neo4j_agent_memory/embeddings/` | `IEmbeddingProvider` (abstracted, no dedicated package) | Embedding abstraction |
| `neo4j_agent_memory/services/geocoder.py` | (No equivalent) | Geocoding service |
| `neo4j_agent_memory/cli/` | (No equivalent) | CLI tool |
| — | `Neo4j.AgentMemory.Abstractions/` | Interface-only package (Python has no equivalent) |

### 2.3 Dependency Comparison

| | Python | .NET |
|---|--------|------|
| Graph driver | `neo4j` (async) | `Neo4j.Driver` (async) |
| Embedding | `openai`, `sentence-transformers`, `boto3`, `google-cloud-aiplatform` | `Microsoft.Extensions.AI` (`IEmbeddingGenerator`) |
| LLM extraction | `openai`, `anthropic` | `Microsoft.Extensions.AI` (`IChatClient`) |
| NER extraction | `spacy`, `gliner`, `glirel` | `Azure.AI.TextAnalytics` |
| MCP server | `fastmcp` (FastAPI-based) | `ModelContextProtocol.Server` (.NET SDK) |
| Framework | LangChain, OpenAI SDK, Pydantic AI, MAF, etc. | Microsoft Agent Framework |
| Config | `pydantic-settings` | `Microsoft.Extensions.Options` |
| Observability | `opentelemetry-sdk`, `opik` | `System.Diagnostics.DiagnosticSource` (OTEL API) |
| Fuzzy match | `rapidfuzz` | `FuzzySharp` |
| Enrichment | `httpx` (async) | `HttpClient` (async) |
| Geocoding | `geopy` | ❌ None |

---

## 3. Main Feature Comparison Table

| Feature | Python | .NET | Status | Notes |
|---------|--------|------|--------|-------|
| **Short-term memory (messages)** | `ShortTermMemory` class | `ShortTermMemoryService` | ✅ Parity | Both support add, retrieve, search |
| **Long-term memory (entities)** | `LongTermMemory` class | `LongTermMemoryService` | ✅ Parity | Both support upsert, search, merge |
| **Long-term memory (facts)** | `LongTermMemory.add_fact()` | `LongTermMemoryService.AddFactAsync()` | ✅ Parity | SPO triple + confidence |
| **Long-term memory (preferences)** | `LongTermMemory.add_preference()` | `LongTermMemoryService.AddPreferenceAsync()` | ✅ Parity | Category + text + confidence |
| **Reasoning traces** | `ReasoningMemory` class | `ReasoningMemoryService` | ✅ Parity | Trace → Steps → ToolCalls |
| **Entity resolution (exact)** | `ExactMatchResolver` | `ExactMatchEntityMatcher` | ✅ Parity | Case-insensitive normalized match |
| **Entity resolution (fuzzy)** | `FuzzyMatchResolver` (rapidfuzz) | `FuzzyMatchEntityMatcher` (FuzzySharp) | ✅ Parity | Different library, same algorithm |
| **Entity resolution (semantic)** | `SemanticMatchResolver` | `SemanticMatchEntityMatcher` | ✅ Parity | Embedding cosine similarity |
| **Entity resolution (composite)** | `CompositeResolver` | `CompositeEntityResolver` | ✅ Parity | Chains exact→fuzzy→semantic |
| **Extraction: LLM** | `LlmEntityExtractor` (OpenAI/Anthropic) | `LlmEntityExtractor` + `LlmFactExtractor` + `LlmPreferenceExtractor` + `LlmRelationshipExtractor` | ✅ Parity | .NET more granular (4 extractors) |
| **Extraction: spaCy NER** | `SpacyEntityExtractor` | ❌ Not implemented | 🔴 Gap | Python-specific ML library |
| **Extraction: GLiNER2** | `GlinerEntityExtractor` | ❌ Not implemented | 🔴 Gap | Python-specific ML library |
| **Extraction: Azure Language** | ❌ Not implemented | `AzureLanguageEntityExtractor` | ✅ .NET addition | Azure-specific advantage |
| **Multi-stage extraction pipeline** | `ExtractionPipeline` (5 merge strategies) | `MemoryExtractionPipeline` (single stage) | 🟡 Partial | .NET has no multi-extractor merging |
| **Streaming extraction** | `StreamingExtractor` (chunked) | ❌ Not implemented | 🟡 Minor gap | For very long documents |
| **Background enrichment** | `BackgroundEnrichmentQueue` (async queue) | `WikimediaEnrichmentService` (synchronous) | 🟡 Partial | Python is non-blocking |
| **Wikipedia enrichment** | `WikimediaProvider` | `WikimediaEnrichmentService` | ✅ Parity | Both fetch Wikipedia summaries |
| **Diffbot enrichment** | `DiffbotProvider` | ❌ Not implemented | 🟡 Minor gap | Commercial knowledge graph |
| **Geocoding** | `GeocoderService` (Nominatim/Google) | ❌ Not implemented | 🟡 Minor gap | Python stores Neo4j Point |
| **Geospatial index** | `setup_point_indexes()` (`Entity.location`) | ❌ Not implemented | 🟡 Minor gap | Enables radius queries |
| **POLE+O schema model** | `EntitySchemaConfig` + `POLEOEntityType` | String types only | 🟡 Partial | .NET has no schema model class |
| **Custom schema (YAML/JSON)** | `load_schema_from_file()` | ❌ Not implemented | 🟡 Minor gap | Python supports schema files |
| **Entity subtypes** | `subtype` field on entities | `Subtype` property on `Entity` | ✅ Parity | Both support it |
| **MCP tools: core (6)** | `memory_search`, `memory_get_context`, `memory_store_message`, `memory_add_entity`, `memory_add_preference`, `memory_add_fact` | Same 6 tools | ✅ Parity | Identical tool names |
| **MCP tools: extended** | 9 additional (15 total) | 7 additional (13 total) | 🟡 Partial | See detailed table below |
| **MCP resources** | `memory://context/{session_id}`, `memory://entities`, `memory://preferences`, `memory://graph/stats` | ❌ Not implemented | 🟡 Minor gap | Python exposes resources |
| **MCP prompts** | `memory-conversation`, `memory-reasoning`, `memory-review` | ❌ Not implemented | 🟡 Minor gap | Python exposes slash commands |
| **LangChain integration** | `Neo4jAgentMemory` (BaseChatMemory) | ❌ Not implemented | 🔴 Gap | Python-ecosystem only |
| **OpenAI Agents integration** | `Neo4jTracingProcessor` + `Neo4jMemoryStore` | ❌ Not implemented | 🔴 Gap | Python-ecosystem only |
| **Pydantic AI integration** | `Neo4jPydanticMemory` | ❌ Not implemented | 🔴 Gap | Python-ecosystem only |
| **LlamaIndex integration** | `Neo4jLlamaMemory` | ❌ Not implemented | 🔴 Gap | Python-ecosystem only |
| **CrewAI integration** | `Neo4jCrewMemory` | ❌ Not implemented | 🔴 Gap | Python-ecosystem only |
| **Google ADK integration** | `Neo4jMemoryService` (ADK) | ❌ Not implemented | 🔴 Gap | Python-ecosystem only |
| **AWS AgentCore integration** | `Neo4jMemoryProvider` | ❌ Not implemented | 🔴 Gap | Python-ecosystem only |
| **MAF integration** | `Neo4jContextProvider` + `Neo4jChatStore` + `Neo4jTracing` | `Neo4jMemoryContextProvider` + `Neo4jChatMessageStore` + `AgentTraceRecorder` | ✅ Parity | Both have full MAF integration |
| **GraphRAG adapter** | ❌ Not implemented | `Neo4jGraphRagContextSource` (vector/fulltext/hybrid) | ✅ .NET addition | Reads external Neo4j KGs |
| **Cross-memory relationships** | Via `EXTRACTED_FROM` in queries | Via `EXTRACTED_FROM` in pipeline | ✅ Parity | Both wire provenance |
| **`SAME_AS` entity dedup** | `DeduplicationConfig` + auto-merge | `Neo4jEntityRepository.AddSameAsRelationshipAsync()` | ✅ Parity | Slightly different trigger |
| **OpenTelemetry** | `OpenTelemetryTracer` | `MemoryActivitySource` + `MemoryMetrics` | ✅ Parity | Both emit spans/metrics |
| **Opik tracing** | `OpikTracer` | ❌ Not implemented | 🟡 Minor gap | LLM observability platform |
| **CLI tool** | `neo4j-agent-memory` (Typer) | ❌ Not implemented | 🟡 Minor gap | Schema setup, health checks |
| **Session strategies** | per_conversation, per_day, persistent | Session ID parameter (manual) | 🟡 Partial | Python auto-generates session IDs |
| **Token-budget / compression** | `observation_threshold` (auto-compress at 30K tokens) | ❌ Not implemented | 🟡 Minor gap | Python truncates/summarises |
| **Unit tests** | 20+ test files (unit/) | Neo4j.AgentMemory.Tests.Unit (multiple folders) | ✅ Both have tests | |
| **Integration tests** | `tests/integration/` | `Neo4j.AgentMemory.Tests.Integration/` | ✅ Both have tests | |
| **Benchmark tests** | `tests/benchmark/` | ❌ Not implemented | 🟡 Minor gap | Python has performance benchmarks |

---

## 4. Sub-Feature Detail Tables

### 4.1 Message Handling

| Sub-Feature | Python (`short_term.py`) | .NET (`ShortTermMemoryService`) | Gap? |
|-------------|--------------------------|--------------------------------|------|
| `add_message(content, role, session_id)` | `ShortTermMemory.add()` | `AddMessageAsync(sessionId, convId, role, content)` | ✅ |
| Batch add messages | `ShortTermMemory.add_batch()` | `Neo4jMessageRepository.AddBatchAsync()` | ✅ |
| Get conversation history | `get_conversation(session_id, limit)` | `GetConversationMessagesAsync(convId)` | ✅ |
| List all sessions | `list_sessions(limit, offset)` | `IConversationRepository.GetBySessionAsync()` | ✅ |
| Delete session | `delete_conversation(session_id)` | `DeleteBySessionAsync()` | ✅ |
| Semantic search on messages | `search(query, threshold)` | `SearchMessagesAsync(sessionId, embedding, limit, minScore)` | ✅ |
| Recent messages retrieval | `get_recent(limit)` | `GetRecentMessagesAsync(sessionId, limit)` | ✅ |
| Metadata filtering | `filters: dict` (JSON CONTAINS) | ❌ No metadata filter in .NET | 🟡 Minor |
| `FIRST_MESSAGE` relationship | `build_create_entity_query` wires it | `Neo4jConversationRepository` via `AddAsync` | ✅ |
| `NEXT_MESSAGE` chain | Query builder | `AddBatchAsync` wires in sequence | ✅ |
| Message embeddings | Configurable via `message_embedding_enabled` | `IEmbeddingProvider` always called | ✅ |
| Role values | `user`, `assistant`, `system` | String (unconstrained) | 🟡 Loose in .NET |

### 4.2 Entity Management

| Sub-Feature | Python | .NET | Gap? |
|-------------|--------|------|------|
| Upsert single entity | `long_term.add_entity()` | `IEntityRepository.UpsertAsync()` | ✅ |
| Upsert entity batch | `(not exposed at service level)` | `IEntityRepository.UpsertBatchAsync()` | ✅ .NET has more |
| Get by name (exact + aliases) | `search_entities(query, entity_types)` | `GetEntitiesByNameAsync(name, includeAliases)` | ✅ |
| Semantic entity search | `search_entities(query)` → vector search | `SearchEntitiesAsync(embedding, limit, minScore)` | ✅ |
| Merge entities (`SAME_AS`) | `DeduplicationConfig` auto-merge | `MergeEntitiesAsync(sourceId, targetId)` | ✅ |
| Add entity mention | `AddMentionAsync` (MENTIONED_IN) | `Neo4jEntityRepository.AddMentionAsync()` | ✅ |
| Batch mentions | `AddMentionsBatchAsync` | `AddMentionsBatchAsync()` | ✅ |
| `SAME_AS` relationship | `DuplicateCandidate.relationship_status` | `AddSameAsRelationshipAsync()` | ✅ |
| Canonical name tracking | `canonical_name` + `aliases` | `CanonicalName` + `Aliases` | ✅ |
| Entity subtype | `subtype` field | `Subtype` property | ✅ |
| Entity description | `description` field | `Description` property | ✅ |
| Entity attributes (dict) | `attributes: dict[str, Any]` | `Attributes` dictionary | ✅ |
| Re-embed after merge | ❌ Not done | `UpsertAsync` with updated text | 🟡 (tracked as gap) |
| Inline deduplication at ingest | `DeduplicationConfig` checks at add time | Resolver runs in pipeline | 🟡 Different approach |
| Entity enrichment on ingest | `BackgroundEnrichmentQueue.enqueue()` | `IEnrichmentService.EnrichAsync()` (sync) | 🟡 Python async |

### 4.3 Fact Management

| Sub-Feature | Python | .NET | Gap? |
|-------------|--------|------|------|
| Upsert fact (SPO triple) | `long_term.add_fact(subject, predicate, object_value)` | `AddFactAsync(fact)` | ✅ |
| Temporal validity (`valid_from`, `valid_until`) | ✅ (`valid_from`, `valid_until` ISO strings) | ✅ (`ValidFrom`, `ValidUntil` DateTimeOffset) | ✅ |
| Confidence score | ✅ | ✅ | ✅ |
| Batch fact upsert | ❌ (iterates single) | `IFactRepository.UpsertBatchAsync()` | ✅ .NET has more |
| Semantic fact search | `search_facts(query)` | `SearchFactsAsync(embedding, limit)` | ✅ |
| Fact deduplication | `fact_deduplication_enabled` config | ❌ No dedup in .NET yet | 🟡 Minor |
| Delete fact | ❌ Not in Python service API | ❌ Not exposed in .NET service API | Both missing |
| `EXTRACTED_FROM` provenance | Not wired explicitly in pipeline | Wired in `MemoryExtractionPipeline` | 🟡 .NET has more |

### 4.4 Preference Management

| Sub-Feature | Python | .NET | Gap? |
|-------------|--------|------|------|
| Add preference | `add_preference(category, preference, context, confidence)` | `AddPreferenceAsync(preference)` | ✅ |
| Semantic preference search | `search_preferences(query)` | `SearchPreferencesAsync(embedding, limit)` | ✅ |
| Delete preference | ❌ Not exposed | `DeletePreferenceAsync()` (tracked, implemented) | ✅ .NET has more |
| `EXTRACTED_FROM` provenance | Not explicit | Wired in `MemoryExtractionPipeline` | 🟡 .NET has more |
| Auto-detect from messages | `auto_preferences` flag in MCP server | `IPreferenceExtractor` in pipeline | ✅ |

### 4.5 Reasoning Traces

| Sub-Feature | Python (`reasoning.py`) | .NET (`ReasoningMemoryService`) | Gap? |
|-------------|--------------------------|--------------------------------|------|
| Start trace | `reasoning.start_trace(session_id, task)` | `StartTraceAsync(sessionId, task)` | ✅ |
| Add step (thought/action/observation) | `reasoning.add_step(trace_id, thought, action, observation)` | `AddStepAsync(traceId, stepNumber, thought, action, observation)` | ✅ |
| Record tool call | `reasoning.record_tool_call(step_id, tool_name, arguments, result)` | `RecordToolCallAsync(stepId, toolName, input, output, status)` | ✅ |
| Complete trace | `reasoning.complete_trace(trace_id, outcome, success)` | `CompleteTraceAsync(traceId, outcome, success)` | ✅ |
| Semantic search on traces | Task embedding search | `SearchTracesAsync(embedding, limit, minScore)` | ✅ |
| Tool statistics | `tool_stats_enabled` config | ❌ Not a distinct feature in .NET | 🟡 Minor |
| `HAS_TRACE` / `IN_SESSION` relationships | Built into queries | Wired in repositories | ✅ |
| `CALLS` relationship (step → tool) | `record_tool_call` → `ToolCall` node | `RecordToolCallAsync` → `ToolCall` node | ✅ |

### 4.6 Extraction Pipeline Components

| Component | Python | .NET | Gap? |
|-----------|--------|------|------|
| LLM entity extractor | `LlmEntityExtractor` (OpenAI/Anthropic) | `LlmEntityExtractor` (IChatClient) | ✅ |
| LLM fact extractor | Embedded in `LlmEntityExtractor` result | `LlmFactExtractor` (dedicated) | ✅ |
| LLM preference extractor | Embedded in `LlmEntityExtractor` | `LlmPreferenceExtractor` (dedicated) | ✅ |
| LLM relationship extractor | Embedded in `LlmEntityExtractor` | `LlmRelationshipExtractor` (dedicated) | ✅ .NET more granular |
| spaCy NER extractor | `SpacyEntityExtractor` | ❌ Not implemented | 🔴 Gap |
| GLiNER2 extractor | `GlinerEntityExtractor` | ❌ Not implemented | 🔴 Gap |
| GLiREL relation extractor | `GlirelRelationExtractor` | ❌ Not implemented | 🔴 Gap |
| Azure Language extractor | ❌ Not implemented | `AzureLanguageEntityExtractor` + `AzureLanguageFactExtractor` + `AzureLanguageRelationshipExtractor` | ✅ .NET adds |
| Multi-stage pipeline | `ExtractionPipeline` (MergeStrategy enum) | ❌ Single-stage (1 entity extractor) | 🟡 Major gap |
| Merge strategies | UNION, INTERSECTION, CONFIDENCE, CASCADE, FIRST_SUCCESS | ❌ No multi-extractor merge | 🟡 Major gap |
| Batch extraction | `extract_batch(texts, ...)` | Single message batch in pipeline | 🟡 Partial |
| Streaming extraction | `StreamingExtractor.extract_streaming()` | ❌ Not implemented | 🟡 Minor gap |
| Progress callbacks | `ProgressCallback` on pipeline | ❌ Not implemented | 🟡 Minor gap |
| Stopword / entity validation | `is_valid_entity_name()`, `ENTITY_STOPWORDS` | `EntityValidator` class | ✅ |

### 4.7 MCP Tools (Individual)

| Tool | Python | .NET | Match? |
|------|--------|------|--------|
| `memory_search` | Core profile | `CoreMemoryTools.MemorySearch` | ✅ |
| `memory_get_context` | Core profile | `CoreMemoryTools.MemoryGetContext` | ✅ |
| `memory_store_message` | Core profile | `CoreMemoryTools.MemoryStoreMessage` | ✅ |
| `memory_add_entity` | Core profile | `CoreMemoryTools.MemoryAddEntity` | ✅ |
| `memory_add_preference` | Core profile | `CoreMemoryTools.MemoryAddPreference` | ✅ |
| `memory_add_fact` | Core profile | `CoreMemoryTools.MemoryAddFact` | ✅ |
| `memory_get_conversation` | Extended profile | `ConversationTools.MemoryGetConversation` | ✅ |
| `memory_list_sessions` | Extended profile | `ConversationTools.MemoryListSessions` | ✅ |
| `memory_get_entity` | Extended profile | `EntityTools.MemoryGetEntity` | ✅ |
| `memory_create_relationship` | Extended profile | `EntityTools.MemoryCreateRelationship` | ✅ |
| `memory_export_graph` | Extended profile | `AdvancedMemoryTools.MemoryExportGraph` | ✅ |
| `memory_start_trace` | Extended profile | `ReasoningTools.MemoryStartTrace` | ✅ |
| `memory_record_step` | Extended profile | `ReasoningTools.MemoryRecordStep` | ✅ |
| `memory_complete_trace` | Extended profile | `ReasoningTools.MemoryCompleteTrace` | ✅ |
| `memory_get_observations` | Extended profile | ❌ Not implemented | 🟡 Minor |
| `graph_query` (read-only) | Extended profile (blocks writes) | `GraphQueryTools.GraphQuery` | ✅ Same intent; Python blocks writes, .NET gates via `EnableGraphQuery` |
| `memory_record_tool_call` | ❌ Not in MCP tools | `AdvancedMemoryTools.MemoryRecordToolCall` | ✅ .NET adds |
| `memory_find_duplicates` | ❌ Not in MCP tools | `AdvancedMemoryTools.MemoryFindDuplicates` | ✅ .NET adds |
| `extract_and_persist` | ❌ Not in MCP tools | `AdvancedMemoryTools.ExtractAndPersist` | ✅ .NET adds |
| MCP Resources | 4 resources (context, entities, prefs, stats) | ❌ Not implemented | 🟡 |
| MCP Prompts | 3 prompts (conversation, reasoning, review) | ❌ Not implemented | 🟡 |

### 4.8 Graph Relationships (Cypher Schema)

| Relationship | Python | .NET | Notes |
|--------------|--------|------|-------|
| `FIRST_MESSAGE` (Session→Message) | `build_create_entity_query` | `Neo4jConversationRepository` | ✅ |
| `NEXT_MESSAGE` (Message→Message) | `add_batch` chain | `Neo4jMessageRepository.AddBatchAsync` | ✅ |
| `EXTRACTED_FROM` (Entity/Fact/Pref→Message) | Not wired in pipeline | ✅ Wired in `MemoryExtractionPipeline` | ✅ .NET stronger |
| `MENTIONS` (Entity→Entity) | Via `add_relationship()` | `Neo4jEntityRepository.AddMentionAsync` | ✅ |
| `SAME_AS` (Entity→Entity) | `AddSameAsRelationshipAsync` | `Neo4jEntityRepository.AddSameAsRelationshipAsync` | ✅ |
| `HAS_STEP` (Trace→Step) | `add_step()` query | `Neo4jReasoningStepRepository` | ✅ |
| `CALLS` (Step→ToolCall→Tool) | `record_tool_call()` | `Neo4jToolCallRepository` | ✅ |
| `HAS_TRACE` (Session→Trace) | wired in `start_trace` | `Neo4jReasoningTraceRepository` | ✅ |
| `IN_SESSION` (Entity→Session) | via queries | `Neo4jEntityRepository` | ✅ |
| POLE+O typed relationships (KNOWS, MEMBER_OF, etc.) | Defined in `schema/models.py` | Only generic `RELATED_TO` | 🟡 Python richer schema |
| `LOCATED_AT` (geospatial) | `RESIDES_AT`, `HEADQUARTERS_AT` | ❌ Not schema-defined | 🟡 Minor |

### 4.9 Vector Indexes and Search

| Index | Python | .NET | Notes |
|-------|--------|------|-------|
| `message_embedding_idx` | ✅ `Message.embedding` | ✅ | Both |
| `entity_embedding_idx` | ✅ `Entity.embedding` | ✅ | Both |
| `preference_embedding_idx` | ✅ `Preference.embedding` | ✅ | Both |
| `fact_embedding_idx` | ✅ `Fact.embedding` | ✅ | Both |
| `task_embedding_idx` | ✅ `ReasoningTrace.task_embedding` | ✅ | Both |
| `entity_location_idx` (Point) | ✅ | ❌ | Python only (geocoding) |
| Hybrid search (fulltext + vector) | `hybrid_search_enabled` config | `GraphRagSearchMode.Hybrid` (adapter only) | 🟡 Python in memory search; .NET in GraphRAG |
| Fulltext index | ✅ entity_name + canonical_name | `conversation_session_idx`, `entity_type_idx` | Both |

### 4.10 Configuration Options

| Config Group | Python (`MemorySettings`) | .NET (`*Options`) | Gap? |
|---|---|---|---|
| Neo4j connection | `Neo4jConfig` (pool, timeouts, keep-alive) | `Neo4jOptions` (uri, user, pass) | 🟡 Python has more connection tuning |
| Embedding | `EmbeddingConfig` (5 providers, batch size, device) | `IEmbeddingProvider` abstraction | 🟡 Python has explicit provider selection |
| LLM | `LLMConfig` (provider, model, temp, max_tokens) | `LlmExtractionOptions` (model, system prompt) | ✅ |
| Schema | `SchemaConfig` (model: poleo/legacy/custom, strict_types) | No schema model concept | 🟡 Python more flexible |
| Extraction | `ExtractionConfig` (extractor_type, pipeline settings, per-extractor) | `ExtractionOptions` (min confidence, flags, validation) | 🟡 Python far more options |
| Resolution | `ResolutionConfig` (strategy, thresholds, fuzzy_scorer) | `EntityResolutionOptions` (thresholds) | ✅ |
| Memory behaviour | `MemoryConfig` (embedding flags, dedup, tool stats) | `LongTermMemoryOptions`, `ShortTermMemoryOptions`, `RecallOptions` | ✅ |
| Search | `SearchConfig` (limit, threshold, hybrid, graph_depth) | `RecallOptions` (counts, blend mode) | ✅ |
| Geocoding | `GeocodingConfig` (provider, cache, rate limit) | ❌ Not implemented | 🟡 Python only |
| Enrichment | `EnrichmentConfig` (providers, queue, cache, entity types) | `EnrichmentOptions` (basic) | 🟡 Python more complete |
| MCP server | `session_strategy`, `observation_threshold`, `auto_preferences` | `McpServerOptions` (sessionId, DefaultConfidence, EnableGraphQuery) | 🟡 Different focus |

### 4.11 Error Handling Patterns

| Pattern | Python | .NET |
|---------|--------|------|
| Neo4j errors | `SchemaError`, `Neo4jError` (typed exceptions) | `Neo4j.Driver` exceptions, logged and rethrown |
| Extractor failures | `try/except` per stage in pipeline | `ExtractSafeAsync<T>` swallows, logs, returns empty |
| Embedding failures | Logged, returns `None` embedding | `IEmbeddingProvider` throws, caught in pipeline |
| MCP tool errors | `try/except`, returns `{"error": str(e)}` JSON | `McpException` for capability errors; service exceptions propagate |
| Enrichment failures | `EnrichmentStatus.ERROR/RATE_LIMITED`, retry queue | `EnrichmentStatus.Error`, no retry queue |
| Resolution failures | Falls through to no-match result | Returns `EntityResolutionResult.NotResolved` |

---

## 5. Gap Analysis

### 5.1 Features in Python NOT in .NET (Gaps to Consider)

| Feature | Severity | File(s) | Estimated Effort |
|---------|----------|---------|-----------------|
| **spaCy NER extractor** | Major | `extraction/spacy_extractor.py` | High (Python ML dep; no .NET port) |
| **GLiNER2 extractor** | Major | `extraction/gliner_extractor.py` | High (Python ML dep; no .NET port) |
| **Multi-stage extraction pipeline with merge strategies** | Major | `extraction/pipeline.py` | Medium — add `IExtractionPipeline` composition in Core |
| **Geocoding (Nominatim/Google)** | Minor | `services/geocoder.py`, `GeocodingConfig` | Medium — `HttpClient` + Neo4j Point type |
| **Geospatial Point index** | Minor | `graph/schema.py setup_point_indexes()` | Low — one Cypher statement in schema setup |
| **Background enrichment queue** | Minor | `enrichment/background.py` | Medium — `Channel<T>` + hosted service |
| **Diffbot enrichment provider** | Minor | `enrichment/diffbot.py` | Low-Medium — HTTP client integration |
| **Streaming extraction for large docs** | Minor | `extraction/streaming.py` | Medium — chunk splitter + async iteration |
| **Session strategies (per_day, persistent)** | Minor | `mcp/server.py create_mcp_server()` | Low — session ID generator strategies |
| **Token-budget observation compression** | Minor | `mcp/_observer.py` | Medium — token counter + summariser |
| **MCP resources** (`memory://context/`, etc.) | Minor | `mcp/_resources.py` | Low — add `[McpServerResource]` handlers |
| **MCP prompts** (`memory-conversation`, etc.) | Minor | `mcp/_prompts.py` | Low — add `[McpServerPrompt]` handlers |
| **Opik observability tracer** | Minor | `observability/opik.py` | Low — `ITracer` adapter |
| **POLE+O schema model config** | Minor | `schema/models.py` | Low — add `EntitySchemaConfig` class |
| **Custom schema from YAML/JSON** | Minor | `schema/models.py load_schema_from_file()` | Low |
| **Metadata filters in message search** | Minor | `short_term.py _build_metadata_filter_clause()` | Low — add WHERE clause builder |
| **Fact deduplication** | Minor | `memory/long_term.py fact_deduplication_enabled` | Medium — SPO hash or embedding threshold |
| **LangChain integration** | Low (not .NET target) | `integrations/langchain/` | N/A |
| **OpenAI Agents integration** | Low (not .NET target) | `integrations/openai_agents/` | N/A |
| **Pydantic AI integration** | Low (not .NET target) | `integrations/pydantic_ai/` | N/A |
| **CLI tool** | Low | `cli/main.py` | Low — `dotnet tool` with schema management |

### 5.2 Features in .NET NOT in Python (Advantages)

| Feature | Description |
|---------|-------------|
| **Abstractions package** | `Neo4j.AgentMemory.Abstractions` — interface-only package enables clean substitution and testing |
| **Azure Language extraction** | `AzureLanguageEntityExtractor` + `AzureLanguageFactExtractor` + `AzureLanguageRelationshipExtractor` — enterprise-grade cloud NLP |
| **GraphRAG adapter** | `Neo4jGraphRagContextSource` — reads external Neo4j knowledge graphs via vector/fulltext/hybrid, blends results into recall |
| **Granular extraction interfaces** | Separate `IEntityExtractor`, `IFactExtractor`, `IPreferenceExtractor`, `IRelationshipExtractor` allow individual replacement |
| **`UpsertBatchAsync` on Entity + Fact** | UNWIND-based batch upsert; Python iterates single-entity |
| **`memory_record_tool_call` MCP tool** | Not exposed in Python MCP surface |
| **`memory_find_duplicates` MCP tool** | Cypher-based candidate surfacing for human review |
| **`extract_and_persist` MCP tool** | Explicit one-shot extraction trigger |
| **`graph_query` gated capability** | Requires `EnableGraphQuery = true` — safer default than Python which validates read-only Cypher |
| **`DI-first / IOptions<T>`** | Fully compatible with ASP.NET Core hosting, `appsettings.json`, secrets |
| **`EXTRACTED_FROM` provenance** | Wired automatically in `MemoryExtractionPipeline` for every entity, fact, and preference |
| **`DeletePreferenceAsync`** | Explicitly supports preference retraction; Python has no delete API |

### 5.3 Behavioural Differences

| Feature | Python Behaviour | .NET Behaviour |
|---------|-----------------|----------------|
| **MCP `graph_query`** | Validates Cypher for write patterns (regex); always available | Requires `EnableGraphQuery = true` in `McpServerOptions` |
| **Entity merge (SAME_AS)** | `DeduplicationConfig` auto-merges at ingest (inline) | Manual trigger via `MergeEntitiesAsync` or `memory_find_duplicates` + tool |
| **Extraction pipeline** | Runs spaCy → GLiNER → LLM (pipeline with fallback) | Runs single extractor per type in parallel (`Task.WhenAll`) |
| **Enrichment** | Asynchronous background queue; non-blocking | Synchronous `EnrichAsync()` (blocking) |
| **Embedding dimensions** | Configurable per provider | Fixed by `IEmbeddingGenerator` contract |
| **Message metadata** | Stored as JSON string; searchable via `CONTAINS` | Structured properties (no JSON metadata bag) |
| **Tool call status** | `ToolCallStatus` enum with 6 values | `ToolCallStatus` enum with 4 values |
| **Session ID generation** | Automatic strategies (per_conversation, per_day) | Caller must provide session ID |

---

## 6. Quality Comparison

### 6.1 Test Coverage

| Aspect | Python | .NET |
|--------|--------|------|
| Unit test files | 20+ files in `tests/unit/` | Multiple folders in `Tests.Unit/` (Extraction, Resolution, Services, etc.) |
| Integration tests | `tests/integration/` | `Tests.Integration/` |
| Benchmark tests | `tests/benchmark/` | ❌ None |
| Test fixtures | `testing/fixtures.py`, `testing/mocks.py` | `Stubs/` + `TestHelpers/` |
| Observable coverage | Extractors, resolvers, pipeline, schema, enrichment | Services, repositories, resolution, MCP tools |

### 6.2 Documentation

| Aspect | Python | .NET |
|--------|--------|------|
| README | Comprehensive with install guide, examples | ✅ README with architecture overview |
| Inline docstrings | Comprehensive (all public APIs) | XML doc comments on public interfaces |
| Examples directory | `examples/` with notebooks | `samples/` with code examples |
| Architecture docs | `docs/` folder | `docs/` folder (architecture.md, design.md, etc.) |
| CHANGELOG | `CHANGELOG.md` | ❌ None |
| CONTRIBUTING | `CONTRIBUTING.md` | ❌ None |

### 6.3 API Design

| Aspect | Python | .NET |
|--------|--------|------|
| Entry point | `MemoryClient` context manager | `IMemoryService` DI injection |
| Async model | `async def` + `asyncio` | `async Task` + `CancellationToken` |
| Configuration | `MemorySettings(pydantic-settings)` | `IOptions<T>` + `IServiceCollection` |
| Type validation | Pydantic `BaseModel` with validators | C# records with `IOptions` validation |
| Error result types | Dict `{"error": str}` in MCP tools | `McpException` + typed service exceptions |
| Schema philosophy | POLE+O is default with configurable custom | String-based types; no formal schema model |
| Idiom | Pythonic (dataclasses, protocols, generators) | Idiomatic C# (records, interfaces, DI) |
| Extensibility | Protocol / ABC based | Interface based (DI replaceable) |

---

## 7. Python-Specific Features We Should Consider

The following Python features are absent in .NET and worth prioritising:

### High Priority

| Feature | Rationale | Estimated Effort |
|---------|-----------|-----------------|
| **Multi-stage pipeline with merge strategies** | LLM-only extraction is expensive; combining a fast extractor (spaCy/Azure Language) with LLM fallback is production-critical for cost | Medium (2–3 days) |
| **Fact deduplication** | Without it, contradicting facts accumulate silently in the graph | Medium (2 days) |
| **Background enrichment queue** | Blocking enrichment slows ingestion path significantly at scale | Medium (1–2 days) |

### Medium Priority

| Feature | Rationale | Estimated Effort |
|---------|-----------|-----------------|
| **MCP resources** (`memory://context/{session_id}`) | Enables Claude Desktop to auto-inject context before every turn | Low (1 day) |
| **MCP prompts** (memory-conversation, memory-reasoning) | Slash commands guide LLM workflows; good UX for end users | Low (1 day) |
| **Streaming extraction** | Required for processing transcripts, large documents, RAG inputs | Medium (2 days) |
| **Metadata filters in message search** | Useful for filtering by model, source, or custom tags | Low (1 day) |
| **Session strategies** | Removes boilerplate from callers; better DX | Low (1 day) |

### Low Priority

| Feature | Rationale | Estimated Effort |
|---------|-----------|-----------------|
| **Geocoding + Point index** | Enables powerful geospatial queries for location-centric agents | Medium (1–2 days) |
| **CLI tool** | Good DX for schema setup, health checks, migration | Low (1 day) |
| **Opik tracer** | LLM observability market; existing OTEL covers most cases | Low (0.5 days) |
| **Custom schema (YAML/JSON)** | Enterprise users want domain-specific entity types | Low (0.5 days) |
| **`memory_get_observations` tool** | Token-budget compression feedback; nice to have | Medium (1 day) |

---

*This document was produced by exhaustive analysis of all Python source files under
`Neo4j/agent-memory/src/neo4j_agent_memory/` and all .NET source files under `src/`.*
