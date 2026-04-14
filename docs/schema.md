# Agent Memory for .NET — Canonical Schema Reference

> **Authority:** This document is the single source of truth for the Neo4j graph schema.
> **Canonical source:** Python reference implementation (`Neo4j/agent-memory/src/neo4j_agent_memory/`)
> **Date:** 2025-07-21
> **Author:** Deckard (Lead / Solution Architect)

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

## 2. Schema Difference Map

### 2.1 Relationship Name Differences

| Python (Canonical) | .NET (Current) | Impact |
|--------------------|----------------|--------|
| `RELATED_TO` | `RELATES_TO` | ❌ **BREAKS PARITY** — Different relationship type in Neo4j |
| `USES_TOOL` | `USED_TOOL` | ❌ **BREAKS PARITY** — Different relationship type in Neo4j |
| `INSTANCE_OF` | `CALLS` | ❌ **BREAKS PARITY** — Different relationship type in Neo4j |

### 2.2 Property Naming Differences

**Python uses `snake_case` throughout. .NET uses `camelCase` throughout.**

| Node | Python Property | .NET Property | Impact |
|------|----------------|---------------|--------|
| Conversation | `session_id` | `sessionId` | ❌ BREAKS PARITY |
| Conversation | `created_at` | `createdAtUtc` | ❌ BREAKS PARITY |
| Conversation | `updated_at` | `updatedAtUtc` | ❌ BREAKS PARITY |
| Conversation | `title` | *(not stored)* | ❌ MISSING |
| Message | — | `conversationId` | ⚠️ .NET extra (Python uses relationship) |
| Message | — | `sessionId` | ⚠️ .NET extra (Python uses conversation lookup) |
| Message | — | `toolCallIds` | ⚠️ .NET extra |
| Entity | `canonical_name` | `canonicalName` | ❌ BREAKS PARITY |
| Entity | `created_at` | `createdAtUtc` | ❌ BREAKS PARITY |
| Entity | `updated_at` | *(not stored)* | ❌ MISSING |
| Entity | `merged_into` | `mergedInto` | ❌ BREAKS PARITY |
| Entity | `merged_at` | `mergedAt` | ❌ BREAKS PARITY |
| Entity | `location` | *(not implemented)* | ❌ MISSING |
| Entity | — | `sourceMessageIds` | ⚠️ .NET extra |
| Entity | — | `attributes` (JSON) | ⚠️ .NET stores as separate property vs Python metadata |
| Fact | `valid_from` | `validFrom` | ❌ BREAKS PARITY |
| Fact | `valid_until` | `validUntil` | ❌ BREAKS PARITY |
| Fact | `created_at` | `createdAtUtc` | ❌ BREAKS PARITY |
| Fact | — | `category` | ⚠️ .NET extra (Python has no Fact.category) |
| Fact | — | `sourceMessageIds` | ⚠️ .NET extra |
| Preference | `preference` | `preferenceText` | ❌ BREAKS PARITY |
| Preference | `created_at` | `createdAtUtc` | ❌ BREAKS PARITY |
| Preference | — | `sourceMessageIds` | ⚠️ .NET extra |
| ReasoningTrace | `session_id` | `sessionId` | ❌ BREAKS PARITY |
| ReasoningTrace | `task_embedding` | `taskEmbedding` | ❌ BREAKS PARITY |
| ReasoningTrace | `started_at` | `startedAtUtc` | ❌ BREAKS PARITY |
| ReasoningTrace | `completed_at` | `completedAtUtc` | ❌ BREAKS PARITY |
| ReasoningStep | `step_number` | `stepNumber` | ❌ BREAKS PARITY |
| ToolCall | `tool_name` | `toolName` | ❌ BREAKS PARITY |
| ToolCall | `arguments` | `argumentsJson` | ❌ BREAKS PARITY |
| ToolCall | `result` | `resultJson` | ❌ BREAKS PARITY |
| ToolCall | `duration_ms` | `durationMs` | ❌ BREAKS PARITY |
| Tool | `created_at` | `createdAtUtc` | ❌ BREAKS PARITY |
| Tool | `total_calls` | `totalCalls` | ❌ BREAKS PARITY |
| Tool | `successful_calls` | *(missing)* | ❌ MISSING |
| Tool | `failed_calls` | *(missing)* | ❌ MISSING |
| Tool | `total_duration_ms` | *(missing)* | ❌ MISSING |
| Tool | `last_used_at` | *(missing)* | ❌ MISSING |
| Tool | `description` | *(missing)* | ❌ MISSING |

### 2.3 Missing/Extra Nodes

| Node Label | Python | .NET | Status |
|------------|--------|------|--------|
| `Extractor` | ✅ Present | ❌ Missing | Provenance tracking not implemented |
| `Schema` | ✅ Present | ❌ Missing | Schema persistence not implemented |
| `Migration` | ❌ Not present | ✅ Present | .NET-only infrastructure node |
| `MemoryRelationship` | ❌ Not present | ✅ Present (constraint only) | .NET-only phantom label |

### 2.4 Missing/Extra Indexes

| Index | Python | .NET | Status |
|-------|--------|------|--------|
| `entity_location_idx` (Point) | ✅ Present | ❌ Missing | Geospatial not implemented |
| `conversation_session_idx` | ✅ Present | ❌ Missing | Not in .NET SchemaBootstrapper |
| `message_role_idx` | ✅ Present | ❌ Missing | Not in .NET SchemaBootstrapper |
| `entity_canonical_idx` | ✅ Present | ❌ Missing | Not in .NET SchemaBootstrapper |
| `trace_success_idx` | ✅ Present | ❌ Missing | Not in .NET SchemaBootstrapper |
| `schema_name_idx` | ✅ Present | ❌ Missing | Schema persistence not implemented |
| `schema_id_idx` | ✅ Present | ❌ Missing | Schema persistence not implemented |
| `reasoning_step_embedding_idx` (Vector) | ❌ Not present | ✅ Present | .NET extension |

### 2.5 Missing/Extra Constraints

| Constraint | Python | .NET | Status |
|------------|--------|------|--------|
| `tool_name` (Tool.name) | ✅ Present | ❌ Missing | Not in .NET SchemaBootstrapper |
| `relationship_id` (MemoryRelationship.id) | ❌ Not present | ✅ Present | .NET-only |
| `migration_version` (Migration.version) | ❌ Not present | ✅ Present | .NET-only infrastructure |

### 2.6 Missing/Extra Relationships

| Relationship | Python | .NET | Status |
|-------------|--------|------|--------|
| `EXTRACTED_BY` (Entity → Extractor) | ✅ Present | ❌ Missing | Provenance not implemented |
| `HAS_FACT` (Conversation → Fact) | ❌ Not present | ✅ Present | .NET extension |
| `HAS_PREFERENCE` (Conversation → Preference) | ❌ Not present | ✅ Present | .NET extension |
| `IN_SESSION` (ReasoningTrace → Conversation) | ❌ Not present | ✅ Present | .NET extension (reverse of HAS_TRACE) |

### 2.7 Relationship Property Differences

| Relationship | Property | Python | .NET |
|-------------|----------|--------|------|
| `MENTIONS` | `confidence` | ✅ float | ❌ Missing |
| `MENTIONS` | `start_pos` | ✅ int | ❌ Missing |
| `MENTIONS` | `end_pos` | ✅ int | ❌ Missing |
| `RELATED_TO`/`RELATES_TO` | `type`/`relation_type` | ✅ `type` | Uses `relationshipType` |
| `RELATED_TO`/`RELATES_TO` | `updated_at` | ✅ datetime | ❌ Missing |
| `SAME_AS` | `status` | ✅ string | ❌ Missing |
| `SAME_AS` | `updated_at` | ✅ datetime | ❌ Missing |
| `HAS_STEP` | `order` | ✅ int | ❌ Missing |
| `EXTRACTED_FROM` | `confidence` | ✅ float | ❌ Missing |
| `EXTRACTED_FROM` | `start_pos` | ✅ int | ❌ Missing |
| `EXTRACTED_FROM` | `end_pos` | ✅ int | ❌ Missing |
| `EXTRACTED_FROM` | `context` | ✅ string | ❌ Missing |
| `EXTRACTED_FROM` | `created_at` | ✅ datetime | ❌ Missing |

### 2.8 Tool Node Differences

| Feature | Python | .NET |
|---------|--------|------|
| `successful_calls` counter | ✅ Pre-aggregated | ❌ Missing |
| `failed_calls` counter | ✅ Pre-aggregated | ❌ Missing |
| `total_duration_ms` counter | ✅ Pre-aggregated | ❌ Missing |
| `last_used_at` timestamp | ✅ Present | ❌ Missing |
| `description` field | ✅ Present | ❌ Missing |
| Stat update in CREATE_TOOL_CALL | ✅ Atomic update | ❌ Only `totalCalls` incremented |
| Tool.name UNIQUE constraint | ✅ Present | ❌ Missing |

---

### 2.9 Full Difference Table

| # | Element | Type | Python (Canonical) | .NET (Current) | Difference | Fix Required |
|---|---------|------|--------------------|----------------|------------|--------------|
| 1 | Entity→Entity rel | Rel name | `RELATED_TO` | `RELATES_TO` | Different name | P0 — Rename to `RELATED_TO` |
| 2 | Step→ToolCall rel | Rel name | `USES_TOOL` | `USED_TOOL` | Different name | P0 — Rename to `USES_TOOL` |
| 3 | ToolCall→Tool rel | Rel name | `INSTANCE_OF` | `CALLS` | Different name | P0 — Rename to `INSTANCE_OF` |
| 4 | All nodes | Property naming | `snake_case` | `camelCase` | All props differ | P0 — Switch to `snake_case` |
| 5 | Conversation | Property | `session_id` | `sessionId` | Casing | P0 — Fix as part of #4 |
| 6 | Conversation | Property | `created_at` (datetime) | `createdAtUtc` (string) | Name + type | P0 — Fix name and use `datetime()` |
| 7 | Conversation | Property | `updated_at` | `updatedAtUtc` | Name | P0 — Rename |
| 8 | Conversation | Property | `title` | *(missing)* | Missing prop | P1 — Add `title` property |
| 9 | Message | Property | — | `conversationId` | Extra prop | P2 — Keep (denormalization) |
| 10 | Message | Property | — | `sessionId` | Extra prop | P2 — Keep (denormalization) |
| 11 | Message | Property | — | `toolCallIds` | Extra prop | P2 — Keep (denormalization) |
| 12 | Entity | Property | `canonical_name` | `canonicalName` | Casing | P0 — Fix as part of #4 |
| 13 | Entity | Property | `location` (Point) | *(missing)* | Missing prop | P1 — Add geospatial support |
| 14 | Entity | Dynamic labels | `(:Entity:Person)` | *(missing)* | Missing feature | P1 — Add POLE+O labels |
| 15 | Preference | Property | `preference` | `preferenceText` | Different name | P0 — Rename to `preference` |
| 16 | ReasoningTrace | Property | `task_embedding` | `taskEmbedding` | Casing | P0 — Fix as part of #4 |
| 17 | ReasoningStep | Property | `step_number` | `stepNumber` | Casing | P0 — Fix as part of #4 |
| 18 | ToolCall | Property | `tool_name` | `toolName` | Casing | P0 — Fix as part of #4 |
| 19 | ToolCall | Property | `arguments` | `argumentsJson` | Different name | P0 — Rename to `arguments` |
| 20 | ToolCall | Property | `result` | `resultJson` | Different name | P0 — Rename to `result` |
| 21 | ToolCall | Property | `duration_ms` | `durationMs` | Casing | P0 — Fix as part of #4 |
| 22 | ToolCall | Status values | `"pending"`, `"success"`, `"failure"`, `"error"`, `"timeout"`, `"cancelled"` | `"Pending"`, `"Success"`, `"Error"`, `"Cancelled"` | Different values + missing values | P0 — Match Python values |
| 23 | Tool | Properties | 7 properties (full stats) | 3 properties (name, createdAtUtc, totalCalls) | Missing 5 properties | P1 — Add full stats |
| 24 | Tool | Constraint | `tool_name` UNIQUE on `name` | *(missing)* | Missing constraint | P0 — Add constraint |
| 25 | `Extractor` | Node | ✅ Present | ❌ Missing | Missing node type | P1 — Add provenance |
| 26 | `Schema` | Node | ✅ Present | ❌ Missing | Missing node type | P2 — Add schema persistence |
| 27 | `Migration` | Node | ❌ Not in Python | ✅ .NET-only | Extra node | P2 — Keep (.NET infrastructure) |
| 28 | `MemoryRelationship` | Constraint | ❌ Not in Python | ✅ .NET-only | Extra constraint | P1 — Remove (not in Python) |
| 29 | `entity_location_idx` | Point index | ✅ Present | ❌ Missing | Missing index | P1 — Add with geospatial |
| 30 | `conversation_session_idx` | Prop index | ✅ Present | ❌ Missing | Missing index | P0 — Add to SchemaBootstrapper |
| 31 | `message_role_idx` | Prop index | ✅ Present | ❌ Missing | Missing index | P0 — Add to SchemaBootstrapper |
| 32 | `entity_canonical_idx` | Prop index | ✅ Present | ❌ Missing | Missing index | P0 — Add to SchemaBootstrapper |
| 33 | `trace_success_idx` | Prop index | ✅ Present | ❌ Missing | Missing index | P0 — Add to SchemaBootstrapper |
| 34 | `schema_name_idx` | Prop index | ✅ Present | ❌ Missing | Missing index | P2 — Add with Schema node |
| 35 | `schema_id_idx` | Prop index | ✅ Present | ❌ Missing | Missing index | P2 — Add with Schema node |
| 36 | `reasoning_step_embedding_idx` | Vector index | ❌ Not in Python | ✅ .NET-only | Extra index | P2 — Keep (.NET extension) |
| 37 | `MENTIONS` rel | Properties | `confidence`, `start_pos`, `end_pos` | *(no properties)* | Missing rel properties | P1 — Add properties |
| 38 | `HAS_STEP` rel | Properties | `order` (int) | *(no properties)* | Missing rel property | P0 — Add `order` property |
| 39 | `SAME_AS` rel | Properties | `status`, `updated_at` | *(missing)* | Missing rel properties | P1 — Add properties |
| 40 | `EXTRACTED_FROM` rel | Properties | `confidence`, `start_pos`, `end_pos`, `context`, `created_at` | *(no properties)* | Missing rel properties | P1 — Add properties |
| 41 | `EXTRACTED_BY` | Relationship | ✅ Present | ❌ Missing | Missing rel type | P1 — Add with Extractor |
| 42 | `HAS_FACT` | Relationship | ❌ Not in Python | ✅ .NET-only | Extra rel type | P2 — Keep (.NET extension) |
| 43 | `HAS_PREFERENCE` | Relationship | ❌ Not in Python | ✅ .NET-only | Extra rel type | P2 — Keep (.NET extension) |
| 44 | `IN_SESSION` | Relationship | ❌ Not in Python | ✅ .NET-only | Extra rel type | P2 — Keep (.NET extension) |
| 45 | Datetime handling | All nodes | `datetime()` (Neo4j native) | ISO 8601 strings | Different storage type | P0 — Use `datetime()` |

---

## 3. Fix Plan

### P0 — Critical (Breaks Compatibility) — Must Fix Immediately

#### 3.1 Property Naming: Switch to `snake_case`

**What:** All Neo4j property names must use `snake_case` to match Python.

**Files to modify:**
- `src/Neo4j.AgentMemory.Neo4j/Repositories/*.cs` — All 9 repository files, every Cypher query
- `src/Neo4j.AgentMemory.Neo4j/Infrastructure/SchemaBootstrapper.cs` — All property index definitions

**Property mapping (domain model → Neo4j):**

| C# Property (PascalCase) | Current Neo4j | Correct Neo4j (snake_case) |
|--------------------------|---------------|---------------------------|
| `SessionId` | `sessionId` | `session_id` |
| `CreatedAtUtc` | `createdAtUtc` | `created_at` |
| `UpdatedAtUtc` | `updatedAtUtc` | `updated_at` |
| `ConversationId` | `conversationId` | `conversation_id` |
| `CanonicalName` | `canonicalName` | `canonical_name` |
| `SourceMessageIds` | `sourceMessageIds` | `source_message_ids` |
| `ValidFrom` | `validFrom` | `valid_from` |
| `ValidUntil` | `validUntil` | `valid_until` |
| `PreferenceText` | `preferenceText` | `preference` |
| `TaskEmbedding` | `taskEmbedding` | `task_embedding` |
| `StartedAtUtc` | `startedAtUtc` | `started_at` |
| `CompletedAtUtc` | `completedAtUtc` | `completed_at` |
| `StepNumber` | `stepNumber` | `step_number` |
| `TraceId` | `traceId` | `trace_id` |
| `ToolName` | `toolName` | `tool_name` |
| `ArgumentsJson` | `argumentsJson` | `arguments` |
| `ResultJson` | `resultJson` | `result` |
| `DurationMs` | `durationMs` | `duration_ms` |
| `StepId` | `stepId` | `step_id` |
| `TotalCalls` | `totalCalls` | `total_calls` |
| `MergedInto` | `mergedInto` | `merged_into` |
| `MergedAt` | `mergedAt` | `merged_at` |
| `ToolCallIds` | `toolCallIds` | `tool_call_ids` |
| `RelationshipType` | `relationshipType` | `relation_type` |
| `SourceEntityId` | `sourceEntityId` | `source_entity_id` |
| `TargetEntityId` | `targetEntityId` | `target_entity_id` |

**Migration:** Existing databases will need a one-time migration to rename all properties. Create migration `001_SnakeCaseProperties.cs`.

**Impact on tests:** All integration tests will need to be updated to expect `snake_case` properties.

#### 3.2 Relationship Names: Match Python

**What:** Rename three relationship types.

| Current .NET | Correct (Python) | Files |
|-------------|-----------------|-------|
| `RELATES_TO` | `RELATED_TO` | `Neo4jRelationshipRepository.cs` |
| `USED_TOOL` | `USES_TOOL` | `Neo4jToolCallRepository.cs` |
| `CALLS` | `INSTANCE_OF` | `Neo4jToolCallRepository.cs` |

**Migration:** Existing databases will need relationship type migration (create new, copy properties, delete old).

#### 3.3 Datetime Handling: Use `datetime()` Not Strings

**What:** Python uses Neo4j native `datetime()` function. .NET serializes as ISO 8601 strings.

**Files to modify:** All repositories that set timestamp properties.

**Migration:** Convert existing string timestamps to Neo4j datetime values.

#### 3.4 ToolCall Status Values: Lowercase

**What:** Python uses lowercase status values (`"pending"`, `"success"`, `"error"`). .NET uses PascalCase enum names (`"Pending"`, `"Success"`, `"Error"`).

**Files to modify:** `Neo4jToolCallRepository.cs`, `ToolCallStatus.cs` (add `[EnumMember]` attributes or custom serialization).

**Also add missing values:** `"failure"`, `"timeout"`.

#### 3.5 Missing Indexes: Add to SchemaBootstrapper

**Add these property indexes:**
```
conversation_session_idx — Conversation.session_id
message_role_idx — Message.role
entity_canonical_idx — Entity.canonical_name
trace_success_idx — ReasoningTrace.success
```

**File:** `src/Neo4j.AgentMemory.Neo4j/Infrastructure/SchemaBootstrapper.cs`

#### 3.6 Missing Constraint: Tool.name

**Add:** `CREATE CONSTRAINT tool_name IF NOT EXISTS FOR (t:Tool) REQUIRE t.name IS UNIQUE`

**File:** `src/Neo4j.AgentMemory.Neo4j/Infrastructure/SchemaBootstrapper.cs`

#### 3.7 HAS_STEP `order` Property

**What:** Python's `HAS_STEP` relationship carries `{order: $step_number}`. .NET omits this.

**File:** `Neo4jReasoningStepRepository.cs`

### P1 — Important (Missing Features)

| # | Fix | Files | Migration |
|---|-----|-------|-----------|
| P1-1 | Add `Extractor` node + `EXTRACTED_BY` relationship | New repository, SchemaBootstrapper | No (additive) |
| P1-2 | Add POLE+O dynamic entity labels | `Neo4jEntityRepository.cs` | Migration to add labels to existing entities |
| P1-3 | Add geospatial support (`location` property + point index) | `Neo4jEntityRepository.cs`, SchemaBootstrapper | No (additive) |
| P1-4 | Add `MENTIONS` relationship properties | `Neo4jEntityRepository.cs` | Migration to backfill |
| P1-5 | Add `EXTRACTED_FROM` relationship properties | All relevant repositories | Migration to backfill |
| P1-6 | Add full Tool aggregate stats | `Neo4jToolCallRepository.cs` | Migration to compute |
| P1-7 | Add `SAME_AS` status/updated_at properties | `Neo4jEntityRepository.cs` | Migration to backfill |
| P1-8 | Remove `MemoryRelationship` phantom constraint | SchemaBootstrapper, migration | Drop constraint |
| P1-9 | Add `Conversation.title` property | `Neo4jConversationRepository.cs` | No (additive) |

### P2 — Nice-to-Have (Extensions)

| # | Fix | Notes |
|---|-----|-------|
| P2-1 | Add `Schema` node + persistence | Python-only feature, may not be needed |
| P2-2 | Keep `.NET-only` extensions (`HAS_FACT`, `HAS_PREFERENCE`, `IN_SESSION`, `reasoning_step_embedding_idx`) | These are valid .NET additions that don't conflict |
| P2-3 | Keep `Migration` node | .NET infrastructure, Python doesn't need this |

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
