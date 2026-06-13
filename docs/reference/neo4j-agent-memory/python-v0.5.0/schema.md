# neo4j-agent-memory — schema reference (Python v0.5.0)

> **Source:** `neo4j-labs/agent-memory` @ `f29ae8dd8` (2026-06-03), Python package **v0.5.0** · captured 2026-06-07.
> **Extracted from:** `graph/schema.py` (constraints/indexes), `graph/queries.py` (`CREATE_*` properties + relationships), `schema/models.py` (ontology), `schema/persistence.py` (`:Schema` storage).
> **Backend note:** this is the **bolt / direct-Neo4j** storage schema. The **NAMS** hosted backend (v0.4+) does not expose Cypher schema management — `client.graph` raises `NotSupportedError` and you go through `client.query.cypher(...)`.
> Machine-readable form: [`schema.json`](./schema.json) · DDL: [`schema.cypher`](./schema.cypher).

---

## Two schema layers

1. **Storage / graph schema** — the Neo4j node labels, relationship types, properties, constraints and indexes the library manages (this doc + `schema.cypher`).
2. **Domain ontology** — the *entity types* (`Entity.type` values) and *semantic relation types* (POLE+O: `KNOWS`, `OWNS`, …). The ontology is **configurable** (`poleo` | `legacy` | `custom`) and can be persisted/versioned as `:Schema` nodes. Importantly, semantic relations are stored as the **`type` property** on a single physical `(:Entity)-[:RELATED_TO]->(:Entity)` edge — *not* as distinct Neo4j relationship types.

---

## Node labels

| Label | Unique | Key properties | Indexed | Vector / Point | Since |
|---|---|---|---|---|---|
| **Conversation** | `id` | `id`, `session_id`, `title`, `created_at`, `updated_at`, `archived`, `archived_at` | `session_id`, `archived` | — | 0.1 |
| **Message** | `id` | `id`, `role`, `content`, `embedding`, `timestamp`, `metadata` | `timestamp`, `role` | vec `embedding` | 0.1 |
| **Entity** | `id` (MERGE on `name`+`type`) | `id`, `name`, `type`, `subtype`, `canonical_name`, `aliases`, `description`, `embedding`, `confidence`, `location`, `merged_into`, `merged_at`, `created_at`, `updated_at`, `metadata` | `type`, `name`, `canonical_name` | vec `embedding`, point `location` | 0.1 |
| **Preference** | `id` | `id`, `category`, `preference`, `context`, `confidence`, `embedding`, `valid_from`, `valid_until`, `created_at`, `metadata` | `category` | vec `embedding` | 0.1 |
| **Fact** | `id` | `id`, `subject`, `predicate`, `object`, `confidence`, `embedding`, `valid_from`, `valid_until`, `created_at`, `metadata` | — | vec `embedding` | 0.1 |
| **ReasoningTrace** | `id` | `id`, `session_id`, `task`, `task_embedding`, `outcome`, `success`, `error_kind`, `started_at`, `completed_at`, `metadata` | `session_id`, `success`, `error_kind` | vec `task_embedding` | 0.1 |
| **ReasoningStep** | `id` | `id`, `step_number`, `thought`, `action`, `observation`, `embedding`, `timestamp`, `metadata` | — | vec `embedding` | 0.1 |
| **Tool** | `name` | `name`, `created_at`, `total_calls`, `successful_calls`, `failed_calls`, `total_duration_ms`, `last_used_at`, `description` | — | — | 0.1 |
| **ToolCall** | `id` | `id`, `tool_name`, `arguments`, `result`, `status`, `duration_ms`, `error`, `timestamp` | `status` | — | 0.1 |
| **Extractor** | (MERGE on `name`) | `name`, `id`, `version`, `config`, `created_at` | — | — | 0.1 |
| **User** 🆕 | `identifier` | `id`, `identifier`, `attributes` | `identifier` | — | **0.4** |
| **ConsolidationRun** 🆕 | `id` | `id`, `kind`, `ran_at`, `dry_run`, `candidate_count`, `actions_taken` | `kind` | — | **0.5** |
| **MemoryReadAudit** 🆕 | `id` | `id`, `kind` | `kind` | — | **0.5** |
| **Schema** | — | `id`, `name`, `version`, `description`, `config` (JSON), `is_active`, `created_at`, `created_by` | `name`, `id` | — | 0.1 |

🆕 = new since the early (~v0.1) baseline the .NET port mirrors.

> **Entity labels are dynamic.** Real entity creation uses `build_create_entity_query()`, which adds the type/subtype as extra labels — e.g. `(:Entity:OBJECT:VEHICLE)` — so every Entity also carries its POLE+O type and subtype as Neo4j labels (the static `CREATE_ENTITY` constant is reference-only). `User`, `ConsolidationRun`, `MemoryReadAudit` have constraints/models but their write Cypher lives outside `graph/queries.py`, so their full property set is partly inferred. **No fulltext indexes** are defined.

---

## Relationship types

| Type | Pattern | Properties | Notes |
|---|---|---|---|
| `HAS_MESSAGE` | `(Conversation)→(Message)` | — | |
| `FIRST_MESSAGE` | `(Conversation)→(Message)` | — | head of the message chain |
| `NEXT_MESSAGE` | `(Message)→(Message)` | — | linked-list ordering |
| `MENTIONS` | `(Message)→(Entity)` | `confidence`, `start_pos`, `end_pos` | extraction provenance |
| `ABOUT` | `(Preference)→(Entity)` | — | preference subject |
| `EXTRACTED_FROM` | `(Entity)→(Message)` | `confidence`, `start_pos`, `end_pos`, `context`, `created_at` | mention origin |
| `EXTRACTED_BY` | `(Entity)→(Extractor)` | `confidence`, `extraction_time_ms`, `created_at` | which extractor produced it |
| `RELATED_TO` | `(Entity)→(Entity)` | `id`, `type`, `description`, `confidence`, `valid_from`, `valid_until`, `created_at` | **MERGE on `type`**; the semantic relation (KNOWS/OWNS/…) is the `type` *property* |
| `HAS_STEP` | `(ReasoningTrace)→(ReasoningStep)` | `order` | |
| `USES_TOOL` | `(ReasoningStep)→(ToolCall)` | — | |
| `INSTANCE_OF` | `(ToolCall)→(Tool)` | — | |
| `HAS_TRACE` | `(Conversation)→(ReasoningTrace)` | — | |
| `INITIATED_BY` | `(ReasoningTrace)→(Message)` | — | |
| `TRIGGERED_BY` | `(ToolCall)→(Message)` | — | |
| `TOUCHED` 🆕 | `(ReasoningStep)→(Entity)` | `recorded_at` | audit/provenance: entities a step read/acted on (v0.2) |
| `SAME_AS` 🆕 | `(Entity)→(Entity)` | `confidence`, `match_type`, `status`, `created_at` | **non-destructive** dedup link (v0.2 consolidation); both nodes kept |
| `SUPERSEDED_BY` 🆕 | `(Preference)→(Preference)` | — | **invalidate-not-delete** supersession (v0.5); closes old `valid_until`, keeps old node |

---

## Constraints (uniqueness)

`conversation_id`, `message_id`, `entity_id`, `preference_id`, `fact_id`, `reasoning_trace_id`, `reasoning_step_id`, `tool_name` (on `Tool.name`), `tool_call_id`, **`user_identifier`** (on `User.identifier`, v0.4), **`consolidation_run_id`** (v0.5), **`memory_read_audit_id`** (v0.5).

## Range / btree indexes

`conversation_session_idx`, `message_timestamp_idx`, `message_role_idx`, `entity_type_idx`, `entity_name_idx`, `entity_canonical_idx`, `preference_category_idx`, `trace_session_idx`, `trace_success_idx`, **`trace_error_kind_idx`**, `tool_call_status_idx`, **`conversation_archived_idx`** (v0.5), **`consolidation_run_kind_idx`** (v0.5), **`memory_read_audit_kind_idx`** (v0.5), plus `schema_name_idx` / `schema_id_idx`.

## Vector indexes (dimensions 1536, cosine)

`message_embedding_idx` (Message.embedding), `entity_embedding_idx` (Entity.embedding), `preference_embedding_idx` (Preference.embedding), `fact_embedding_idx` (Fact.embedding), `task_embedding_idx` (ReasoningTrace.task_embedding), `step_embedding_idx` (ReasoningStep.embedding).

## Point index (geospatial)

`entity_location_idx` (Entity.location — Neo4j `Point`).

---

## Domain ontology (POLE+O)

**Entity types** (values of `Entity.type`): `PERSON`, `OBJECT`, `LOCATION`, `EVENT`, `ORGANIZATION` — each with subtypes (e.g. PERSON → INDIVIDUAL/SUSPECT/WITNESS/VICTIM…). Legacy types (`CONCEPT`, `EMOTION`, `PREFERENCE`, `FACT`) map onto POLE+O for back-compat.

**Semantic relation types** (stored as `RELATED_TO.type`): `KNOWS`, `ALIAS_OF`, `MEMBER_OF`, `EMPLOYED_BY`, `OWNS`, `USES`, `LOCATED_AT`, `RESIDES_AT`, `HEADQUARTERS_AT`, `PARTICIPATED_IN`, `OCCURRED_AT`, `INVOLVED`, `SUBSIDIARY_OF`, `PARTNER_WITH`, `RELATED_TO`, `MENTIONS` — each with valid source/target types and optional edge properties (see `schema.json` → `ontology.relation_types`).

---

## Temporal posture (important)

- **Valid-time** fields (`valid_from`/`valid_until`) exist on **Fact** and **RELATED_TO**, and now on **Preference**.
- **No transaction-time axis** — there is **no `invalidated_at` / `expired_at`** anywhere in constraints, indexes, or `CREATE_*` statements.
- **Point-in-time (`as_of`) recall** (v0.5) is **valid-time only** and currently wired for **Preferences** (`get_preferences_for(as_of=)`); Facts/Entities have no `as_of` read path (recall is plain vector search).
- **Forgetting is non-destructive**: dedup → `:SAME_AS` (keeps both), conversation expiry → `archived=true`, preference supersession → `valid_until` + `:SUPERSEDED_BY`. Hard `DETACH DELETE` appears only in `clear_session` cleanup, not as a decay/forget feature.

See [`ANALYSIS.md`](./ANALYSIS.md) for the full feature diff vs the .NET port baseline and parity implications.
