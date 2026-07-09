# Agent Memory for .NET - Neo4j Schema Reference

**Status:** Current, code-aligned schema reference
**Last updated:** 2026-07-09
**Implementation sources:** `SchemaConstants.cs`, `SchemaQueries.cs`, repository query classes, migrations

This document records the active Neo4j schema used by the shipped preview. The implementation source remains authoritative for exact Cypher text; this file summarizes the graph shape, compatibility intent, and bootstrap objects.

## Compatibility Model

The schema is a .NET superset of the Python `neo4j-labs/agent-memory` schema. It preserves the Python labels, relationship types, and snake_case property conventions where applicable, and adds .NET-specific capabilities for owner isolation, temporal invalidation, consolidation, GraphRAG/fulltext retrieval, and operational migrations.

Compatibility is enforced as a guardrail, not a runtime compatibility layer. `agentmemory schema-parity` compares the .NET descriptor against embedded upstream snapshots and a versioned divergence policy. Behavioral compatibility is tracked separately through `neo4j-labs/agent-memory-tck` or mirrored .NET integration scenarios. Snapshot refreshes should follow tagged upstream releases or material Bolt/schema changes, not docs-only upstream churn.

## Node Labels

| Label | Purpose | Notes |
|---|---|---|
| `Conversation` | Short-term conversation container | Carries `session_id`, title, metadata, optional `user_id`, and consolidation archive fields. |
| `Message` | Short-term message | Carries `conversation_id`, `session_id`, role, content, timestamp, metadata, optional embedding. |
| `Entity` | Long-term named entity | Supports POLE+O type/subtype, aliases, attributes, location point, embeddings, provenance, owner scope, invalidation. |
| `Fact` | Long-term subject/predicate/object assertion | MERGE key is `{subject, predicate, object, owner_key}`; carries `owner_id`, `owner_key`, valid-time, transaction-time invalidation, category, provenance. |
| `Preference` | Long-term preference | Carries category, preference text, context, owner scope, invalidation, provenance, embedding. |
| `ReasoningTrace` | Reasoning memory root | Carries session, owner, task, `task_embedding`, outcome, success, timestamps, metadata. |
| `ReasoningStep` | Ordered reasoning step | Carries trace id, step number, thought/action/observation, timestamp, embedding, metadata. |
| `ToolCall` | Tool invocation inside a reasoning step | Carries tool name, arguments/result JSON, status, duration, error, timestamp, metadata. |
| `Tool` | Tool aggregate | Tracks total/success/failed calls, total duration, last-used timestamp, description. |
| `Extractor` | Extraction provenance source | Unique by name. |
| `Schema` | Versioned custom entity-schema document | Managed through `ISchemaManager`/`Neo4jSchemaManager`. |
| `Migration` | Applied migration marker | Used by `MigrationRunner`; has `version` uniqueness. |
| `ConsolidationRun` | Applied memory-hygiene audit record | Written by applied consolidation runs. |
| `MemoryReadAudit` | Read/access audit record | Created when long-term access timestamps are updated; carries read kind, memory id, owner, access count, and read time. |

Entity nodes may also receive type/subtype labels such as `PERSON`, `ORGANIZATION`, `LOCATION`, `EVENT`, and `OBJECT`.

## Relationship Types

| Relationship | Direction | Purpose |
|---|---|---|
| `HAS_MESSAGE` | `Conversation -> Message` | Conversation contains a message. |
| `FIRST_MESSAGE` | `Conversation -> Message` | Start of message chain. |
| `NEXT_MESSAGE` | `Message -> Message` | Ordered message chain. |
| `MENTIONS` | `Message -> Entity` | Message mentions entity; carries confidence, positions, context, created time. |
| `RELATED_TO` | `Entity -> Entity` | Domain relationship; carries id, relation type, owner, valid-time, metadata, attributes, provenance. |
| `ABOUT` | `Fact/Preference -> Entity` | Long-term memory concerns an entity. |
| `SAME_AS` | `Entity -> Entity` | Potential or confirmed duplicate/equivalent entity. |
| `SUPERSEDED_BY` | `Fact/Preference -> Fact/Preference` | Non-destructive supersession; loser is invalidated and points to winner. |
| `HAS_STEP` | `ReasoningTrace -> ReasoningStep` | Trace contains ordered step. |
| `USES_TOOL` | `ReasoningStep -> ToolCall` | Step invoked tool. |
| `INSTANCE_OF` | `ToolCall -> Tool` | Tool call updates aggregate tool node. |
| `TOUCHED` | `ReasoningStep -> Entity` | Reasoning step read or acted on an entity; carries `recorded_at`. |
| `HAS_TRACE` | `Conversation -> ReasoningTrace` | Conversation has trace. |
| `IN_SESSION` | `ReasoningTrace -> Conversation` | Reverse traversal convenience for trace/session linkage. |
| `INITIATED_BY` | `ReasoningTrace -> Message` | Trace initiated by message. |
| `TRIGGERED_BY` | `ToolCall -> Message` | Tool call triggered by message. |
| `EXTRACTED_FROM` | `Entity/Fact/Preference -> Message` | Provenance back to source message. |
| `EXTRACTED_BY` | `Entity -> Extractor` | Provenance back to extractor. |
| `HAS_FACT` | `Conversation -> Fact` | Convenience link from conversation to fact. |
| `HAS_PREFERENCE` | `Conversation -> Preference` | Convenience link from conversation to preference. |

## Core Properties

All Neo4j property names are snake_case. C# domain models use PascalCase at the boundary.

Important cross-cutting properties:

- `id`: stable identifier for node or edge records that need identity.
- `created_at`, `updated_at`, `timestamp`: native Neo4j `datetime()` values.
- `metadata`: JSON string for extensibility.
- `embedding`: vector property where applicable.
- `owner_id`: nullable user/owner scope; null means shared/global.
- `owner_key`: non-null owner merge sentinel, currently used on facts to keep shared and owned triples distinct.
- `invalidated_at`: transaction-time clock; live recall excludes invalidated rows, as-of recall can include earlier beliefs.
- `valid_from`, `valid_until`: valid-time window for facts and relationships.
- `source_message_ids`: extracted-memory provenance list.
- `last_accessed_at`, `access_count`: long-term access reinforcement fields used by decay/reranking.
- `kind`, `memory_id`, `read_at`: read-audit fields on `MemoryReadAudit`; `owner_id` is copied from the read memory node.

## Bootstrap Constraints

`SchemaQueries.Constraints` creates these uniqueness constraints:

| Name | Target |
|---|---|
| `conversation_id` | `Conversation.id` |
| `message_id` | `Message.id` |
| `entity_id` | `Entity.id` |
| `fact_id` | `Fact.id` |
| `preference_id` | `Preference.id` |
| `reasoning_trace_id` | `ReasoningTrace.id` |
| `reasoning_step_id` | `ReasoningStep.id` |
| `tool_call_id` | `ToolCall.id` |
| `tool_name` | `Tool.name` |
| `extractor_name` | `Extractor.name` |
| `consolidation_run_id` | `ConsolidationRun.id` |
| `memory_read_audit_id` | `MemoryReadAudit.id` |

`MigrationRunner` additionally ensures `migration_version` on `Migration.version` for migration tracking.

## Bootstrap Indexes

Fulltext indexes:

| Name | Target |
|---|---|
| `message_content` | `Message.content` |
| `entity_name` | `Entity.name`, `Entity.description` |
| `fact_content` | `Fact.subject`, `Fact.predicate`, `Fact.object` |

Vector indexes, all cosine and configured with `Neo4jOptions.EmbeddingDimensions`:

| Name | Target |
|---|---|
| `message_embedding_idx` | `Message.embedding` |
| `entity_embedding_idx` | `Entity.embedding` |
| `preference_embedding_idx` | `Preference.embedding` |
| `fact_embedding_idx` | `Fact.embedding` |
| `reasoning_step_embedding_idx` | `ReasoningStep.embedding` |
| `task_embedding_idx` | `ReasoningTrace.task_embedding` |

Range/point/relationship indexes:

| Name | Target |
|---|---|
| `conversation_session_idx` | `Conversation.session_id` |
| `message_timestamp_idx` | `Message.timestamp` |
| `message_role_idx` | `Message.role` |
| `entity_type_idx` | `Entity.type` |
| `entity_name_idx` | `Entity.name` |
| `entity_canonical_idx` | `Entity.canonical_name` |
| `fact_category` | `Fact.category` |
| `preference_category_idx` | `Preference.category` |
| `trace_session_idx` | `ReasoningTrace.session_id` |
| `trace_success_idx` | `ReasoningTrace.success` |
| `reasoning_step_timestamp` | `ReasoningStep.timestamp` |
| `tool_call_status_idx` | `ToolCall.status` |
| `schema_name_idx` | `Schema.name` |
| `schema_version_idx` | `Schema.version` |
| `entity_location_idx` | `Entity.location` point index |
| `fact_owner_idx` | `Fact.owner_id` |
| `entity_owner_idx` | `Entity.owner_id` |
| `preference_owner_idx` | `Preference.owner_id` |
| `trace_owner_idx` | `ReasoningTrace.owner_id` |
| `rel_owner_idx` | `RELATED_TO.owner_id` relationship-property index |
| `conversation_archived_idx` | `Conversation.archived` |
| `memory_read_audit_kind_idx` | `MemoryReadAudit.kind` |

## Fact Merge Semantics

Facts are idempotent by owner-scoped triple, not by incoming id:

```cypher
MERGE (f:Fact {subject: ..., predicate: ..., object: ..., owner_key: ...})
```

On create, the incoming id becomes the persisted `id`. On match, the existing id is retained, mutable fields are updated, valid windows are coalesced, and `invalidated_at` is reset for live re-assertion.

## Temporal Semantics

The schema separates two clocks:

- valid time: `valid_from` / `valid_until`, meaning when a fact or relationship is true in the modeled world;
- transaction time: `created_at` / `invalidated_at`, meaning when the system believed the record.

Live recall excludes invalidated records. As-of recall can answer what was believed at a past system time and what was true at a valid time.

## Schema Operations

- `ISchemaBootstrapper.BootstrapAsync()` creates constraints/indexes idempotently and validates vector dimensions.
- `IMigrationRunner` applies versioned `.cypher` migrations and records applied versions.
- `agentmemory schema-check` compares live database objects with `SchemaQueries.BootstrapStatements`.
- `agentmemory schema-parity` compares the .NET schema descriptor against embedded upstream Python snapshots.
