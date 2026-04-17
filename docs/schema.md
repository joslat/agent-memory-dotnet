# Agent Memory for .NET — Definitive Schema Reference

> **Authority:** This document is the single source of truth for the Neo4j graph schema.
> **Canonical source:** Python reference implementation (`Neo4j/agent-memory/src/neo4j_agent_memory/`)
> **Date:** 2026-04-19 (Absolute Zero Schema Parity Audit)
> **Auditor:** Gaff (Neo4j Persistence Engineer)
> **Method:** Line-by-line Cypher extraction from every .cs repository file vs Python analysis

---

## Parity Status: 4 Actionable Gaps Remain

| Category | Python | .NET | Status |
|----------|:---:|:---:|:---:|
| Node labels | 11 | 12 (+Migration) | ✅ All 11 Python labels present |
| Node properties | 73 | 73 + 14 extras | ❌ **2 gaps** — `ReasoningStep.timestamp`, `ToolCall.timestamp` never written |
| Constraints | 9 | 10 (+extractor_name) | ✅ All 9 Python constraints present |
| Property indexes | 12 | 14 (+2 extras) | ✅ All 12 Python indexes present |
| Vector indexes | 5 | 6 (+reasoning_step_embedding_idx) | ✅ All 5 Python indexes present |
| Point indexes | 1 | 1 | ✅ Match |
| Fulltext indexes | 0 | 3 | 🔵 .NET extension |
| Relationship types | 15 | 18 (+3 extras) | ✅ All 15 Python types present |
| Relationship properties | 27 props | 25 of 27 | ❌ **2 gaps** — `MENTIONS.context`, `MENTIONS.created_at` not written |
| Property naming (snake_case) | — | All correct | ✅ Match |
| Datetime storage | Native `datetime()` | Native `datetime()` | ✅ Match |
| Tool aggregate stats | 6 props | 7 (+description) | ✅ All Python props present |
| Schema node repository | ✅ CRUD | ❌ No repository | 🔵 Decided omission (P2) |
| **Weighted overall** | | | **~97%** |

### Summary of Remaining Gaps

| # | Gap | Severity | File(s) to Fix |
|---|-----|----------|---------------|
| G1 | `ReasoningStep` nodes never get `timestamp` property (Python auto-sets `datetime()`) | ❌ HIGH — indexed property never populated | `Neo4jReasoningStepRepository.cs` |
| G2 | `ToolCall` nodes never get `timestamp` property (Python auto-sets `datetime()`) | ❌ HIGH — property expected by Python clients | `Neo4jToolCallRepository.cs` |
| G3 | `MENTIONS` relationship missing `context` and `created_at` properties | ❌ MEDIUM — Python sets 5 props, .NET sets 3 | `Neo4jEntityRepository.cs` |
| G4 | `Schema` node has no repository implementation | 🔵 LOW — indexes exist, .NET uses fixed types | New `Neo4jSchemaRepository.cs` |

---

## Table of Contents

1. [Node Labels](#1-node-labels) — Every node with every property
2. [Relationship Types](#2-relationship-types) — Every relationship with direction and properties
3. [Indexes](#3-indexes) — Every index with name, type, target
4. [Constraints](#4-constraints) — Every constraint
5. [Gap Analysis](#5-gap-analysis) — Complete parity comparison matrix
6. [Required Code Changes](#6-required-code-changes) — Precise fix specifications
7. [Design Differences](#7-design-differences) — Intentional divergences
8. [.NET Extensions](#8-net-extensions) — Properties/relationships beyond Python
9. [Property Naming Convention](#9-property-naming-convention)
10. [Appendix A: POLE+O Entity Types](#appendix-a-poleo-entity-type-system)
11. [Appendix B: Count Summary](#appendix-b-count-summary)

---

## 1. Node Labels

### `Conversation` — Short-Term Memory

| Property | Type | Python | .NET | Required | Default | Notes |
|----------|------|:---:|:---:|:---:|---------|-------|
| `id` | string (UUID) | ✅ | ✅ | ✅ | — | MERGE key |
| `session_id` | string | ✅ | ✅ | ✅ | — | Links to session |
| `title` | string | ✅ | ✅ | ✅ | — | Conversation title |
| `created_at` | datetime | ✅ | ✅ | ✅ | `datetime()` | Auto-set on CREATE |
| `updated_at` | datetime | ✅ | ✅ | ✅ | `datetime()` | Updated on each message add |
| `user_id` | string | ❌ | ✅ | ❌ | — | 🔵 .NET extension — multi-user support |
| `metadata` | string (JSON) | ❌ | ✅ | ❌ | `"{}"` | 🔵 .NET extension — serialized JSON |

**MERGE strategy:** `MERGE (c:Conversation {id: $id})` — identical in both Python and .NET.

### `Message` — Short-Term Memory

| Property | Type | Python | .NET | Required | Default | Notes |
|----------|------|:---:|:---:|:---:|---------|-------|
| `id` | string (UUID) | ✅ | ✅ | ✅ | — | CREATE key |
| `role` | string | ✅ | ✅ | ✅ | — | `"user"`, `"assistant"`, `"system"`, `"tool"` |
| `content` | string | ✅ | ✅ | ✅ | — | Message text |
| `embedding` | list\<float\> | ✅ | ✅ | ❌ | — | Set at creation or backfilled |
| `timestamp` | datetime | ✅ | ✅ | ✅ | `datetime()` | Auto-set |
| `metadata` | string (JSON) | ✅ | ✅ | ❌ | — | Serialized JSON |
| `conversation_id` | string | ❌ | ✅ | ✅ | — | 🔵 .NET extension — denormalization |
| `session_id` | string | ❌ | ✅ | ✅ | — | 🔵 .NET extension — denormalization |
| `tool_call_ids` | list\<string\> | ❌ | ✅ | ❌ | `[]` | 🔵 .NET extension — direct lookup |

### `Entity` — Long-Term Memory

| Property | Type | Python | .NET | Required | Default | Notes |
|----------|------|:---:|:---:|:---:|---------|-------|
| `id` | string (UUID) | ✅ | ✅ | ✅ | — | ON CREATE SET (Python MERGE key = name+type) |
| `name` | string | ✅ | ✅ | ✅ | — | Python MERGE key (with `type`) |
| `type` | string | ✅ | ✅ | ✅ | — | POLE+O: PERSON, OBJECT, LOCATION, EVENT, ORGANIZATION |
| `subtype` | string | ✅ | ✅ | ❌ | — | e.g., INDIVIDUAL, VEHICLE, CITY |
| `canonical_name` | string | ✅ | ✅ | ❌ | — | Normalized name for matching |
| `description` | string | ✅ | ✅ | ❌ | — | Entity description |
| `embedding` | list\<float\> | ✅ | ✅ | ❌ | — | Set at creation or backfilled |
| `confidence` | float | ✅ | ✅ | ❌ | — | Extraction confidence (0.0–1.0) |
| `created_at` | datetime | ✅ | ✅ | ✅ | `datetime()` | Auto-set on CREATE |
| `updated_at` | datetime | ✅ | ✅ | ❌ | `datetime()` | ON MATCH auto-set |
| `metadata` | string (JSON) | ✅ | ✅ | ❌ | — | Serialized JSON |
| `location` | Point | ✅ | ✅ | ❌ | — | `point({latitude:..., longitude:...})` for LOCATION types |
| `aliases` | list\<string\> | ✅ | ✅ | ❌ | `[]` | Alternative names (set during merge) |
| `merged_into` | string (UUID) | ✅ | ✅ | ❌ | — | Set when entity is merged into another |
| `merged_at` | datetime | ✅ | ✅ | ❌ | — | Timestamp of merge |
| `attributes` | string (JSON) | ❌ | ✅ | ❌ | `"{}"` | 🔵 .NET extension — structured attributes |
| `source_message_ids` | list\<string\> | ❌ | ✅ | ❌ | `[]` | 🔵 .NET extension — provenance refs |

**MERGE strategy:** Python uses `MERGE (e:Entity {name: $name, type: $type})`. .NET uses `MERGE (e:Entity {id: $id})`. See [§7 Design Differences](#7-design-differences).

**Dynamic Labels:** Entity nodes get additional labels for type/subtype. Python: PascalCase (`(:Entity:Person:Individual)`). .NET: UPPERCASE (`(:Entity:PERSON:INDIVIDUAL)`). Functionally equivalent.

### `Fact` — Long-Term Memory

| Property | Type | Python | .NET | Required | Default | Notes |
|----------|------|:---:|:---:|:---:|---------|-------|
| `id` | string (UUID) | ✅ | ✅ | ✅ | — | CREATE key |
| `subject` | string | ✅ | ✅ | ✅ | — | Fact subject |
| `predicate` | string | ✅ | ✅ | ✅ | — | Fact predicate |
| `object` | string | ✅ | ✅ | ✅ | — | Fact object |
| `confidence` | float | ✅ | ✅ | ❌ | — | Extraction confidence (0.0–1.0) |
| `embedding` | list\<float\> | ✅ | ✅ | ❌ | — | Set at creation or backfilled |
| `valid_from` | datetime | ✅ (ISO str) | ✅ (datetime) | ❌ | — | Python stores as ISO string; .NET stores as native `datetime()` — improvement |
| `valid_until` | datetime | ✅ (ISO str) | ✅ (datetime) | ❌ | — | Python stores as ISO string; .NET stores as native `datetime()` — improvement |
| `created_at` | datetime | ✅ | ✅ | ✅ | `datetime()` | Auto-set on CREATE |
| `metadata` | string (JSON) | ✅ | ✅ | ❌ | — | Serialized JSON |
| `category` | string | ❌ | ✅ | ❌ | — | 🔵 .NET extension — category grouping (indexed) |
| `source_message_ids` | list\<string\> | ❌ | ✅ | ❌ | `[]` | 🔵 .NET extension — provenance refs |
| `updated_at` | datetime | ❌ | ✅ | ❌ | `datetime()` | 🔵 .NET extension — ON MATCH auto-set |

**MERGE strategy:** Python uses `CREATE (f:Fact {id: ...})`. .NET single upsert uses `MERGE (f:Fact {subject: $subject, predicate: $predicate, object: $object})` (deduplicates identical triples). .NET batch uses `MERGE (f:Fact {id: item.id})`.

### `Preference` — Long-Term Memory

| Property | Type | Python | .NET | Required | Default | Notes |
|----------|------|:---:|:---:|:---:|---------|-------|
| `id` | string (UUID) | ✅ | ✅ | ✅ | — | CREATE key |
| `category` | string | ✅ | ✅ | ✅ | — | Preference category |
| `preference` | string | ✅ | ✅ | ✅ | — | Preference text (C# domain: `PreferenceText`) |
| `context` | string | ✅ | ✅ | ❌ | — | Context of preference |
| `confidence` | float | ✅ | ✅ | ❌ | — | Extraction confidence (0.0–1.0) |
| `embedding` | list\<float\> | ✅ | ✅ | ❌ | — | Set at creation or backfilled |
| `created_at` | datetime | ✅ | ✅ | ✅ | `datetime()` | Auto-set on CREATE |
| `metadata` | string (JSON) | ✅ | ✅ | ❌ | — | Serialized JSON |
| `source_message_ids` | list\<string\> | ❌ | ✅ | ❌ | `[]` | 🔵 .NET extension — provenance refs |

### `ReasoningTrace` — Reasoning Memory

| Property | Type | Python | .NET | Required | Default | Notes |
|----------|------|:---:|:---:|:---:|---------|-------|
| `id` | string (UUID) | ✅ | ✅ | ✅ | — | CREATE key |
| `session_id` | string | ✅ | ✅ | ✅ | — | Links to session |
| `task` | string | ✅ | ✅ | ✅ | — | Task description |
| `task_embedding` | list\<float\> | ✅ | ✅ | ❌ | — | Embedding of task |
| `outcome` | string | ✅ | ✅ | ❌ | — | Set on completion |
| `success` | boolean | ✅ | ✅ | ❌ | — | Set on completion |
| `started_at` | datetime | ✅ | ✅ | ✅ | `datetime()` | Auto-set on CREATE |
| `completed_at` | datetime | ✅ | ✅ | ❌ | — | Set on update |
| `metadata` | string (JSON) | ✅ | ✅ | ❌ | — | Serialized JSON |

### `ReasoningStep` — Reasoning Memory

| Property | Type | Python | .NET | Required | Default | Notes |
|----------|------|:---:|:---:|:---:|---------|-------|
| `id` | string (UUID) | ✅ | ✅ | ✅ | — | CREATE key |
| `step_number` | int | ✅ | ✅ | ✅ | — | Order within trace |
| `thought` | string | ✅ | ✅ | ❌ | — | Step thought |
| `action` | string | ✅ | ✅ | ❌ | — | Step action |
| `observation` | string | ✅ | ✅ | ❌ | — | Step observation (may be set later) |
| `embedding` | list\<float\> | ✅ | ✅ | ❌ | — | Set at creation or backfilled |
| `timestamp` | datetime | ✅ | ❌ **GAP** | ✅ | `datetime()` | ❌ **G1** — Python auto-sets; .NET never writes this. Index exists but property never populated |
| `metadata` | string (JSON) | ✅ | ✅ | ❌ | — | Serialized JSON |
| `trace_id` | string | ❌ | ✅ | ✅ | — | 🔵 .NET extension — direct trace lookup |

### `ToolCall` — Reasoning Memory

| Property | Type | Python | .NET | Required | Default | Notes |
|----------|------|:---:|:---:|:---:|---------|-------|
| `id` | string (UUID) | ✅ | ✅ | ✅ | — | CREATE key |
| `tool_name` | string | ✅ | ✅ | ✅ | — | Name of tool called |
| `arguments` | string (JSON) | ✅ | ✅ | ✅ | — | Serialized arguments (C# domain: `ArgumentsJson`) |
| `result` | string (JSON) | ✅ | ✅ | ❌ | — | Serialized result (C# domain: `ResultJson`) |
| `status` | string | ✅ | ✅ | ✅ | — | Lowercase: `"pending"`, `"success"`, `"error"`, `"cancelled"`, `"failure"`, `"timeout"` |
| `duration_ms` | int/long | ✅ | ✅ | ❌ | — | Execution time in milliseconds |
| `error` | string | ✅ | ✅ | ❌ | — | Error message |
| `timestamp` | datetime | ✅ | ❌ **GAP** | ✅ | `datetime()` | ❌ **G2** — Python auto-sets; .NET never writes this |
| `step_id` | string | ❌ | ✅ | ✅ | — | 🔵 .NET extension — direct step lookup |
| `metadata` | string (JSON) | ❌ | ✅ | ❌ | `"{}"` | 🔵 .NET extension |

### `Tool` — Reasoning Memory (Aggregate)

| Property | Type | Python | .NET | Required | Default | Notes |
|----------|------|:---:|:---:|:---:|---------|-------|
| `name` | string | ✅ | ✅ | ✅ | — | MERGE key (UNIQUE constraint) |
| `created_at` | datetime | ✅ | ✅ | ✅ | `datetime()` | ON CREATE SET |
| `total_calls` | int | ✅ | ✅ | ✅ | `0` | Pre-aggregated counter |
| `successful_calls` | int | ✅ | ✅ | ✅ | `0` | Pre-aggregated counter |
| `failed_calls` | int | ✅ | ✅ | ✅ | `0` | Pre-aggregated counter |
| `total_duration_ms` | int/long | ✅ | ✅ | ✅ | `0` | Pre-aggregated sum |
| `last_used_at` | datetime | ✅ | ✅ | ❌ | `datetime()` | Updated on each call |
| `description` | string | ❌ | ✅ | ❌ | — | 🔵 .NET extension — tool docs |

### `Extractor` — Provenance

| Property | Type | Python | .NET | Required | Default | Notes |
|----------|------|:---:|:---:|:---:|---------|-------|
| `name` | string | ✅ | ✅ | ✅ | — | MERGE key |
| `id` | string (UUID) | ✅ | ✅ | ✅ | — | ON CREATE SET |
| `version` | string | ✅ | ✅ | ❌ | — | Extractor version |
| `config` | string (JSON) | ✅ | ✅ | ❌ | — | Serialized `EntitySchemaConfig` |
| `created_at` | datetime | ✅ | ✅ | ✅ | `datetime()` | ON CREATE SET |

### `Schema` — Schema Persistence

| Property | Type | Python | .NET | Required | Default | Notes |
|----------|------|:---:|:---:|:---:|---------|-------|
| `id` | string (UUID) | ✅ | 🔵 Declared | ✅ | — | CREATE key |
| `name` | string | ✅ | 🔵 Declared | ✅ | — | Schema name (indexed) |
| `version` | string | ✅ | 🔵 Declared | ✅ | — | Schema version (indexed) |
| `description` | string | ✅ | 🔵 Declared | ❌ | — | Description |
| `config` | string (JSON) | ✅ | 🔵 Declared | ❌ | — | Serialized `EntitySchemaConfig` |
| `is_active` | boolean | ✅ | 🔵 Declared | ✅ | — | Active flag |
| `created_at` | datetime | ✅ | 🔵 Declared | ✅ | `datetime()` | Auto-set |
| `created_by` | string | ✅ | 🔵 Declared | ❌ | — | Creator identifier |

> 🔵 **Note:** Schema node is declared in `SchemaConstants` and `SchemaModel` domain class. Indexes `schema_name_idx` and `schema_version_idx` are created by `SchemaBootstrapper`. However, **no Neo4j repository implementation exists** (no CRUD). Python uses this for custom entity schema persistence. .NET uses fixed types. This is a decided P2 omission.

### `Migration` — .NET-only Infrastructure

Not in Python. Used by .NET to track applied schema migrations.

---

## 2. Relationship Types

### 2.1 Structural — Short-Term Memory

| # | Type | Direction | Source | Target | Properties | Python | .NET |
|---|------|-----------|--------|--------|------------|:---:|:---:|
| 1 | `HAS_MESSAGE` | → | `Conversation` | `Message` | — | ✅ | ✅ |
| 2 | `FIRST_MESSAGE` | → | `Conversation` | `Message` | — | ✅ | ✅ |
| 3 | `NEXT_MESSAGE` | → | `Message` | `Message` | — | ✅ | ✅ |

### 2.2 Long-Term Memory

| # | Type | Direction | Source | Target | Properties | Python | .NET |
|---|------|-----------|--------|--------|------------|:---:|:---:|
| 4 | `MENTIONS` | → | `Message` | `Entity` | `confidence` (float), `start_pos` (int), `end_pos` (int), `context` (string), `created_at` (datetime) | ✅ all 5 | ❌ **GAP: 3 of 5** — missing `context`, `created_at` |
| 5 | `RELATED_TO` | → | `Entity` | `Entity` | `id` (UUID), `relation_type` (string), `description` (string), `confidence` (float), `valid_from` (datetime), `valid_until` (datetime), `created_at` (datetime), `updated_at` (datetime) | ✅ | ✅ + extras |
| 6 | `ABOUT` | → | `Preference` | `Entity` | — | ✅ | ✅ |
| 6b | `ABOUT` | → | `Fact` | `Entity` | — | ❌ | ✅ 🔵 .NET extension |
| 7 | `SAME_AS` | ↔ | `Entity` | `Entity` | `confidence` (float), `match_type` (string), `created_at` (datetime), `status` (string), `updated_at` (datetime) | ✅ all 5 | ✅ all 5 |

### 2.3 Reasoning Memory

| # | Type | Direction | Source | Target | Properties | Python | .NET |
|---|------|-----------|--------|--------|------------|:---:|:---:|
| 8 | `HAS_STEP` | → | `ReasoningTrace` | `ReasoningStep` | `order` (int) | ✅ | ✅ |
| 9 | `USES_TOOL` | → | `ReasoningStep` | `ToolCall` | — | ✅ | ✅ |
| 10 | `INSTANCE_OF` | → | `ToolCall` | `Tool` | — | ✅ | ✅ |

### 2.4 Cross-Memory

| # | Type | Direction | Source | Target | Properties | Python | .NET |
|---|------|-----------|--------|--------|------------|:---:|:---:|
| 11 | `HAS_TRACE` | → | `Conversation` | `ReasoningTrace` | — | ✅ | ✅ |
| 12 | `INITIATED_BY` | → | `ReasoningTrace` | `Message` | — | ✅ | ✅ |
| 13 | `TRIGGERED_BY` | → | `ToolCall` | `Message` | — | ✅ | ✅ |

### 2.5 Provenance

| # | Type | Direction | Source | Target | Properties | Python | .NET |
|---|------|-----------|--------|--------|------------|:---:|:---:|
| 14 | `EXTRACTED_FROM` | → | `Entity` | `Message` | `confidence` (float), `start_pos` (int), `end_pos` (int), `context` (string), `created_at` (datetime) | ✅ all 5 | ✅ all 5 |
| 14b | `EXTRACTED_FROM` | → | `Fact` | `Message` | — (bare MERGE) | ❌ | ✅ 🔵 .NET extension |
| 14c | `EXTRACTED_FROM` | → | `Preference` | `Message` | — (bare MERGE) | ❌ | ✅ 🔵 .NET extension |
| 15 | `EXTRACTED_BY` | → | `Entity` | `Extractor` | `confidence` (float), `extraction_time_ms` (int), `created_at` (datetime) | ✅ all 3 | ✅ all 3 |

### 2.6 .NET-Only Relationships

| # | Type | Direction | Source | Target | Properties | Notes |
|---|------|-----------|--------|--------|------------|-------|
| 16 | `HAS_FACT` | → | `Conversation` | `Fact` | — | Conversation → Fact convenience link |
| 17 | `HAS_PREFERENCE` | → | `Conversation` | `Preference` | — | Conversation → Preference convenience link |
| 18 | `IN_SESSION` | → | `ReasoningTrace` | `Conversation` | — | Reverse of HAS_TRACE for bidirectional traversal |

### 2.7 RELATED_TO — .NET Extra Properties

The .NET implementation stores additional properties on `RELATED_TO` beyond the Python schema:

| Property | Type | Python | .NET | Notes |
|----------|------|:---:|:---:|-------|
| `source_entity_id` | string | ❌ | ✅ | Denormalization |
| `target_entity_id` | string | ❌ | ✅ | Denormalization |
| `attributes` | string (JSON) | ❌ | ✅ | Structured attributes |
| `source_message_ids` | list\<string\> | ❌ | ✅ | Provenance |
| `metadata` | string (JSON) | ❌ | ✅ | Rich metadata |

---

## 3. Indexes

### 3.1 Vector Indexes

| Index Name | Label | Property | Dimensions | Similarity | Python | .NET |
|------------|-------|----------|:---:|:---:|:---:|:---:|
| `message_embedding_idx` | `Message` | `embedding` | 1536 (configurable) | cosine | ✅ | ✅ |
| `entity_embedding_idx` | `Entity` | `embedding` | 1536 | cosine | ✅ | ✅ |
| `preference_embedding_idx` | `Preference` | `embedding` | 1536 | cosine | ✅ | ✅ |
| `fact_embedding_idx` | `Fact` | `embedding` | 1536 | cosine | ✅ | ✅ |
| `task_embedding_idx` | `ReasoningTrace` | `task_embedding` | 1536 | cosine | ✅ | ✅ |
| `reasoning_step_embedding_idx` | `ReasoningStep` | `embedding` | 1536 | cosine | ❌ | ✅ 🔵 |

### 3.2 Point Indexes

| Index Name | Label | Property | Python | .NET |
|------------|-------|----------|:---:|:---:|
| `entity_location_idx` | `Entity` | `location` | ✅ | ✅ |

### 3.3 Property Indexes

| Index Name | Label | Property | Python | .NET | Notes |
|------------|-------|----------|:---:|:---:|-------|
| `conversation_session_idx` | `Conversation` | `session_id` | ✅ | ✅ | |
| `message_timestamp_idx` | `Message` | `timestamp` | ✅ | ✅ | |
| `message_role_idx` | `Message` | `role` | ✅ | ✅ | |
| `entity_type_idx` | `Entity` | `type` | ✅ | ✅ | |
| `entity_name_idx` | `Entity` | `name` | ✅ | ✅ | |
| `entity_canonical_idx` | `Entity` | `canonical_name` | ✅ | ✅ | |
| `preference_category_idx` | `Preference` | `category` | ✅ | ✅ | |
| `trace_session_idx` | `ReasoningTrace` | `session_id` | ✅ | ✅ | |
| `trace_success_idx` | `ReasoningTrace` | `success` | ✅ | ✅ | |
| `tool_call_status_idx` | `ToolCall` | `status` | ✅ | ✅ | |
| `schema_name_idx` | `Schema` | `name` | ✅ | ✅ | |
| `schema_version_idx` | `Schema` | `version` | ✅ | ✅ | |
| `fact_category` | `Fact` | `category` | ❌ | ✅ | 🔵 .NET extension |
| `reasoning_step_timestamp` | `ReasoningStep` | `timestamp` | ❌ | ✅ | 🔵 .NET extension — ⚠️ index exists but property never set (G1) |

### 3.4 Fulltext Indexes (.NET-only extensions)

| Index Name | Label | Properties | Python | .NET |
|------------|-------|-----------|:---:|:---:|
| `message_content` | `Message` | `[content]` | ❌ | ✅ 🔵 |
| `entity_name` | `Entity` | `[name, description]` | ❌ | ✅ 🔵 |
| `fact_content` | `Fact` | `[subject, predicate, object]` | ❌ | ✅ 🔵 |

---

## 4. Constraints

| Constraint Name | Label | Property | Type | Python | .NET |
|-----------------|-------|----------|------|:---:|:---:|
| `conversation_id` | `Conversation` | `id` | UNIQUE | ✅ | ✅ |
| `message_id` | `Message` | `id` | UNIQUE | ✅ | ✅ |
| `entity_id` | `Entity` | `id` | UNIQUE | ✅ | ✅ |
| `fact_id` | `Fact` | `id` | UNIQUE | ✅ | ✅ |
| `preference_id` | `Preference` | `id` | UNIQUE | ✅ | ✅ |
| `reasoning_trace_id` | `ReasoningTrace` | `id` | UNIQUE | ✅ | ✅ |
| `reasoning_step_id` | `ReasoningStep` | `id` | UNIQUE | ✅ | ✅ |
| `tool_call_id` | `ToolCall` | `id` | UNIQUE | ✅ | ✅ |
| `tool_name` | `Tool` | `name` | UNIQUE | ✅ | ✅ |
| `extractor_name` | `Extractor` | `name` | UNIQUE | ❌ | ✅ 🔵 |

---

## 5. Gap Analysis — Complete Parity Matrix

### 5.1 Node Property Parity

| # | Node | Property | Python | .NET | Status |
|---|------|----------|--------|------|--------|
| 1 | Conversation | `id` | ✅ | ✅ | ✅ MATCH |
| 2 | Conversation | `session_id` | ✅ | ✅ | ✅ MATCH |
| 3 | Conversation | `title` | ✅ | ✅ | ✅ MATCH |
| 4 | Conversation | `created_at` | ✅ datetime | ✅ datetime | ✅ MATCH |
| 5 | Conversation | `updated_at` | ✅ datetime | ✅ datetime | ✅ MATCH |
| 6 | Conversation | `user_id` | ❌ | ✅ | 🔵 .NET extension |
| 7 | Conversation | `metadata` | ❌ | ✅ | 🔵 .NET extension |
| 8 | Message | `id` | ✅ | ✅ | ✅ MATCH |
| 9 | Message | `role` | ✅ | ✅ | ✅ MATCH |
| 10 | Message | `content` | ✅ | ✅ | ✅ MATCH |
| 11 | Message | `embedding` | ✅ | ✅ | ✅ MATCH |
| 12 | Message | `timestamp` | ✅ datetime | ✅ datetime | ✅ MATCH |
| 13 | Message | `metadata` | ✅ | ✅ | ✅ MATCH |
| 14 | Message | `conversation_id` | ❌ | ✅ | 🔵 .NET extension |
| 15 | Message | `session_id` | ❌ | ✅ | 🔵 .NET extension |
| 16 | Message | `tool_call_ids` | ❌ | ✅ | 🔵 .NET extension |
| 17 | Entity | `id` | ✅ | ✅ | ✅ MATCH |
| 18 | Entity | `name` | ✅ | ✅ | ✅ MATCH |
| 19 | Entity | `type` | ✅ | ✅ | ✅ MATCH |
| 20 | Entity | `subtype` | ✅ | ✅ | ✅ MATCH |
| 21 | Entity | `canonical_name` | ✅ | ✅ | ✅ MATCH |
| 22 | Entity | `description` | ✅ | ✅ | ✅ MATCH |
| 23 | Entity | `embedding` | ✅ | ✅ | ✅ MATCH |
| 24 | Entity | `confidence` | ✅ | ✅ | ✅ MATCH |
| 25 | Entity | `created_at` | ✅ datetime | ✅ datetime | ✅ MATCH |
| 26 | Entity | `updated_at` | ✅ datetime | ✅ datetime | ✅ MATCH |
| 27 | Entity | `metadata` | ✅ | ✅ | ✅ MATCH |
| 28 | Entity | `location` | ✅ Point | ✅ Point | ✅ MATCH |
| 29 | Entity | `aliases` | ✅ | ✅ | ✅ MATCH |
| 30 | Entity | `merged_into` | ✅ | ✅ | ✅ MATCH |
| 31 | Entity | `merged_at` | ✅ | ✅ | ✅ MATCH |
| 32 | Entity | `attributes` | ❌ | ✅ | 🔵 .NET extension |
| 33 | Entity | `source_message_ids` | ❌ | ✅ | 🔵 .NET extension |
| 34 | Fact | `id` | ✅ | ✅ | ✅ MATCH |
| 35 | Fact | `subject` | ✅ | ✅ | ✅ MATCH |
| 36 | Fact | `predicate` | ✅ | ✅ | ✅ MATCH |
| 37 | Fact | `object` | ✅ | ✅ | ✅ MATCH |
| 38 | Fact | `confidence` | ✅ | ✅ | ✅ MATCH |
| 39 | Fact | `embedding` | ✅ | ✅ | ✅ MATCH |
| 40 | Fact | `valid_from` | ✅ ISO str | ✅ datetime | ⚠️ MINOR — .NET uses native datetime (improvement) |
| 41 | Fact | `valid_until` | ✅ ISO str | ✅ datetime | ⚠️ MINOR — .NET uses native datetime (improvement) |
| 42 | Fact | `created_at` | ✅ datetime | ✅ datetime | ✅ MATCH |
| 43 | Fact | `metadata` | ✅ | ✅ | ✅ MATCH |
| 44 | Fact | `category` | ❌ | ✅ | 🔵 .NET extension |
| 45 | Fact | `source_message_ids` | ❌ | ✅ | 🔵 .NET extension |
| 46 | Fact | `updated_at` | ❌ | ✅ | 🔵 .NET extension |
| 47 | Preference | `id` | ✅ | ✅ | ✅ MATCH |
| 48 | Preference | `category` | ✅ | ✅ | ✅ MATCH |
| 49 | Preference | `preference` | ✅ | ✅ | ✅ MATCH |
| 50 | Preference | `context` | ✅ | ✅ | ✅ MATCH |
| 51 | Preference | `confidence` | ✅ | ✅ | ✅ MATCH |
| 52 | Preference | `embedding` | ✅ | ✅ | ✅ MATCH |
| 53 | Preference | `created_at` | ✅ datetime | ✅ datetime | ✅ MATCH |
| 54 | Preference | `metadata` | ✅ | ✅ | ✅ MATCH |
| 55 | Preference | `source_message_ids` | ❌ | ✅ | 🔵 .NET extension |
| 56 | ReasoningTrace | `id` | ✅ | ✅ | ✅ MATCH |
| 57 | ReasoningTrace | `session_id` | ✅ | ✅ | ✅ MATCH |
| 58 | ReasoningTrace | `task` | ✅ | ✅ | ✅ MATCH |
| 59 | ReasoningTrace | `task_embedding` | ✅ | ✅ | ✅ MATCH |
| 60 | ReasoningTrace | `outcome` | ✅ | ✅ | ✅ MATCH |
| 61 | ReasoningTrace | `success` | ✅ | ✅ | ✅ MATCH |
| 62 | ReasoningTrace | `started_at` | ✅ datetime | ✅ datetime | ✅ MATCH |
| 63 | ReasoningTrace | `completed_at` | ✅ datetime | ✅ datetime | ✅ MATCH |
| 64 | ReasoningTrace | `metadata` | ✅ | ✅ | ✅ MATCH |
| 65 | ReasoningStep | `id` | ✅ | ✅ | ✅ MATCH |
| 66 | ReasoningStep | `step_number` | ✅ | ✅ | ✅ MATCH |
| 67 | ReasoningStep | `thought` | ✅ | ✅ | ✅ MATCH |
| 68 | ReasoningStep | `action` | ✅ | ✅ | ✅ MATCH |
| 69 | ReasoningStep | `observation` | ✅ | ✅ | ✅ MATCH |
| 70 | ReasoningStep | `embedding` | ✅ | ✅ | ✅ MATCH |
| 71 | ReasoningStep | `timestamp` | ✅ datetime | ❌ **NEVER SET** | ❌ **GAP G1** |
| 72 | ReasoningStep | `metadata` | ✅ | ✅ | ✅ MATCH |
| 73 | ReasoningStep | `trace_id` | ❌ | ✅ | 🔵 .NET extension |
| 74 | ToolCall | `id` | ✅ | ✅ | ✅ MATCH |
| 75 | ToolCall | `tool_name` | ✅ | ✅ | ✅ MATCH |
| 76 | ToolCall | `arguments` | ✅ | ✅ | ✅ MATCH |
| 77 | ToolCall | `result` | ✅ | ✅ | ✅ MATCH |
| 78 | ToolCall | `status` | ✅ | ✅ | ✅ MATCH |
| 79 | ToolCall | `duration_ms` | ✅ | ✅ | ✅ MATCH |
| 80 | ToolCall | `error` | ✅ | ✅ | ✅ MATCH |
| 81 | ToolCall | `timestamp` | ✅ datetime | ❌ **NEVER SET** | ❌ **GAP G2** |
| 82 | ToolCall | `step_id` | ❌ | ✅ | 🔵 .NET extension |
| 83 | ToolCall | `metadata` | ❌ | ✅ | 🔵 .NET extension |
| 84 | Tool | `name` | ✅ | ✅ | ✅ MATCH |
| 85 | Tool | `created_at` | ✅ | ✅ | ✅ MATCH |
| 86 | Tool | `total_calls` | ✅ | ✅ | ✅ MATCH |
| 87 | Tool | `successful_calls` | ✅ | ✅ | ✅ MATCH |
| 88 | Tool | `failed_calls` | ✅ | ✅ | ✅ MATCH |
| 89 | Tool | `total_duration_ms` | ✅ | ✅ | ✅ MATCH |
| 90 | Tool | `last_used_at` | ✅ | ✅ | ✅ MATCH |
| 91 | Tool | `description` | ❌ | ✅ | 🔵 .NET extension |
| 92 | Extractor | `name` | ✅ | ✅ | ✅ MATCH |
| 93 | Extractor | `id` | ✅ | ✅ | ✅ MATCH |
| 94 | Extractor | `version` | ✅ | ✅ | ✅ MATCH |
| 95 | Extractor | `config` | ✅ | ✅ | ✅ MATCH |
| 96 | Extractor | `created_at` | ✅ | ✅ | ✅ MATCH |
| 97–104 | Schema | all 8 props | ✅ | 🔵 Declared | 🔵 DECIDED OMISSION (P2) |

### 5.2 Relationship Property Parity

| # | Relationship | Property | Python | .NET | Status |
|---|-------------|----------|--------|------|--------|
| R1 | `MENTIONS` | `confidence` | ✅ float | ✅ | ✅ MATCH |
| R2 | `MENTIONS` | `start_pos` | ✅ int | ✅ | ✅ MATCH |
| R3 | `MENTIONS` | `end_pos` | ✅ int | ✅ | ✅ MATCH |
| R4 | `MENTIONS` | `context` | ✅ string | ❌ **NOT SET** | ❌ **GAP G3a** |
| R5 | `MENTIONS` | `created_at` | ✅ datetime | ❌ **NOT SET** | ❌ **GAP G3b** |
| R6 | `RELATED_TO` | `id` | ✅ | ✅ | ✅ MATCH |
| R7 | `RELATED_TO` | `relation_type` | ✅ | ✅ | ✅ MATCH |
| R8 | `RELATED_TO` | `description` | ✅ | ✅ | ✅ MATCH |
| R9 | `RELATED_TO` | `confidence` | ✅ | ✅ | ✅ MATCH |
| R10 | `RELATED_TO` | `valid_from` | ✅ | ✅ | ✅ MATCH |
| R11 | `RELATED_TO` | `valid_until` | ✅ | ✅ | ✅ MATCH |
| R12 | `RELATED_TO` | `created_at` | ✅ | ✅ | ✅ MATCH |
| R13 | `RELATED_TO` | `updated_at` | ✅ | ✅ | ✅ MATCH |
| R14 | `SAME_AS` | `confidence` | ✅ | ✅ | ✅ MATCH |
| R15 | `SAME_AS` | `match_type` | ✅ | ✅ | ✅ MATCH |
| R16 | `SAME_AS` | `created_at` | ✅ | ✅ | ✅ MATCH |
| R17 | `SAME_AS` | `status` | ✅ | ✅ | ✅ MATCH |
| R18 | `SAME_AS` | `updated_at` | ✅ | ✅ | ✅ MATCH |
| R19 | `HAS_STEP` | `order` | ✅ int | ✅ | ✅ MATCH |
| R20 | `EXTRACTED_FROM` (Entity→Msg) | `confidence` | ✅ | ✅ | ✅ MATCH |
| R21 | `EXTRACTED_FROM` | `start_pos` | ✅ | ✅ | ✅ MATCH |
| R22 | `EXTRACTED_FROM` | `end_pos` | ✅ | ✅ | ✅ MATCH |
| R23 | `EXTRACTED_FROM` | `context` | ✅ | ✅ | ✅ MATCH |
| R24 | `EXTRACTED_FROM` | `created_at` | ✅ | ✅ | ✅ MATCH |
| R25 | `EXTRACTED_BY` | `confidence` | ✅ | ✅ | ✅ MATCH |
| R26 | `EXTRACTED_BY` | `extraction_time_ms` | ✅ | ✅ | ✅ MATCH |
| R27 | `EXTRACTED_BY` | `created_at` | ✅ | ✅ | ✅ MATCH |

---

## 6. Required Code Changes

> **DO NOT make these changes without this section as specification.** Each gap specifies exactly what Cypher should change.

### G1 — Add `timestamp` to ReasoningStep CREATE

**File:** `src/Neo4j.AgentMemory.Neo4j/Repositories/Neo4jReasoningStepRepository.cs`

**Current Cypher (AddAsync, ~line 27):**
```cypher
CREATE (s:ReasoningStep {
    id:          $id,
    trace_id:    $traceId,
    step_number: $stepNumber,
    thought:     $thought,
    action:      $action,
    observation: $observation,
    metadata:    $metadata
})
```

**Required Cypher:**
```cypher
CREATE (s:ReasoningStep {
    id:          $id,
    trace_id:    $traceId,
    step_number: $stepNumber,
    thought:     $thought,
    action:      $action,
    observation: $observation,
    timestamp:   datetime(),
    metadata:    $metadata
})
```

**Domain model change:** The `ReasoningStep` C# class may need a `TimestampUtc` property (check if it exists). The mapper should read `node["timestamp"]` using `Neo4jDateTimeHelper.ReadDateTimeOffset`.

**Impact:** The `reasoning_step_timestamp` index in `SchemaBootstrapper` currently indexes a property that is never set. After this fix it will work correctly.

---

### G2 — Add `timestamp` to ToolCall CREATE

**File:** `src/Neo4j.AgentMemory.Neo4j/Repositories/Neo4jToolCallRepository.cs`

**Current Cypher (AddAsync, ~line 27):**
```cypher
CREATE (tc:ToolCall {
    id:          $id,
    step_id:     $stepId,
    tool_name:   $toolName,
    arguments:   $arguments,
    result:      $result,
    status:      $status,
    duration_ms: $durationMs,
    error:       $error,
    metadata:    $metadata
})
```

**Required Cypher:**
```cypher
CREATE (tc:ToolCall {
    id:          $id,
    step_id:     $stepId,
    tool_name:   $toolName,
    arguments:   $arguments,
    result:      $result,
    status:      $status,
    duration_ms: $durationMs,
    error:       $error,
    timestamp:   datetime(),
    metadata:    $metadata
})
```

**Domain model change:** The `ToolCall` C# class may need a `TimestampUtc` property. The mapper should read `node["timestamp"]` using `Neo4jDateTimeHelper`.

---

### G3 — Add `context` and `created_at` to MENTIONS relationship

**File:** `src/Neo4j.AgentMemory.Neo4j/Repositories/Neo4jEntityRepository.cs`

**Current Cypher (AddMentionAsync, ~line 224):**
```cypher
MATCH (m:Message {id: $messageId})
MATCH (e:Entity {id: $entityId})
MERGE (m)-[r:MENTIONS]->(e)
ON CREATE SET r.confidence = $confidence, r.start_pos = $startPos, r.end_pos = $endPos
```

**Required Cypher:**
```cypher
MATCH (m:Message {id: $messageId})
MATCH (e:Entity {id: $entityId})
MERGE (m)-[r:MENTIONS]->(e)
ON CREATE SET r.confidence = $confidence, r.start_pos = $startPos, r.end_pos = $endPos, r.context = $context, r.created_at = datetime()
```

**Parameter addition:** Add `context` parameter to the method signature and `new { ..., context = (object?)context }` to the parameter object.

**Interface change:** `IEntityRepository.AddMentionAsync` needs an optional `string? context = null` parameter.

**Also update AddMentionsBatchAsync (~line 240):** Currently only sets `confidence`. Should also set `created_at = datetime()` on CREATE.

---

### G4 — Schema Node Repository (P2 — Low Priority)

**File:** New `src/Neo4j.AgentMemory.Neo4j/Repositories/Neo4jSchemaRepository.cs`

Python uses the Schema node for custom entity schema persistence (defining entity types, subtypes, relationship types via YAML/JSON config). .NET currently uses fixed types defined in `DefaultSchemas.cs`.

**Required operations (from Python):**
```cypher
-- Create schema
CREATE (s:Schema {
    id:          $id,
    name:        $name,
    version:     $version,
    description: $description,
    config:      $config,
    is_active:   $isActive,
    created_at:  datetime(),
    created_by:  $createdBy
})

-- Get active schema
MATCH (s:Schema {is_active: true}) RETURN s

-- List schemas
MATCH (s:Schema) RETURN s ORDER BY s.created_at DESC

-- Deactivate all then activate one
MATCH (s:Schema) SET s.is_active = false
WITH 1 AS dummy
MATCH (s:Schema {id: $id}) SET s.is_active = true RETURN s
```

**Status:** 🔵 Decided omission. Indexes (`schema_name_idx`, `schema_version_idx`) exist. Domain model (`SchemaModel`) exists. Only the repository implementation is missing. Low priority unless custom schema support is needed.

---

## 7. Design Differences

These are intentional divergences that are NOT gaps.

| # | Feature | Python | .NET | Rationale |
|---|---------|--------|------|-----------|
| D1 | Entity MERGE key | `MERGE {name: $name, type: $type}` | `MERGE {id: $id}` | .NET is stricter — UUID-based prevents accidental merges of different entities with same name+type. Entity resolution is handled at a higher layer |
| D2 | Fact MERGE strategy | `CREATE (f:Fact {id: ...})` | `MERGE (f:Fact {subject: $s, predicate: $p, object: $o})` | .NET deduplicates identical triples — idempotent. Batch uses id-based MERGE |
| D3 | Dynamic label casing | PascalCase (`Person`) | UPPERCASE (`PERSON`) | .NET follows POLE+O uppercase convention. Functionally equivalent for Cypher queries |
| D4 | Fact.valid_from/until type | ISO string | Native `datetime()` | .NET uses native temporal type — improvement over Python's string storage |
| D5 | `RELATED_TO` vs Python `type` prop | Python has `type` property | .NET uses `relation_type` | Same semantic meaning; .NET name is clearer and avoids collision with Neo4j's `type()` function |

---

## 8. .NET Extensions

Properties, relationships, and indexes that exist in .NET but NOT in Python. These add value and do not break parity.

### 8.1 Extra Node Properties

| Node | Property | Purpose |
|------|----------|---------|
| Conversation | `user_id` | Multi-user support |
| Conversation | `metadata` | Rich metadata storage |
| Message | `conversation_id` | Denormalization for direct query |
| Message | `session_id` | Denormalization for session-scoped queries |
| Message | `tool_call_ids` | Direct tool call lookup |
| Entity | `attributes` (JSON) | Structured entity attributes |
| Entity | `source_message_ids` | Provenance tracking |
| Fact | `category` | Category-based grouping and filtering |
| Fact | `source_message_ids` | Provenance tracking |
| Fact | `updated_at` | Temporal tracking on MATCH |
| Preference | `source_message_ids` | Provenance tracking |
| ReasoningStep | `trace_id` | Direct trace lookup (vs graph traversal) |
| ToolCall | `step_id` | Direct step lookup (vs graph traversal) |
| ToolCall | `metadata` | Rich metadata storage |
| Tool | `description` | Tool documentation |

### 8.2 Extra Relationships

| Relationship | Direction | Purpose |
|-------------|-----------|---------|
| `HAS_FACT` | Conversation → Fact | Direct conversation-to-fact link |
| `HAS_PREFERENCE` | Conversation → Preference | Direct conversation-to-preference link |
| `IN_SESSION` | ReasoningTrace → Conversation | Bidirectional trace-conversation traversal |
| `ABOUT` (Fact→Entity) | Fact → Entity | Link facts to entities they're about |
| `EXTRACTED_FROM` (Fact→Msg) | Fact → Message | Fact provenance tracking |
| `EXTRACTED_FROM` (Pref→Msg) | Preference → Message | Preference provenance tracking |

### 8.3 Extra RELATED_TO Properties

| Property | Purpose |
|----------|---------|
| `source_entity_id` | Denormalization |
| `target_entity_id` | Denormalization |
| `attributes` (JSON) | Structured relationship attributes |
| `source_message_ids` | Provenance tracking |
| `metadata` (JSON) | Rich metadata |

### 8.4 Extra Indexes

| Index | Type | Purpose |
|-------|------|---------|
| `reasoning_step_embedding_idx` | Vector | Semantic step search |
| `fact_category` | Property | Category filtering |
| `reasoning_step_timestamp` | Property | Temporal ordering (⚠️ needs G1 fix) |
| `message_content` | Fulltext | Message text search |
| `entity_name` | Fulltext | Entity name/description search |
| `fact_content` | Fulltext | Fact triple text search |

### 8.5 Extra Constraint

| Constraint | Purpose |
|-----------|---------|
| `extractor_name` (Extractor.name UNIQUE) | Prevent duplicate extractor registrations |

---

## 9. Property Naming Convention

### The Definitive Ruling

**Neo4j properties MUST use `snake_case`.** This is not a style choice — it is a schema compatibility requirement.

### What Does the Python Schema Actually Use?

Python uses `snake_case` for ALL Neo4j properties without exception:
- `session_id`, `created_at`, `updated_at`, `canonical_name`, `task_embedding`
- `step_number`, `tool_name`, `duration_ms`, `valid_from`, `valid_until`
- `merged_into`, `merged_at`, `start_pos`, `end_pos`, `match_type`
- `total_calls`, `successful_calls`, `failed_calls`, `total_duration_ms`, `last_used_at`
- `is_active`, `created_by`, `extraction_time_ms`
- Single-word properties: `id`, `name`, `type`, `role`, `content`, `embedding`, `timestamp`, `confidence`, `description`, `metadata`, `status`, `error`

### Translation Boundary

```
C# Domain Model (PascalCase) → Repository Cypher (snake_case) → Neo4j (snake_case)
     SessionId               →      session_id               →   session_id
     CreatedAtUtc             →      created_at               →   created_at
     PreferenceText           →      preference               →   preference
     ArgumentsJson            →      arguments                →   arguments
```

---

## Appendix A: POLE+O Entity Type System

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

| Category | Python | .NET | Delta | Notes |
|----------|:---:|:---:|:---:|-------|
| Node labels | 11 | 12 | +1 | Migration (.NET infra) |
| Node properties (Python set) | 73 | 71 of 73 | -2 | **G1, G2**: ReasoningStep.timestamp, ToolCall.timestamp |
| Node properties (.NET extras) | — | +14 | +14 | See §8.1 |
| Relationship types | 15 | 18 | +3 | HAS_FACT, HAS_PREFERENCE, IN_SESSION |
| Relationship properties (Python set) | 27 | 25 of 27 | -2 | **G3**: MENTIONS.context, MENTIONS.created_at |
| Relationship properties (.NET extras) | — | +5 | +5 | RELATED_TO extras (§8.3) |
| Constraints | 9 | 10 | +1 | extractor_name |
| Vector indexes | 5 | 6 | +1 | reasoning_step_embedding_idx |
| Point indexes | 1 | 1 | 0 | Match |
| Property indexes | 12 | 14 | +2 | fact_category, reasoning_step_timestamp |
| Fulltext indexes | 0 | 3 | +3 | message_content, entity_name, fact_content |

### Path to Absolute Zero

| Step | Gap | Effort | Impact |
|------|-----|--------|--------|
| 1 | **G1**: Add `timestamp: datetime()` to ReasoningStep CREATE | ~15 min | Fixes indexed-but-empty property |
| 2 | **G2**: Add `timestamp: datetime()` to ToolCall CREATE | ~15 min | Python compatibility |
| 3 | **G3**: Add `context`, `created_at` to MENTIONS ON CREATE SET | ~30 min | Full relationship property parity |
| 4 | **G4**: Schema node repository | ~2-3 days | True 100% (low priority) |

After G1–G3: **99.7% parity** (all Python properties written, only Schema repo missing).
After G1–G4: **100% parity — Absolute Zero.**
