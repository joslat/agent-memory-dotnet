# Agent Memory for .NET — Canonical Schema Reference

> **Authority:** This document is the single source of truth for the Neo4j graph schema.
> **Canonical source:** Python reference implementation (`Neo4j/agent-memory/src/neo4j_agent_memory/`)
> **Date:** 2025-07-24 (Post Gap Closure Sprint — Waves A/B/C)
> **Author:** Deckard (Lead / Solution Architect)

---

## Schema Parity Status: ~99% — All P0, P1, and Gap Closure items FIXED

| Category | Python Items | .NET Matches | Parity |
|----------|:---:|:---:|:---:|
| Node labels | 11 | 11 (all labels including Schema — indexed via schema_name_idx, schema_version_idx) | 100% |
| Constraints | 9 | 10 (all 9 Python + `extractor_name`) | 100% |
| Property indexes | 10 + 2 Schema | 14 (all 10 Python + 2 Schema + 2 extras) | 100% |
| Vector indexes | 5 | 6 (all 5 Python + `reasoning_step_embedding_idx`) | 100% |
| Point indexes | 1 | 1 (`entity_location_idx`) | 100% |
| Relationship types | 15 | 18 (all 15 Python + 3 .NET extras) | 100% |
| Property naming (snake_case) | — | All correct | 100% |
| Relationship properties | ~25 props | ~25 (all Python properties present) | 100% |
| Tool aggregate stats | 7 properties | 7 (all including `description`) | 100% |
| Datetime storage | Native `datetime()` | Native `datetime()` (with backward compat helper) | 100% |
| **Weighted overall** | | | **~99%** |

**Summary:** The Gap Closure Sprint (Waves A/B/C) completed all 9 actionable gaps (G1-G5, G7-G8, G10-G11). All timestamps now use native Neo4j `datetime()` storage via `Neo4jDateTimeHelper` (G1). Schema node indexes (`schema_name_idx`, `schema_version_idx`) are created in `SchemaBootstrapper` (G2). `Tool.description` is stored on the Tool node (G3). MCP resources return correct data with snake_case Cypher (G7 fix). MemoryStatus returns 6 counts matching Python (G8). 1058 tests pass, 0 failures.

---

## Table of Contents

1. [Canonical Schema Reference (Python)](#1-canonical-schema-reference-python)
   - [1.1 Node Labels](#11-node-labels)
   - [1.2 Relationship Types](#12-relationship-types)
   - [1.3 Indexes](#13-indexes)
   - [1.4 Constraints](#14-constraints)
2. [Schema Difference Map](#2-schema-difference-map)
   - [2.1 Relationship Name Differences](#21-relationship-name-differences)
   - [2.2 Property Naming Differences](#22-property-naming-differences)
   - [2.3 Missing/Extra Nodes](#23-missingextra-nodes)
   - [2.4 Missing/Extra Indexes](#24-missingextra-indexes)
   - [2.5 Missing/Extra Constraints](#25-missingextra-constraints)
   - [2.6 Missing/Extra Relationships](#26-missingextra-relationships)
   - [2.7 Relationship Property Differences](#27-relationship-property-differences)
   - [2.8 Tool Node Differences](#28-tool-node-differences)
   - [2.9 Full Difference Table](#29-full-difference-table)
3. [Fix Plan](#3-fix-plan)
4. [Property Naming Convention](#4-property-naming-convention)

---

## 1. Canonical Schema Reference (Python)

### 1.1 Node Labels

#### `Conversation` — Short-Term Memory

| Property | Type | Required | Default | Notes |
|----------|------|----------|---------|-------|
| `id` | string (UUID) | ✅ | — | MERGE key |
| `session_id` | string | ✅ | — | Links to session |
| `title` | string | ✅ | — | Conversation title |
| `created_at` | datetime | ✅ | `datetime()` | Auto-set on CREATE |
| `updated_at` | datetime | ✅ | `datetime()` | Updated on each message add |

#### `Message` — Short-Term Memory

| Property | Type | Required | Default | Notes |
|----------|------|----------|---------|-------|
| `id` | string (UUID) | ✅ | — | CREATE key |
| `role` | string | ✅ | — | `"user"`, `"assistant"`, `"system"`, `"tool"` |
| `content` | string | ✅ | — | Message text |
| `embedding` | list\<float\> | ❌ | — | Set at creation or backfilled |
| `timestamp` | datetime | ✅ | `datetime()` | Auto-set |
| `metadata` | string (JSON) | ❌ | — | Serialized JSON |

#### `Entity` — Long-Term Memory

| Property | Type | Required | Default | Notes |
|----------|------|----------|---------|-------|
| `id` | string (UUID) | ✅ | — | ON CREATE SET |
| `name` | string | ✅ | — | MERGE key (with `type`) |
| `type` | string | ✅ | — | MERGE key (with `name`). POLE+O: PERSON, OBJECT, LOCATION, EVENT, ORGANIZATION |
| `subtype` | string | ❌ | — | e.g. INDIVIDUAL, VEHICLE, CITY |
| `canonical_name` | string | ❌ | — | Normalized name for matching |
| `description` | string | ❌ | — | Entity description |
| `embedding` | list\<float\> | ❌ | — | Set at creation or backfilled |
| `confidence` | float | ❌ | — | Extraction confidence |
| `created_at` | datetime | ✅ | `datetime()` | Auto-set |
| `updated_at` | datetime | ❌ | `datetime()` | ON MATCH auto-set |
| `metadata` | string (JSON) | ❌ | — | Contains `aliases`, `attributes` |
| `location` | Point | ❌ | — | `point({latitude:..., longitude:...})` for LOCATION types |
| `aliases` | list\<string\> | ❌ | — | Alternative names (set during merge) |
| `merged_into` | string (UUID) | ❌ | — | Set when entity is merged into another |
| `merged_at` | datetime | ❌ | — | Timestamp of merge |

**Dynamic Labels:** Entity nodes get additional PascalCase labels for type/subtype via `build_label_set_clause()`: e.g. `(:Entity:Person)`, `(:Entity:Object:Vehicle)`, `(:Entity:Location:City)`

#### `Fact` — Long-Term Memory

| Property | Type | Required | Default | Notes |
|----------|------|----------|---------|-------|
| `id` | string (UUID) | ✅ | — | CREATE key |
| `subject` | string | ✅ | — | Fact subject |
| `predicate` | string | ✅ | — | Fact predicate |
| `object` | string | ✅ | — | Fact object |
| `confidence` | float | ❌ | — | Extraction confidence |
| `embedding` | list\<float\> | ❌ | — | Set at creation or backfilled |
| `valid_from` | string (ISO) | ❌ | — | Temporal validity start |
| `valid_until` | string (ISO) | ❌ | — | Temporal validity end |
| `created_at` | datetime | ✅ | `datetime()` | Auto-set |
| `metadata` | string (JSON) | ❌ | — | Serialized JSON |

#### `Preference` — Long-Term Memory

| Property | Type | Required | Default | Notes |
|----------|------|----------|---------|-------|
| `id` | string (UUID) | ✅ | — | CREATE key |
| `category` | string | ✅ | — | Preference category |
| `preference` | string | ✅ | — | Preference text |
| `context` | string | ❌ | — | Context of preference |
| `confidence` | float | ❌ | — | Extraction confidence |
| `embedding` | list\<float\> | ❌ | — | Set at creation or backfilled |
| `created_at` | datetime | ✅ | `datetime()` | Auto-set |
| `metadata` | string (JSON) | ❌ | — | Serialized JSON |

#### `ReasoningTrace` — Reasoning Memory

| Property | Type | Required | Default | Notes |
|----------|------|----------|---------|-------|
| `id` | string (UUID) | ✅ | — | CREATE key |
| `session_id` | string | ✅ | — | Links to session |
| `task` | string | ✅ | — | Task description |
| `task_embedding` | list\<float\> | ❌ | — | Embedding of task |
| `outcome` | string | ❌ | — | Set on completion |
| `success` | boolean | ❌ | — | Set on completion |
| `started_at` | datetime | ✅ | `datetime()` | Auto-set |
| `completed_at` | datetime | ❌ | `datetime()` | Set on update |
| `metadata` | string (JSON) | ❌ | — | Serialized JSON |

#### `ReasoningStep` — Reasoning Memory

| Property | Type | Required | Default | Notes |
|----------|------|----------|---------|-------|
| `id` | string (UUID) | ✅ | — | CREATE key |
| `step_number` | int | ✅ | — | Order within trace |
| `thought` | string | ❌ | — | Step thought |
| `action` | string | ❌ | — | Step action |
| `observation` | string | ❌ | — | Step observation (may be set later) |
| `embedding` | list\<float\> | ❌ | — | Set at creation or backfilled |
| `timestamp` | datetime | ✅ | `datetime()` | Auto-set |
| `metadata` | string (JSON) | ❌ | — | Serialized JSON |

#### `ToolCall` — Reasoning Memory

| Property | Type | Required | Default | Notes |
|----------|------|----------|---------|-------|
| `id` | string (UUID) | ✅ | — | CREATE key |
| `tool_name` | string | ✅ | — | Name of tool called |
| `arguments` | string (JSON) | ✅ | — | Serialized arguments |
| `result` | string (JSON) | ❌ | — | Serialized result |
| `status` | string | ✅ | — | `"pending"`, `"success"`, `"failure"`, `"error"`, `"timeout"`, `"cancelled"` |
| `duration_ms` | int | ❌ | — | Execution time |
| `error` | string | ❌ | — | Error message |
| `timestamp` | datetime | ✅ | `datetime()` | Auto-set |

#### `Tool` — Reasoning Memory (Aggregate)

| Property | Type | Required | Default | Notes |
|----------|------|----------|---------|-------|
| `name` | string | ✅ | — | MERGE key (UNIQUE constraint) |
| `created_at` | datetime | ✅ | `datetime()` | ON CREATE SET |
| `total_calls` | int | ✅ | `0` | Pre-aggregated counter |
| `successful_calls` | int | ✅ | `0` | Pre-aggregated counter |
| `failed_calls` | int | ✅ | `0` | Pre-aggregated counter |
| `total_duration_ms` | int | ✅ | `0` | Pre-aggregated sum |
| `last_used_at` | datetime | ❌ | `datetime()` | Updated on each call |
| `description` | string | ❌ | — | Tool description (stored by .NET) |

#### `Extractor` — Provenance

| Property | Type | Required | Default | Notes |
|----------|------|----------|---------|-------|
| `name` | string | ✅ | — | MERGE key |
| `id` | string (UUID) | ✅ | — | ON CREATE SET |
| `version` | string | ❌ | — | Extractor version |
| `config` | string (JSON) | ❌ | — | Serialized `EntitySchemaConfig` |
| `created_at` | datetime | ✅ | `datetime()` | ON CREATE SET |

#### `Schema` — Schema Persistence

| Property | Type | Required | Default | Notes |
|----------|------|----------|---------|-------|
| `id` | string (UUID) | ✅ | — | CREATE key |
| `name` | string | ✅ | — | Schema name |
| `version` | string | ✅ | — | Schema version |
| `description` | string | ❌ | — | Description |
| `config` | string (JSON) | ❌ | — | Serialized `EntitySchemaConfig` |
| `is_active` | boolean | ✅ | — | Active flag |
| `created_at` | datetime | ✅ | `datetime()` | Auto-set |
| `created_by` | string | ❌ | — | Creator identifier |

---

### 1.2 Relationship Types

#### Structural — Short-Term Memory

| # | Type | Source → Target | Properties | Notes |
|---|------|-----------------|------------|-------|
| 1 | `HAS_MESSAGE` | `Conversation` → `Message` | — | Container relationship |
| 2 | `FIRST_MESSAGE` | `Conversation` → `Message` | — | Points to head of linked list |
| 3 | `NEXT_MESSAGE` | `Message` → `Message` | — | Linked list chain |

#### Long-Term Memory

| # | Type | Source → Target | Properties | Notes |
|---|------|-----------------|------------|-------|
| 4 | `MENTIONS` | `Message` → `Entity` | `confidence` (float), `start_pos` (int), `end_pos` (int) | Entity mention in message |
| 5 | `RELATED_TO` | `Entity` → `Entity` | `id` (UUID), `type` (string), `relation_type` (string), `description` (string), `confidence` (float), `valid_from` (string), `valid_until` (string), `created_at` (datetime), `updated_at` (datetime) | Inter-entity relationship |
| 6 | `ABOUT` | `Preference` → `Entity` | — | Links preference to entity |
| 7 | `SAME_AS` | `Entity` ↔ `Entity` | `confidence` (float), `match_type` (string), `created_at` (datetime), `status` (string), `updated_at` (datetime) | Deduplication link |

#### Reasoning Memory

| # | Type | Source → Target | Properties | Notes |
|---|------|-----------------|------------|-------|
| 8 | `HAS_STEP` | `ReasoningTrace` → `ReasoningStep` | `order` (int) | Step ordering |
| 9 | `USES_TOOL` | `ReasoningStep` → `ToolCall` | — | Step-to-tool-call link |
| 10 | `INSTANCE_OF` | `ToolCall` → `Tool` | — | Links call to tool definition |

#### Cross-Memory

| # | Type | Source → Target | Properties | Notes |
|---|------|-----------------|------------|-------|
| 11 | `HAS_TRACE` | `Conversation` → `ReasoningTrace` | — | Conversation-to-trace |
| 12 | `INITIATED_BY` | `ReasoningTrace` → `Message` | — | Trace triggered by message |
| 13 | `TRIGGERED_BY` | `ToolCall` → `Message` | — | Tool call triggered by message |

#### Provenance

| # | Type | Source → Target | Properties | Notes |
|---|------|-----------------|------------|-------|
| 14 | `EXTRACTED_FROM` | `Entity` → `Message` | `confidence` (float), `start_pos` (int), `end_pos` (int), `context` (string), `created_at` (datetime) | Entity provenance |
| 15 | `EXTRACTED_BY` | `Entity` → `Extractor` | `confidence` (float), `extraction_time_ms` (int), `created_at` (datetime) | Extractor provenance |

---

### 1.3 Indexes

#### Vector Indexes (5)

| Index Name | Label | Property | Dimensions | Similarity |
|------------|-------|----------|------------|------------|
| `message_embedding_idx` | `Message` | `embedding` | 1536 (configurable) | cosine |
| `entity_embedding_idx` | `Entity` | `embedding` | 1536 | cosine |
| `preference_embedding_idx` | `Preference` | `embedding` | 1536 | cosine |
| `fact_embedding_idx` | `Fact` | `embedding` | 1536 | cosine |
| `task_embedding_idx` | `ReasoningTrace` | `task_embedding` | 1536 | cosine |

> **Note:** Python has NO `ReasoningStep` vector index. The .NET addition of `reasoning_step_embedding_idx` is an extension.

#### Point Indexes (1)

| Index Name | Label | Property | Notes |
|------------|-------|----------|-------|
| `entity_location_idx` | `Entity` | `location` | For geospatial queries |

#### Property Indexes (12)

| Index Name | Label | Property |
|------------|-------|----------|
| `conversation_session_idx` | `Conversation` | `session_id` |
| `message_timestamp_idx` | `Message` | `timestamp` |
| `message_role_idx` | `Message` | `role` |
| `entity_type_idx` | `Entity` | `type` |
| `entity_name_idx` | `Entity` | `name` |
| `entity_canonical_idx` | `Entity` | `canonical_name` |
| `preference_category_idx` | `Preference` | `category` |
| `trace_session_idx` | `ReasoningTrace` | `session_id` |
| `trace_success_idx` | `ReasoningTrace` | `success` |
| `tool_call_status_idx` | `ToolCall` | `status` |

#### Schema Persistence Indexes (2)

| Index Name | Label | Property |
|------------|-------|----------|
| `schema_name_idx` | `Schema` | `name` |
| `schema_version_idx` | `Schema` | `version` |

---

### 1.4 Constraints (10)

| Constraint Name | Label | Property | Type |
|-----------------|-------|----------|------|
| `conversation_id` | `Conversation` | `id` | UNIQUE |
| `message_id` | `Message` | `id` | UNIQUE |
| `entity_id` | `Entity` | `id` | UNIQUE |
| `preference_id` | `Preference` | `id` | UNIQUE |
| `fact_id` | `Fact` | `id` | UNIQUE |
| `reasoning_trace_id` | `ReasoningTrace` | `id` | UNIQUE |
| `reasoning_step_id` | `ReasoningStep` | `id` | UNIQUE |
| `tool_name` | `Tool` | `name` | UNIQUE |
| `tool_call_id` | `ToolCall` | `id` | UNIQUE |
| `extractor_name` | `Extractor` | `name` | UNIQUE |

---

## 2. Schema Difference Map — Post P1 Schema Parity Sprint

> **Audit date:** 2025-07-23 | **Auditor:** Deckard | **Method:** Line-by-line comparison of Python `queries.py` + `query_builder.py` vs .NET `Repositories/*.cs` + `SchemaBootstrapper.cs`

### 2.1 Relationship Name Differences

| Python (Canonical) | .NET (Current) | Status |
|--------------------|----------------|--------|
| `RELATED_TO` | `RELATED_TO` | ✅ FIXED (Wave 4A) |
| `USES_TOOL` | `USES_TOOL` | ✅ FIXED (Wave 4A) |
| `INSTANCE_OF` | `INSTANCE_OF` | ✅ FIXED (Wave 4A) |

### 2.2 Property Naming Differences

**All .NET Cypher queries now use `snake_case` property names ✅ FIXED (Wave 4A)**

| Node | Python Property | .NET Cypher Property | Status |
|------|----------------|---------------------|--------|
| Conversation | `session_id` | `session_id` | ✅ FIXED |
| Conversation | `created_at` | `created_at` | ✅ FIXED |
| Conversation | `updated_at` | `updated_at` | ✅ FIXED |
| Conversation | `title` | `title` | ✅ FIXED |
| Entity | `canonical_name` | `canonical_name` | ✅ FIXED |
| Entity | `source_message_ids` | `source_message_ids` | ✅ (.NET extension) |
| Entity | `updated_at` | `updated_at` | ✅ FIXED (P1 Sprint) — Entity upsert ON MATCH now sets `updated_at = datetime()` |
| Entity | `location` (Point) | `location` (via separate SET) | ✅ FIXED (entity repo stores Point) |
| Fact | `valid_from` | `valid_from` | ✅ FIXED |
| Fact | `valid_until` | `valid_until` | ✅ FIXED |
| Preference | `preference` | `preference` | ✅ FIXED (Cypher uses `preference`, not `preferenceText`) |
| ReasoningTrace | `session_id` | `session_id` | ✅ FIXED |
| ReasoningTrace | `task_embedding` | `task_embedding` | ✅ FIXED |
| ReasoningTrace | `started_at` | `started_at` | ✅ FIXED |
| ReasoningTrace | `completed_at` | `completed_at` | ✅ FIXED |
| ReasoningStep | `step_number` | `step_number` | ✅ FIXED |
| ToolCall | `tool_name` | `tool_name` | ✅ FIXED |
| ToolCall | `arguments` | `arguments` | ✅ FIXED |
| ToolCall | `result` | `result` | ✅ FIXED |
| ToolCall | `duration_ms` | `duration_ms` | ✅ FIXED |
| ToolCall | Status values | lowercase (`"success"`, `"error"`, etc.) | ✅ FIXED |
| Tool | `total_calls` | `total_calls` | ✅ FIXED |
| Tool | `successful_calls` | `successful_calls` | ✅ FIXED (P1 Sprint) — Pre-aggregated on INSTANCE_OF creation |
| Tool | `failed_calls` | `failed_calls` | ✅ FIXED (P1 Sprint) — Pre-aggregated on INSTANCE_OF creation |
| Tool | `total_duration_ms` | `total_duration_ms` | ✅ FIXED (P1 Sprint) — Pre-aggregated sum |
| Tool | `last_used_at` | `last_used_at` | ✅ FIXED (P1 Sprint) — Set to `datetime()` on each call |
| Tool | `description` | ✅ Stored | 🟡 Not set | ✅ FIXED (Gap Closure G3) — Stored on Tool node via `Neo4jToolCallRepository` |

**Remaining .NET extras (not in Python but kept for added value):**

| Node | Property | Notes |
|------|----------|-------|
| Message | `conversation_id`, `session_id`, `tool_call_ids` | Denormalization for query convenience |
| Entity | `attributes` (JSON), `source_message_ids` | Richer provenance tracking |
| Fact | `category`, `source_message_ids` | Category index + provenance |
| Preference | `source_message_ids` | Provenance tracking |
| Conversation | `user_id` | Multi-user support |
| ReasoningStep | `trace_id` | Direct trace lookup |
| ToolCall | `step_id` | Direct step lookup |

### 2.3 Missing/Extra Nodes

| Node Label | Python | .NET | Status |
|------------|--------|------|--------|
| `Extractor` | ✅ Present | ✅ Full implementation | ✅ FIXED (P1 Sprint) — `Neo4jExtractorRepository` with `IExtractorRepository`, `Extractor` domain model, `extractor_name` UNIQUE constraint |
| `Schema` | ✅ Present | ⚠️ Declared in SchemaConstants only | 🟡 P2 — No repository implementation. Python uses it for custom entity schema persistence |
| `Migration` | ❌ Not present | ✅ Present | .NET extension (infrastructure) |
| `MemoryRelationship` | ❌ Not present | ❌ Removed | ✅ FIXED (P1 Sprint) — Phantom constraint removed from SchemaBootstrapper |

### 2.4 Missing/Extra Indexes

| Index | Python | .NET | Status |
|-------|--------|------|--------|
| `conversation_session_idx` | ✅ | ✅ | ✅ FIXED (Wave 4B) |
| `message_timestamp_idx` | ✅ | ✅ | ✅ Already present |
| `message_role_idx` | ✅ | ✅ | ✅ FIXED (Wave 4B) |
| `entity_type_idx` | ✅ | ✅ | ✅ Already present |
| `entity_name_idx` | ✅ | ✅ | ✅ Already present |
| `entity_canonical_idx` | ✅ | ✅ | ✅ FIXED (Wave 4B) |
| `preference_category_idx` | ✅ | ✅ | ✅ Already present |
| `trace_session_idx` | ✅ | ✅ | ✅ Already present |
| `trace_success_idx` | ✅ | ✅ | ✅ FIXED (Wave 4B) |
| `tool_call_status_idx` | ✅ | ✅ | ✅ Already present |
| `entity_location_idx` (Point) | ✅ | ✅ | ✅ FIXED (P1 Sprint) — `CREATE POINT INDEX entity_location_idx` in SchemaBootstrapper |
| `schema_name_idx` | ✅ | ✅ | ✅ FIXED (Gap Closure G2) — Created in SchemaBootstrapper |
| `schema_version_idx` | ✅ | ✅ | ✅ FIXED (Gap Closure G2) — Created in SchemaBootstrapper |
| `fact_category` (Property) | ❌ | ✅ | .NET extension |
| `reasoning_step_timestamp` (Property) | ❌ | ✅ | .NET extension |
| `reasoning_step_embedding_idx` (Vector) | ❌ | ✅ | .NET extension |
| Fulltext: `message_content`, `entity_name`, `fact_content` | ❌ | ✅ | .NET extension |

### 2.5 Missing/Extra Constraints

| Constraint | Python | .NET | Status |
|------------|--------|------|--------|
| `conversation_id` | ✅ | ✅ | ✅ Match |
| `message_id` | ✅ | ✅ | ✅ Match |
| `entity_id` | ✅ | ✅ | ✅ Match |
| `fact_id` | ✅ | ✅ | ✅ Match |
| `preference_id` | ✅ | ✅ | ✅ Match |
| `reasoning_trace_id` | ✅ | ✅ | ✅ Match |
| `reasoning_step_id` | ✅ | ✅ | ✅ Match |
| `tool_name` (Tool.name) | ✅ | ✅ | ✅ FIXED (Wave 4B) |
| `tool_call_id` | ✅ | ✅ | ✅ Match |
| `relationship_id` (MemoryRelationship.id) | ❌ | ✅ | .NET extension |

### 2.6 Missing/Extra Relationships

| Relationship | Python | .NET | Status |
|-------------|--------|------|--------|
| `HAS_MESSAGE` | ✅ | ✅ | ✅ Match |
| `FIRST_MESSAGE` | ✅ | ✅ | ✅ Match |
| `NEXT_MESSAGE` | ✅ | ✅ | ✅ Match |
| `MENTIONS` | ✅ | ✅ | ✅ Match (but missing properties — see 2.7) |
| `RELATED_TO` | ✅ | ✅ | ✅ FIXED |
| `ABOUT` | ✅ | ✅ | ✅ Match |
| `SAME_AS` | ✅ | ✅ | ✅ Match (but missing properties — see 2.7) |
| `HAS_STEP` | ✅ | ✅ | ✅ Match (with `order` property ✅ FIXED) |
| `USES_TOOL` | ✅ | ✅ | ✅ FIXED |
| `INSTANCE_OF` | ✅ | ✅ | ✅ FIXED |
| `HAS_TRACE` | ✅ | ✅ | ✅ Match |
| `INITIATED_BY` | ✅ | ✅ | ✅ Match |
| `TRIGGERED_BY` | ✅ | ✅ | ✅ Match |
| `EXTRACTED_FROM` | ✅ | ✅ | ✅ Match (but missing properties — see 2.7) |
| `EXTRACTED_BY` (Entity → Extractor) | ✅ | ✅ | ✅ FIXED (P1 Sprint) — `Neo4jExtractorRepository.CreateExtractedByRelationshipAsync` with `confidence`, `extraction_time_ms`, `created_at` |
| `HAS_FACT` | ❌ | ✅ | .NET extension |
| `HAS_PREFERENCE` | ❌ | ✅ | .NET extension |
| `IN_SESSION` | ❌ | ✅ | .NET extension |

### 2.7 Relationship Property Differences

| Relationship | Property | Python | .NET | Status |
|-------------|----------|--------|------|--------|
| `MENTIONS` | `confidence` | ✅ float | ✅ | ✅ FIXED (P1 Sprint) — `ON CREATE SET r.confidence = $confidence` |
| `MENTIONS` | `start_pos` | ✅ int | ✅ | ✅ FIXED (P1 Sprint) — `ON CREATE SET r.start_pos = $startPos` |
| `MENTIONS` | `end_pos` | ✅ int | ✅ | ✅ FIXED (P1 Sprint) — `ON CREATE SET r.end_pos = $endPos` |
| `RELATED_TO` | `relation_type` | ✅ | ✅ | ✅ FIXED |
| `RELATED_TO` | `id` | ✅ | ✅ | ✅ Match |
| `RELATED_TO` | `description` | ✅ | ✅ | ✅ Match |
| `RELATED_TO` | `confidence` | ✅ | ✅ | ✅ Match |
| `RELATED_TO` | `valid_from` | ✅ | ✅ | ✅ Match |
| `RELATED_TO` | `valid_until` | ✅ | ✅ | ✅ Match |
| `RELATED_TO` | `created_at` | ✅ | ✅ | ✅ Match |
| `RELATED_TO` | `updated_at` | ✅ | ✅ | ✅ Match |
| `SAME_AS` | `confidence` | ✅ | ✅ | ✅ Match |
| `SAME_AS` | `match_type` | ✅ | ✅ | ✅ Match |
| `SAME_AS` | `created_at` | ✅ | ✅ | ✅ Match |
| `SAME_AS` | `status` | ✅ | ✅ | ✅ FIXED (P1 Sprint) — `ON CREATE SET r.status = $status` |
| `SAME_AS` | `updated_at` | ✅ | ✅ | ✅ FIXED (P1 Sprint) — `ON MATCH SET r.updated_at = datetime()` |
| `HAS_STEP` | `order` | ✅ int | ✅ `{order: $stepNumber}` | ✅ FIXED |
| `EXTRACTED_FROM` | `confidence` | ✅ float | ✅ | ✅ FIXED (P1 Sprint) — `ON CREATE SET r.confidence = $confidence` |
| `EXTRACTED_FROM` | `start_pos` | ✅ int | ✅ | ✅ FIXED (P1 Sprint) — `ON CREATE SET r.start_pos = $startPos` |
| `EXTRACTED_FROM` | `end_pos` | ✅ int | ✅ | ✅ FIXED (P1 Sprint) — `ON CREATE SET r.end_pos = $endPos` |
| `EXTRACTED_FROM` | `context` | ✅ string | ✅ | ✅ FIXED (P1 Sprint) — `ON CREATE SET r.context = $context` |
| `EXTRACTED_FROM` | `created_at` | ✅ datetime | ✅ | ✅ FIXED (P1 Sprint) — `ON CREATE SET r.created_at = datetime()` |

### 2.8 Tool Node Differences

| Feature | Python | .NET | Status |
|---------|--------|------|--------|
| `total_calls` counter | ✅ | ✅ | ✅ FIXED |
| `successful_calls` counter | ✅ Pre-aggregated | ✅ Pre-aggregated | ✅ FIXED (P1 Sprint) — Incremented atomically on each `INSTANCE_OF` creation |
| `failed_calls` counter | ✅ Pre-aggregated | ✅ Pre-aggregated | ✅ FIXED (P1 Sprint) — Incremented for status `error`/`timeout` |
| `total_duration_ms` counter | ✅ Pre-aggregated | ✅ Pre-aggregated | ✅ FIXED (P1 Sprint) — Summed from `COALESCE(tc.duration_ms, 0)` |
| `last_used_at` timestamp | ✅ Present | ✅ Present | ✅ FIXED (P1 Sprint) — `SET tool.last_used_at = datetime()` on each call |
| `description` field | ✅ Optional | ✅ Stored | ✅ FIXED (Gap Closure G3) — Stored on Tool node via `Neo4jToolCallRepository` |
| Tool.name UNIQUE constraint | ✅ Present | ✅ Present | ✅ FIXED (Wave 4B) |

### 2.9 Additional Differences

| Feature | Python | .NET | Notes |
|---------|--------|------|-------|
| Datetime storage | Neo4j native `datetime()` | ✅ Native `datetime()` | ✅ FIXED (Gap Closure G1) — All repositories migrated to native `datetime()` via `Neo4jDateTimeHelper` with backward compat for mixed-type reads |
| Entity MERGE key | `{name: $name, type: $type}` | `{id: $id}` | ⚠️ Design difference — .NET is stricter (UUID-based dedup). Python merges on name+type, allowing natural dedup but risking collisions |
| Dynamic entity labels | `(:Entity:Person:Individual)` | `(:Entity:PERSON:INDIVIDUAL)` | ✅ FIXED (P1 Sprint) — .NET uses UPPERCASE labels via `BuildDynamicLabels()`; Python uses PascalCase. Functionally equivalent |
| Geospatial queries | `SEARCH_LOCATIONS_NEAR`, `SEARCH_LOCATIONS_IN_BOUNDING_BOX` | `SearchByLocationAsync`, `SearchInBoundingBoxAsync` | ✅ FIXED (P1 Sprint) — Both radius and bounding box queries implemented |
| Graph export queries | `GET_GRAPH_SHORT_TERM`, `GET_GRAPH_LONG_TERM`, etc. | `AdvancedMemoryTools.MemoryExportGraph` | 🟡 P2 — .NET has export via MCP tool; Python has 4 typed export queries |
| Memory stats query | `GET_MEMORY_STATS` | ✅ `MemoryStatusResource` (6 counts) | ✅ FIXED (Gap Closure G8) — Returns Conversation, Message, Entity, Fact, Preference, ReasoningTrace counts matching Python |
| Session listing w/ pagination | `LIST_SESSIONS` with ORDER BY/SKIP/LIMIT | Simple session queries | 🟡 P2 — Python has richer session management with ordering/pagination |

---

### 2.10 Full Difference Table (Updated Post-P1 Sprint)

| # | Element | Type | Status | Notes |
|---|---------|------|--------|-------|
| 1 | Entity→Entity rel name | Rel name | ✅ FIXED (Wave 4A) | Now `RELATED_TO` |
| 2 | Step→ToolCall rel name | Rel name | ✅ FIXED (Wave 4A) | Now `USES_TOOL` |
| 3 | ToolCall→Tool rel name | Rel name | ✅ FIXED (Wave 4A) | Now `INSTANCE_OF` |
| 4 | All property naming | snake_case | ✅ FIXED (Wave 4A) | All Cypher uses snake_case |
| 5–7 | Conversation props | snake_case | ✅ FIXED | `session_id`, `created_at`, `updated_at` |
| 8 | Conversation.title | Property | ✅ FIXED | Now stored |
| 9–11 | Message denorm props | Extra | ✅ .NET Extension | `conversation_id`, `session_id`, `tool_call_ids` |
| 12 | Entity.canonical_name | snake_case | ✅ FIXED | |
| 13 | Entity.location | Point | ✅ FIXED | Repo stores Point + Point index in bootstrapper |
| 14 | Dynamic entity labels | Feature | ✅ FIXED (P1 Sprint) | `BuildDynamicLabels()` adds POLE+O type/subtype labels |
| 15 | Preference.preference | Property name | ✅ FIXED | Cypher uses `preference` |
| 16–21 | Reasoning/ToolCall props | snake_case | ✅ FIXED | All snake_case |
| 22 | ToolCall status | Values | ✅ FIXED | Lowercase values |
| 23 | Tool aggregate stats | Properties | ✅ FIXED (P1 Sprint) | All 5 auto-populated fields: `successful_calls`, `failed_calls`, `total_duration_ms`, `last_used_at`, `total_calls` |
| 24 | Tool.name constraint | Constraint | ✅ FIXED (Wave 4B) | |
| 25 | Extractor node | Node | ✅ FIXED (P1 Sprint) | Full `Neo4jExtractorRepository` with UPSERT, GET, LIST, EXTRACTED_BY |
| 26 | Schema node | Node | 🟡 P2 | Not implemented — Python uses for custom schema persistence |
| 27 | Migration node | Extra | ✅ .NET Extension | |
| 28 | MemoryRelationship | Extra | ✅ FIXED (P1 Sprint) | Phantom constraint removed |
| 29 | entity_location_idx | Point index | ✅ FIXED (P1 Sprint) | `CREATE POINT INDEX entity_location_idx` in SchemaBootstrapper |
| 30–33 | Property indexes | Indexes | ✅ FIXED (Wave 4B) | All 10 Python indexes present |
| 34–35 | Schema indexes | Indexes | ✅ FIXED (Gap Closure G2) | `schema_name_idx` + `schema_version_idx` in SchemaBootstrapper |
| 36 | reasoning_step_embedding | Vector | ✅ .NET Extension | |
| 37 | MENTIONS rel props | Rel props | ✅ FIXED (P1 Sprint) | `confidence`, `start_pos`, `end_pos` all present |
| 38 | HAS_STEP order | Rel prop | ✅ FIXED | `{order: $stepNumber}` |
| 39 | SAME_AS status/updated | Rel props | ✅ FIXED (P1 Sprint) | `status` on CREATE, `updated_at` on MATCH |
| 40 | EXTRACTED_FROM props | Rel props | ✅ FIXED (P1 Sprint) | All 5 properties: `confidence`, `start_pos`, `end_pos`, `context`, `created_at` |
| 41 | EXTRACTED_BY rel | Relationship | ✅ FIXED (P1 Sprint) | `confidence`, `extraction_time_ms`, `created_at` |
| 42–44 | HAS_FACT/PREF/SESSION | Extra rels | ✅ .NET Extension | |
| 45 | Datetime handling | Storage | ✅ FIXED (Gap Closure G1) | Native `datetime()` via `Neo4jDateTimeHelper` with backward compat |
| 46 | Entity updated_at | Property | ✅ FIXED (P1 Sprint) | ON MATCH SET `e.updated_at = datetime()` |
| 47 | Geospatial queries | Methods | ✅ FIXED (P1 Sprint) | `SearchByLocationAsync`, `SearchInBoundingBoxAsync` |
| 48 | Tool.description | Property | ✅ FIXED (Gap Closure G3) | Stored on Tool node via `Neo4jToolCallRepository` |

---

## 3. Remaining Fix Plan (Post Gap Closure Sprint)

> All P0, P1, and critical P2 items are RESOLVED. The Gap Closure Sprint (Waves A/B/C) closed 9 actionable gaps (G1-G5, G7-G8, G10-G11). 1058 tests pass, 0 failures.

### P1 — ✅ ALL COMPLETED

| # | Fix | Status | Notes |
|---|-----|--------|-------|
| P1-1 | Add `Extractor` node + `EXTRACTED_BY` relationship | ✅ DONE | `Neo4jExtractorRepository`, `IExtractorRepository`, `Extractor` domain model, `extractor_name` constraint |
| P1-2 | Add POLE+O dynamic entity labels | ✅ DONE | `BuildDynamicLabels()` in `Neo4jEntityRepository` — single + batch upsert |
| P1-3 | Add `entity_location_idx` Point index | ✅ DONE | `CREATE POINT INDEX entity_location_idx` in SchemaBootstrapper |
| P1-4 | Add `MENTIONS` relationship properties | ✅ DONE | `confidence`, `start_pos`, `end_pos` on `AddMentionAsync` |
| P1-5 | Add `EXTRACTED_FROM` relationship properties | ✅ DONE | `confidence`, `start_pos`, `end_pos`, `context`, `created_at` on `CreateExtractedFromRelationshipAsync` |
| P1-6 | Add full Tool aggregate stats | ✅ DONE | `successful_calls`, `failed_calls`, `total_duration_ms`, `last_used_at` in `Neo4jToolCallRepository.AddAsync` |
| P1-7 | Add `SAME_AS` status/updated_at properties | ✅ DONE | `status` on CREATE, `updated_at` on MATCH in `AddSameAsRelationshipAsync` |
| P1-8 | Add Entity `updated_at` on ON MATCH | ✅ DONE | `e.updated_at = datetime()` in `UpsertAsync` ON MATCH SET |
| P1-9 | Switch to native `datetime()` | ✅ DONE (Gap Closure G1) | All 7 repository files migrated to native `datetime()` via `Neo4jDateTimeHelper` with backward compat |
| P1-10 | Add geospatial query methods | ✅ DONE | `SearchByLocationAsync` (radius) + `SearchInBoundingBoxAsync` in `Neo4jEntityRepository` |
| P1-11 | Remove `MemoryRelationship` phantom constraint | ✅ DONE | Removed from SchemaBootstrapper |

### P2 — Nice-to-Have / Improvements

| # | Fix | Required for Parity? | Effort | Value | Notes |
|---|-----|:---:|:---:|:---:|-------|
| P2-1 | Add `Schema` node + repository | 🟡 Partial | Medium | Low | Python uses for custom entity schema persistence (POLE+O + YAML/JSON config). .NET uses fixed types. Only needed if we support custom schema models. Schema indexes are already created (G2). |
| P2-2 | Add graph export queries (typed) | ❌ No | Low | Low | Python has 4 typed exports (`GET_GRAPH_SHORT_TERM`, etc.). .NET already has `MemoryExportGraph` MCP tool with basic export |
| P2-3 | ~~Add `GET_MEMORY_STATS` query~~ | ~~❌ No~~ | ~~Low~~ | ~~Medium~~ | ✅ **CLOSED (Gap Closure G8)** — `MemoryStatusResource` returns 6 counts |
| P2-4 | Add session listing with ordering/pagination | 🟡 Partial | Low | Medium | Python `LIST_SESSIONS` has ORDER BY/SKIP/LIMIT. .NET has basic `GetBySessionAsync`. Pagination would improve DX |
| P2-5 | Keep `.NET-only` extensions | N/A | N/A | High | `HAS_FACT`, `HAS_PREFERENCE`, `IN_SESSION`, fulltext indexes, `reasoning_step_embedding_idx` — all add value beyond Python |
| P2-6 | ~~Add `Tool.description` field~~ | ~~❌ No~~ | ~~Trivial~~ | ~~Low~~ | ✅ **CLOSED (Gap Closure G3)** — Stored on Tool node |

---

## 4. What Would 100% Schema Parity Require?

The Gap Closure Sprint brought schema parity from ~96% to ~99%. The only remaining item for true 100% parity is:

### Remaining Changes (1 item)

| # | Item | Current Gap | Effort |
|---|------|-------------|--------|
| 1 | **Schema node** (P2-1) | No `Schema` node repository (indexes exist, node declaration exists) | Medium (2-3 days) |

### Already Achieved (formerly gaps, now closed)

| Item | Status | Sprint |
|------|--------|--------|
| Native `datetime()` storage | ✅ DONE | Gap Closure G1 |
| Schema indexes | ✅ DONE | Gap Closure G2 |
| Tool.description | ✅ DONE | Gap Closure G3 |
| Memory stats resource | ✅ DONE | Gap Closure G8 |

### Already-Different-by-Design (not blocking parity)

| Item | Python | .NET | Rationale |
|------|--------|------|-----------|
| Entity MERGE key | `{name, type}` | `{id}` | .NET is stricter — UUID-based prevents accidental merges. A conscious design choice |
| Dynamic label casing | PascalCase (`Person`) | UPPERCASE (`PERSON`) | Functionally equivalent for queries; .NET matches POLE+O uppercase convention |
| .NET-only extensions | N/A | 3 extra rels, 3 extra indexes, 1 extra vector index | Added value — not breaking parity |

### Summary: 99% Today → 100% with 1 Item

The path from 99% to 100% requires only the Schema node repository implementation (P2-1), which is low priority since .NET uses fixed entity types rather than custom schemas.

---

## 4. Property Naming Convention

### The Definitive Ruling

**Neo4j properties MUST use `snake_case`.** This is not a style choice — it is a schema compatibility requirement.

### What Does the Python Schema Actually Use?

Python uses `snake_case` for ALL Neo4j properties without exception:
- `session_id`, `created_at`, `updated_at`, `canonical_name`, `task_embedding`
- `step_number`, `tool_name`, `duration_ms`, `valid_from`, `valid_until`
- `merged_into`, `merged_at`, `start_pos`, `end_pos`, `match_type`
- `total_calls`, `successful_calls`, `failed_calls`, `total_duration_ms`, `last_used_at`
- `is_active`, `created_by`, `extraction_time_ms`
- Single-word properties are already compatible: `id`, `name`, `type`, `role`, `content`, `embedding`, `timestamp`, `confidence`, `description`, `metadata`, `status`, `error`

### What Should Our .NET Code Produce?

The C# domain models keep PascalCase per .NET conventions (`SessionId`, `CreatedAtUtc`). The **repository layer** is the translation boundary:

```
C# Domain Model (PascalCase) → Repository Cypher (snake_case) → Neo4j (snake_case)
     SessionId               →      session_id               →   session_id
     CreatedAtUtc             →      created_at               →   created_at
     PreferenceText           →      preference               →   preference
```

### Implementation Pattern

In each repository's Cypher strings, use `snake_case` property names and map to/from PascalCase domain properties in the C# mapping code:

```csharp
// Cypher uses snake_case
const string cypher = @"
    MERGE (e:Entity {name: $name, type: $type})
    ON CREATE SET
        e.id = $id,
        e.canonical_name = $canonical_name,
        e.created_at = datetime()
    RETURN e";

// C# parameter mapping: PascalCase → snake_case
var parameters = new {
    id = entity.EntityId,
    name = entity.Name,
    type = entity.Type,
    canonical_name = entity.CanonicalName
};

// Result mapping: snake_case → PascalCase
entity.CanonicalName = record["e"]["canonical_name"].As<string>();
```

### Why Not camelCase?

1. **Schema parity is non-negotiable.** The Python implementation is the reference.
2. **Neo4j convention is snake_case.** Most Neo4j examples and drivers use it.
3. **Same database instance.** Both Python and .NET clients may hit the same Neo4j database.
4. **The domain layer is the right place for .NET conventions.** Cypher is a database language, not C#.

---

## Appendix A: Python POLE+O Entity Type System

### Valid Types
`PERSON`, `OBJECT`, `LOCATION`, `EVENT`, `ORGANIZATION`

### Valid Subtypes

| Parent Type | Subtypes |
|-------------|----------|
| PERSON | INDIVIDUAL, ALIAS, PERSONA, SUSPECT, WITNESS, VICTIM |
| OBJECT | VEHICLE, PHONE, EMAIL, DOCUMENT, DEVICE, WEAPON, MONEY, DRUG, EVIDENCE, SOFTWARE, PRODUCT |
| LOCATION | ADDRESS, CITY, REGION, COUNTRY, LANDMARK, FACILITY, COORDINATES, GEOPOLITICAL, GEOGRAPHIC |
| EVENT | INCIDENT, MEETING, TRANSACTION, COMMUNICATION, CRIME, TRAVEL, EMPLOYMENT, OBSERVATION, DATE, TIME |
| ORGANIZATION | COMPANY, NONPROFIT, GOVERNMENT, EDUCATIONAL, CRIMINAL, POLITICAL, RELIGIOUS, MILITARY, GROUP |

### Default Relationship Types (Schema Model)

| Relationship | Source Types | Target Types |
|-------------|-------------|-------------|
| KNOWS | PERSON | PERSON |
| ALIAS_OF | PERSON | PERSON |
| MEMBER_OF | PERSON | ORGANIZATION |
| EMPLOYED_BY | PERSON | ORGANIZATION |
| OWNS | PERSON, ORGANIZATION | OBJECT |
| USES | PERSON | OBJECT |
| LOCATED_AT | PERSON, OBJECT, ORGANIZATION, EVENT | LOCATION |
| RESIDES_AT | PERSON | LOCATION |
| HEADQUARTERS_AT | ORGANIZATION | LOCATION |
| PARTICIPATED_IN | PERSON, ORGANIZATION | EVENT |
| OCCURRED_AT | EVENT | LOCATION |
| INVOLVED | EVENT | OBJECT |
| SUBSIDIARY_OF | ORGANIZATION | ORGANIZATION |
| PARTNER_WITH | ORGANIZATION | ORGANIZATION |
| RELATED_TO | ALL | ALL |
| MENTIONS | ALL | ALL |

---

## Appendix B: Count Summary (Post Gap Closure Sprint)

| Category | Python | .NET | Delta |
|----------|--------|------|-------|
| Node labels | 11 | 12 | +1 extra (Migration), -1 missing (Schema repo) |
| Relationship types | 15 | 18 | +3 extra (HAS_FACT, HAS_PREFERENCE, IN_SESSION) |
| Constraints | 9 | 10 | +1 extra (extractor_name) |
| Vector indexes | 5 | 6 | +1 extra (reasoning_step_embedding_idx) |
| Point indexes | 1 | 1 | Match |
| Property indexes | 10 + 2 Schema | 14 (all 12 Python + 2 extras) | Match on Python set, +2 extras |
| Fulltext indexes | 0 | 3 | +3 extra (message_content, entity_name, fact_content) |
| Schema persistence indexes | 2 | 2 | ✅ Match (schema_name_idx, schema_version_idx) |
