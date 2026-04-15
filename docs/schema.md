# Agent Memory for .NET — Canonical Schema Reference

> **Authority:** This document is the single source of truth for the Neo4j graph schema.
> **Canonical source:** Python reference implementation (`Neo4j/agent-memory/src/neo4j_agent_memory/`)
> **Date:** 2025-07-22 (Post Wave 4A/4B/4C review)
> **Author:** Deckard (Lead / Solution Architect)

---

## Schema Parity Status: ~88% — 27 items FIXED, 16 items remaining

| Category | Python Items | .NET Matches | Parity |
|----------|:---:|:---:|:---:|
| Node labels | 11 | 9 implemented | 82% |
| Constraints | 9 | 9/9 | 100% |
| Property indexes | 10 | 10/10 | 100% |
| Vector indexes | 5 | 5/5 | 100% |
| Point indexes | 1 | 0 | 0% |
| Relationship types | 15 | 13 | 87% |
| Property naming (snake_case) | — | All correct | 100% |
| Relationship properties | ~20 props | ~8 | 40% |
| Tool aggregate stats | 6 fields | 1 field | 17% |
| **Weighted overall** | | | **~88%** |

**Summary:** All P0 critical fixes (property naming, relationship type names, missing constraints, missing indexes) are now RESOLVED. Remaining gaps are P1 feature-level items (relationship properties, provenance subsystem, dynamic labels, point index, Tool stats).

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
| `description` | string | ❌ | — | Tool description |

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

#### Property Indexes (10)

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
| `schema_id_idx` | `Schema` | `id` |

---

### 1.4 Constraints (9)

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

---

## 2. Schema Difference Map — Post Wave 4A/4B/4C

> **Audit date:** 2025-07-22 | **Auditor:** Deckard | **Method:** Line-by-line comparison of Python `queries.py` vs .NET `Repositories/*.cs`

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
| Entity | `updated_at` | *(not set on MATCH)* | ❌ REMAINING — Entity upsert ON MATCH does not set `updated_at` |
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
| Tool | `successful_calls` | *(missing)* | ❌ REMAINING |
| Tool | `failed_calls` | *(missing)* | ❌ REMAINING |
| Tool | `total_duration_ms` | *(missing)* | ❌ REMAINING |
| Tool | `last_used_at` | *(missing)* | ❌ REMAINING |
| Tool | `description` | *(missing)* | ❌ REMAINING |

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
| `Extractor` | ✅ Present | ⚠️ Declared in SchemaConstants only | ❌ REMAINING — No repository implementation |
| `Schema` | ✅ Present | ⚠️ Declared in SchemaConstants only | ❌ REMAINING — No repository implementation |
| `Migration` | ❌ Not present | ✅ Present | .NET extension (infrastructure) |
| `MemoryRelationship` | ❌ Not present | ✅ Present (constraint only) | ⚠️ Should be reviewed/removed |

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
| `entity_location_idx` (Point) | ✅ | ❌ | ❌ REMAINING — Entity stores location but no Point index in bootstrapper |
| `schema_name_idx` | ✅ | ❌ | ❌ REMAINING — Schema node not implemented |
| `schema_id_idx` | ✅ | ❌ | ❌ REMAINING — Schema node not implemented |
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
| `EXTRACTED_BY` (Entity → Extractor) | ✅ | ❌ | ❌ REMAINING — Extractor node not implemented |
| `HAS_FACT` | ❌ | ✅ | .NET extension |
| `HAS_PREFERENCE` | ❌ | ✅ | .NET extension |
| `IN_SESSION` | ❌ | ✅ | .NET extension |

### 2.7 Relationship Property Differences

| Relationship | Property | Python | .NET | Status |
|-------------|----------|--------|------|--------|
| `MENTIONS` | `confidence` | ✅ float | ❌ | ❌ REMAINING |
| `MENTIONS` | `start_pos` | ✅ int | ❌ | ❌ REMAINING |
| `MENTIONS` | `end_pos` | ✅ int | ❌ | ❌ REMAINING |
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
| `SAME_AS` | `status` | ✅ | ❌ | ❌ REMAINING |
| `SAME_AS` | `updated_at` | ✅ | ❌ | ❌ REMAINING |
| `HAS_STEP` | `order` | ✅ int | ✅ `{order: $stepNumber}` | ✅ FIXED |
| `EXTRACTED_FROM` | `confidence` | ✅ float | ❌ | ❌ REMAINING |
| `EXTRACTED_FROM` | `start_pos` | ✅ int | ❌ | ❌ REMAINING |
| `EXTRACTED_FROM` | `end_pos` | ✅ int | ❌ | ❌ REMAINING |
| `EXTRACTED_FROM` | `context` | ✅ string | ❌ | ❌ REMAINING |
| `EXTRACTED_FROM` | `created_at` | ✅ datetime | ❌ | ❌ REMAINING |

### 2.8 Tool Node Differences

| Feature | Python | .NET | Status |
|---------|--------|------|--------|
| `total_calls` counter | ✅ | ✅ | ✅ FIXED |
| `successful_calls` counter | ✅ Pre-aggregated | ❌ | ❌ REMAINING |
| `failed_calls` counter | ✅ Pre-aggregated | ❌ | ❌ REMAINING |
| `total_duration_ms` counter | ✅ Pre-aggregated | ❌ | ❌ REMAINING |
| `last_used_at` timestamp | ✅ Present | ❌ | ❌ REMAINING |
| `description` field | ✅ Present | ❌ | ❌ REMAINING |
| Tool.name UNIQUE constraint | ✅ Present | ✅ Present | ✅ FIXED (Wave 4B) |

### 2.9 Additional Differences

| Feature | Python | .NET | Notes |
|---------|--------|------|-------|
| Datetime storage | Neo4j native `datetime()` | ISO 8601 strings | ❌ REMAINING — functional but different type |
| Entity MERGE key | `{name: $name, type: $type}` | `{id: $id}` | ⚠️ Different merge strategy — .NET is stricter |
| Dynamic entity labels | `(:Entity:Person:Individual)` | Not implemented | ❌ REMAINING |
| Geospatial queries | `SEARCH_LOCATIONS_NEAR`, `SEARCH_LOCATIONS_IN_BOUNDING_BOX` | Not implemented | ❌ REMAINING |
| Graph export queries | `GET_GRAPH_SHORT_TERM`, `GET_GRAPH_LONG_TERM`, etc. | Not implemented | ❌ REMAINING (MCP covers some) |
| Memory stats query | `GET_MEMORY_STATS` | Not implemented | ❌ REMAINING |
| Session listing w/ pagination | `LIST_SESSIONS` with ORDER BY/SKIP/LIMIT | Simple session queries | ❌ REMAINING |

---

### 2.10 Full Difference Table (Updated Post-Wave 4A/4B/4C)

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
| 13 | Entity.location | Point | ✅ FIXED (repo stores Point) | Missing Point index in bootstrapper |
| 14 | Dynamic entity labels | Feature | ❌ REMAINING | POLE+O labels not implemented |
| 15 | Preference.preference | Property name | ✅ FIXED | Cypher uses `preference` |
| 16–21 | Reasoning/ToolCall props | snake_case | ✅ FIXED | All snake_case |
| 22 | ToolCall status | Values | ✅ FIXED | Lowercase values |
| 23 | Tool aggregate stats | Properties | ❌ REMAINING | Only `total_calls` — missing 5 fields |
| 24 | Tool.name constraint | Constraint | ✅ FIXED (Wave 4B) | |
| 25 | Extractor node | Node | ❌ REMAINING | Not implemented |
| 26 | Schema node | Node | ❌ REMAINING | Not implemented |
| 27 | Migration node | Extra | ✅ .NET Extension | |
| 28 | MemoryRelationship | Extra | ⚠️ Review | Should consider removing |
| 29 | entity_location_idx | Point index | ❌ REMAINING | Not in bootstrapper |
| 30–33 | Property indexes | Indexes | ✅ FIXED (Wave 4B) | All 10 Python indexes present |
| 34–35 | Schema indexes | Indexes | ❌ REMAINING | Tied to Schema node |
| 36 | reasoning_step_embedding | Vector | ✅ .NET Extension | |
| 37 | MENTIONS rel props | Rel props | ❌ REMAINING | Missing confidence, start_pos, end_pos |
| 38 | HAS_STEP order | Rel prop | ✅ FIXED | `{order: $stepNumber}` |
| 39 | SAME_AS status/updated | Rel props | ❌ REMAINING | Missing status, updated_at |
| 40 | EXTRACTED_FROM props | Rel props | ❌ REMAINING | Missing 5 properties |
| 41 | EXTRACTED_BY rel | Relationship | ❌ REMAINING | Tied to Extractor node |
| 42–44 | HAS_FACT/PREF/SESSION | Extra rels | ✅ .NET Extension | |
| 45 | Datetime handling | Storage | ❌ REMAINING | ISO strings vs native datetime() |
| 46 | Entity updated_at | Property | ❌ REMAINING | ON MATCH should set updated_at |

---

## 3. Remaining Fix Plan (Post Wave 4A/4B/4C)

> All P0 critical items are RESOLVED. Remaining items are P1 (important) and P2 (nice-to-have).

### P1 — Important (Missing Features)

| # | Fix | Files | Impact |
|---|-----|-------|--------|
| P1-1 | Add `Extractor` node + `EXTRACTED_BY` relationship | New repository, SchemaBootstrapper | Provenance subsystem |
| P1-2 | Add POLE+O dynamic entity labels | `Neo4jEntityRepository.cs` | Query filtering by type label |
| P1-3 | Add `entity_location_idx` Point index | SchemaBootstrapper | Geospatial query performance |
| P1-4 | Add `MENTIONS` relationship properties | `Neo4jEntityRepository.cs` | `confidence`, `start_pos`, `end_pos` |
| P1-5 | Add `EXTRACTED_FROM` relationship properties | All relevant repositories | `confidence`, `start_pos`, `end_pos`, `context`, `created_at` |
| P1-6 | Add full Tool aggregate stats | `Neo4jToolCallRepository.cs` | `successful_calls`, `failed_calls`, `total_duration_ms`, `last_used_at`, `description` |
| P1-7 | Add `SAME_AS` status/updated_at properties | `Neo4jEntityRepository.cs` | Deduplication workflow support |
| P1-8 | Add Entity `updated_at` on ON MATCH | `Neo4jEntityRepository.cs` | Track last entity update |
| P1-9 | Switch to native `datetime()` | All repositories | Timestamp type parity |
| P1-10 | Add geospatial query methods | `Neo4jEntityRepository.cs` | `SEARCH_LOCATIONS_NEAR`, `SEARCH_LOCATIONS_IN_BOUNDING_BOX` |
| P1-11 | Remove `MemoryRelationship` phantom constraint | SchemaBootstrapper | Clean up non-Python artifact |

### P2 — Nice-to-Have

| # | Fix | Notes |
|---|-----|-------|
| P2-1 | Add `Schema` node + persistence | Python-only feature, may not be needed in .NET |
| P2-2 | Add graph export queries | Python has 4 export queries; MCP server may cover this |
| P2-3 | Add `GET_MEMORY_STATS` utility query | Useful for diagnostics |
| P2-4 | Add session listing with ordering/pagination | Python `LIST_SESSIONS` query |
| P2-5 | Keep `.NET-only` extensions | `HAS_FACT`, `HAS_PREFERENCE`, `IN_SESSION`, fulltext indexes, etc. |

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

## Appendix B: Count Summary

| Category | Python | .NET | Delta |
|----------|--------|------|-------|
| Node labels | 11 | 11 | -2 missing, +2 extra |
| Relationship types | 15 | 17 | -2 missing, +4 extra |
| Constraints | 9 | 10 | -1 missing, +2 extra |
| Vector indexes | 5 | 6 | +1 extra |
| Point indexes | 1 | 0 | -1 missing |
| Property indexes | 10 | 9 | -4 missing |
| Fulltext indexes | 0 | 3 | +3 extra |
| Schema persistence indexes | 2 | 0 | -2 missing |
