# Schema Parity Assessment — `agent-memory-dotnet` vs `neo4j-labs/agent-memory`

**Scope:** Neo4j graph schema (node labels, properties, constraints, property/vector/fulltext indexes, relationship types) plus the R1/R1b multi-user isolation delta this port introduced.
**Code-grounded:** Verified against `src/AgentMemory.Neo4j/Queries/SchemaQueries.cs`, `src/AgentMemory.Abstractions/Schema/SchemaConstants.cs`, and `src/AgentMemory.Neo4j/Schema/Migrations/0002_owner_scope.cypher`.
**Upstream confidence:** DDL/constraints/vector indexes — HIGH (read verbatim from `graph/schema.py`). Node property lists, relationship names/directionality, and "no fulltext index" — MEDIUM (upstream `graph/queries.py` was summarized by the fetch tool, not read line-by-line; the SDK source is not vendored in the repo). Rows depending on the medium-confidence material are marked **verify**.

---

## 1. Our schema delta from R1/R1b

These are additions this port made that have **no upstream counterpart**. All are additive, idempotent (`IF NOT EXISTS`), and backward-compatible.

**R1 — owner-scope (multi-user isolation), exact additions:**

- **`owner_id` property** (nullable string; NULL = shared/global) added to long-term nodes and traces: `Fact`, `Entity`, `Preference`, `ReasoningTrace`. Declared at `SchemaConstants.Properties.OwnerId` (line 222).
- **`owner_key` property** — **`Fact` only** (non-null merge key = `coalesce(owner_id, '*')`, sentinel `'*'` for shared). Used in the `Fact` MERGE key `{subject, predicate, object, owner_key}` so two users extracting the same SPO triple don't collapse. Declared at `SchemaConstants.Properties.OwnerKey` (line 226).
- **Four owner property indexes** in `SchemaQueries.cs` (lines 127–136), included in `PropertyIndexes[]` (lines 156–159):
  - `fact_owner_idx` → `(:Fact).owner_id`
  - `entity_owner_idx` → `(:Entity).owner_id`
  - `preference_owner_idx` → `(:Preference).owner_id`
  - `trace_owner_idx` → `(:ReasoningTrace).owner_id`
- **Migration `0002_owner_scope.cypher`** — creates the four indexes above on existing DBs; **non-backfilling** (pre-existing nodes keep `owner_id = NULL` = shared). Fresh DBs get them via `SchemaBootstrapper`/`PropertyIndexes`. The four statements in the file exactly match the four constants.
- **Read-side scope model (not schema, but the consumer of these indexes):** `MemoryScope` (`OwnerId` + `IncludeShared`), threaded as an **enforced** parameter through `{Fact,Entity,Preference}Queries.SearchByVector` (over-fetch-top-K then post-filter `owner_id = $ownerId OR owner_id IS NULL`).

**R1b — application/store tier (optional, above owner):**

- `MemoryStorageStrategy` enum (`SharedDatabase` default; `DatabasePerApplication` for Enterprise/AuraDB), `MemoryStoreOptions`, `DefaultMemoryStoreContext`, `StoreDatabaseNaming.Resolve` (collision-safe 63-char DB-name hashing), `Neo4jMemoryStoreProvisioner` (`CREATE DATABASE … WAIT` + bootstrap), per-call DB resolution in `Neo4jSessionFactory`. This is an isolation/routing layer, not graph DDL — it adds **no node/property/index** to the schema. Default path (`SharedDatabase` + null `ApplicationId`) is byte-for-byte the pre-R1b behavior.

---

## 2. Divergence we deliberately introduced vs upstream

Per the gathered facts, upstream long-term isolation is **relationship-based** (`:User` node + `HAS_CONVERSATION`/`HAS_TRACE`/`HAS_PREFERENCE` edges + one denormalized scalar `Conversation.user_identifier`). Upstream `Entity` and `Fact` are **global** (no owner/user property and no `:User` link), and `Preference`/`ReasoningTrace` are scoped only by edge. Enforcement upstream is **write-only** (`multi_tenant=True`), and reads default to a global/anonymous scope (issue #135).

Our port diverges by adding a **scalar `owner_id` property** directly to `Fact`, `Entity`, `Preference`, `ReasoningTrace`, plus `owner_key` on `Fact`, and by enforcing scope on the **read/vector-recall path**. Concretely, these node-shape differences exist **because of us**, not because of an upstream feature we lack:

| Node | Property we added | Upstream has it? |
|------|-------------------|------------------|
| Fact | `owner_id`, `owner_key` | No (Fact is global upstream) |
| Entity | `owner_id` | No (Entity is global upstream) |
| Preference | `owner_id` | No (scalar absent; upstream scopes Preference by `HAS_PREFERENCE` edge only) |
| ReasoningTrace | `owner_id` | No (upstream scopes trace by `HAS_TRACE` edge only) |

**This divergence is intentional and should be KEPT** — it is the port's headline value-add (the upstream gap this closes is precisely the "long-term recall not isolation-enforced" gap documented in issue #135). It is not a parity regression.

---

## 3. Parity drift the OTHER direction (upstream has it / we don't, and vice-versa)

Item | Upstream | Ours | Action
---|---|---|---
**Node: `User`** (constraint `user_identifier` UNIQUE on `User.identifier`, v0.4 multi-tenant) | Present | **Absent** | **Decision.** We replaced relationship-based scoping with scalar `owner_id`. Adopting `:User` is a design choice, not a defect. Keep our model; optionally add `:User` only if cross-impl graph interop is required.
**Node: `ConsolidationRun`** (constraint `consolidation_run_id`, idx `consolidation_run_kind_idx`, v0.5) | Present | **Absent** | **verify / decision.** v0.5 feature. Add only if we implement consolidation runs. Confidence on exact props (`id`,`kind`) is HIGH (from `schema.py`); presence of the feature in our port is out of scope.
**Node: `MemoryReadAudit`** (constraint `memory_read_audit_id`, idx `memory_read_audit_kind_idx`, v0.5 privacy/audit) | Present | **Absent** | **verify / decision.** v0.5 audit feature. Add only with a read-audit implementation.
**Relationship-scoping edges: `(:User)-[:HAS_CONVERSATION/HAS_TRACE/HAS_PREFERENCE]->`** | Present | **Absent** (we use scalar `owner_id`) | **Decision.** Intentional divergence (see §2). Keep.
**Relationship: `[:TOUCHED]`** (trace → touched entities) | Present | **Absent** | **verify.** Medium confidence (relationship list was summarized upstream). If confirmed, candidate to add for trace→entity provenance. Add `Touched = "TOUCHED"` to `SchemaConstants.RelationshipTypes`.
**Relationship: `(:Entity)-[:INSTANCE_OF]->(:Entity)`** | Present (Entity→Entity) | **Present but different semantics** — ours is `INSTANCE_OF (ToolCall → Tool)` | **verify.** Name collides; direction/endpoints differ. Upstream uses it for entity typing; we use it for tool typing. Likely both legitimately exist for different subgraphs — confirm upstream endpoints before any change. No action unless interop required.
**Domain relation vocabulary (POLE+O):** `KNOWS, ALIAS_OF, MEMBER_OF, EMPLOYED_BY, OWNS, USES, LOCATED_AT, RESIDES_AT, HEADQUARTERS_AT, PARTICIPATED_IN, OCCURRED_AT, INVOLVED, SUBSIDIARY_OF, PARTNER_WITH, RELATED_TO, MENTIONS` (config-driven `RelationTypeConfig`) | Present (as configurable relation types) | **Absent as named constants** (we have generic `RELATED_TO` with a `relation_type` property carrying the value) | **verify / decision.** We model these as data (`relation_type` on `RELATED_TO`), upstream as distinct edge types via config. Functionally equivalent for storage; differs for graph-pattern queries. Keep our approach unless typed-edge traversal parity is needed.
**Index: `conversation_archived_idx` + `Conversation.archived` property (v0.5)** | Present | **Absent** (we have no `archived` property) | **Decision.** Soft-archive feature. Add `archived` + index only if we add archive support.
**Index: `trace_error_kind_idx` + `ReasoningTrace.error_kind` / `summary` props** | Present | **Absent** (we use `outcome`/`result`/`completed_at`/`started_at`/`task`) | **verify / decision.** Upstream trace shape (`error_kind`, `summary`) differs from ours. Not a constraint/index parity gap that breaks anything; a property-naming divergence. Keep ours; reconcile only if cross-impl read of traces is required.
**Vector index naming: step embedding** | `step_embedding_idx` on `(:ReasoningStep).embedding` | `reasoning_step_embedding_idx` on `(:ReasoningStep).embedding` | **Decision.** Same target, different index **name**. Cosmetic; renaming is a breaking migration for existing DBs. Keep ours unless name-level parity is mandated.
**Fulltext indexes** `message_content`, `entity_name`, `fact_content` | **None** | **3 present** (ours) | **Keep (our extension).** Confidence "upstream has none" is HIGH-but-not-absolute (not every upstream module scanned).
**Extra node labels** `Extractor`, `Schema`, `Migration` + constraint `extractor_name`, idx `schema_name_idx`/`schema_version_idx`, constraint `migration_version` | Absent | **Present** (ours) | **Keep (our extension** — provenance + migration bookkeeping).
**Extra rel types** `HAS_FACT`, `HAS_PREFERENCE`, `IN_SESSION`, `EXTRACTED_BY` (as constant), full directional `RELATED_TO` provenance props | Mostly absent / partial | **Present** (ours) | **Keep (our extension).**
**Embedding dimensions** | Default 1536, configurable per embedder | Parameterized (`BuildVectorIndexes(int dimensions)`), `cosine` | **Parity.** No action.

**Net:** No upstream **constraint, btree index, or vector index on a shared node** is missing from our port. The only clear-cut upstream-has/we-lack items are the **v0.4/v0.5 feature nodes** (`User`, `ConsolidationRun`, `MemoryReadAudit`) and a few feature-specific props/indexes (`archived`, `error_kind`/`summary`, `[:TOUCHED]`) — all gated behind features we have not (yet) ported, and several resting on medium-confidence upstream reads.

---

## 4. Recommended updates to OUR repo for 100% parity

### Safe to apply now (pure additions, idempotent, no behavior change to existing paths)

These close *name-level / constant-level* gaps without altering stored data or existing queries. Each is `IF NOT EXISTS` or a const declaration.

1. ~~**(verify-then-safe) Add `[:TOUCHED]` relationship-type constant.**~~ ✅ **DONE (2026-06-06).**
   Upstream direction verified against `graph/queries.py` — `(:ReasoningStep)-[:TOUCHED]->(:Entity)`, `recorded_at` stamped on create, identity precedence id > name+type > name. We ported the **by-id** variant (links existing entities only; never MERGE-creates, preserving our resolution/dedup pipeline). `SchemaConstants.RelationshipTypes.Touched = "TOUCHED"` added and wired into `ReasoningQueries.RecordTouchedEntitiesByIds`/`GetTouchedEntityIds` + `IReasoningMemoryService`/`IReasoningStepRepository`. No constraint/index needed (parity-confirmed: upstream `schema.py` has none). 4 unit + 5 integration tests.

> No other "safe now" schema additions exist: every remaining upstream-only item (`User`, `ConsolidationRun`, `MemoryReadAudit`, `archived`, `error_kind`/`summary`) belongs to an unported feature and would create orphan schema with no writer, which is *not* desirable to land blindly.

### Needs a human decision

2. **Keep `owner_id` / `owner_key` and the four `*_owner_idx` indexes (do NOT remove for parity).** This is an intentional, enforced-on-read improvement over upstream's write-only/edge-based model. Decision = affirm divergence. No file change.
3. **`:User` node + `HAS_CONVERSATION`/`HAS_TRACE`/`HAS_PREFERENCE` edges** (upstream v0.4). Decision: adopt only if cross-implementation graph interop with the Python store is a goal. If yes: add `User` to `NodeLabels`, a `user_identifier` UNIQUE constraint to `SchemaQueries.Constraints`, and the three edges to `RelationshipTypes`. If no (recommended given our scalar model): document the divergence and skip.
4. **`ConsolidationRun` + `MemoryReadAudit` nodes/indexes** (upstream v0.5). Decision: tie to porting consolidation and read-audit features. Schema lands *with* the feature, not before. Files when greenlit: `SchemaQueries.cs` (constraints + `consolidation_run_kind_idx`, `memory_read_audit_kind_idx`), `SchemaConstants.cs` (labels), a new `0003_*.cypher` migration.
5. **`Conversation.archived` + `conversation_archived_idx`** (upstream v0.5). Decision: tie to a soft-archive feature. Files: add `Archived` to `Properties`, an index to `SchemaQueries.PropertyIndexes`, migration `0003`.
6. **`ReasoningTrace.error_kind` / `summary` + `trace_error_kind_idx`** (upstream). Decision: reconcile trace shape only if cross-impl trace read is required; otherwise keep our `outcome`/`result` shape. Files (if pursued): `Properties` + `SchemaQueries.PropertyIndexes` + migration.
7. **POLE+O typed relations vs our `relation_type`-on-`RELATED_TO` model.** Decision: keep data-driven model (recommended) or emit typed edges to match upstream traversal patterns. No change recommended; document as an intentional modeling difference.
8. **Index-name reconciliation** (`step_embedding_idx` vs `reasoning_step_embedding_idx`). Decision: renaming is a breaking migration (drop+recreate) for deployed DBs; only do it if exact index-name parity is a hard requirement. Recommended: keep ours.

### Two internal nits found while verifying (not parity, but worth flagging)

- In `SchemaQueries.cs`, two of our index names lack the `_idx` suffix used everywhere else and used by the facts summary: `fact_category` (line 96) and `reasoning_step_timestamp` (line 108). These are internally inconsistent with `fact_category_idx`/`reasoning_step_timestamp_idx` as referenced in our own docs/parity material. Renaming is a **decision** (breaking for existing DBs), not safe-now.

---

## 5. structuredRecommendations

```json
[
  {
    "file": "src/AgentMemory.Abstractions/Schema/SchemaConstants.cs",
    "change": "Add `public const string Touched = \"TOUCHED\";` to RelationshipTypes for upstream parity (trace -> touched entities). Constant-only; wire into queries only after the trace->entity edge is implemented.",
    "risk": "decision",
    "rationale": "Upstream [:TOUCHED] edge is from a MEDIUM-confidence summarized read of graph/queries.py; verify endpoints/direction before use. Adding the constant alone is harmless; emitting the edge requires a writer."
  },
  {
    "file": "src/AgentMemory.Neo4j/Queries/SchemaQueries.cs and src/AgentMemory.Neo4j/Schema/Migrations/0002_owner_scope.cypher",
    "change": "KEEP owner_id/owner_key properties and fact_owner_idx/entity_owner_idx/preference_owner_idx/trace_owner_idx. Do not remove for 'parity' with upstream's edge-based model.",
    "risk": "decision",
    "rationale": "Intentional divergence: this port enforces owner scope on the read/vector-recall path, closing the upstream write-only/anonymous-global-recall gap (issue #135). Removing it would regress the port's core value-add."
  },
  {
    "file": "src/AgentMemory.Abstractions/Schema/SchemaConstants.cs + src/AgentMemory.Neo4j/Queries/SchemaQueries.cs",
    "change": "Optionally add :User node label, a user_identifier UNIQUE constraint, and HAS_CONVERSATION/HAS_TRACE/HAS_PREFERENCE relationship constants to match upstream v0.4 multi-tenant model.",
    "risk": "decision",
    "rationale": "Adopt ONLY if cross-implementation graph interop with the Python store is a goal. Our scalar owner_id model already provides isolation; adding :User duplicates the concept. Recommended: document the divergence and skip."
  },
  {
    "file": "src/AgentMemory.Neo4j/Queries/SchemaQueries.cs + src/AgentMemory.Abstractions/Schema/SchemaConstants.cs + new src/AgentMemory.Neo4j/Schema/Migrations/0003_*.cypher",
    "change": "Add ConsolidationRun node (constraint consolidation_run_id, index consolidation_run_kind_idx) and MemoryReadAudit node (constraint memory_read_audit_id, index memory_read_audit_kind_idx), upstream v0.5.",
    "risk": "decision",
    "rationale": "Feature-gated. Land schema WITH the consolidation/read-audit feature implementation, not before, to avoid orphan schema with no writer. Upstream DDL is HIGH confidence (read from schema.py)."
  },
  {
    "file": "src/AgentMemory.Abstractions/Schema/SchemaConstants.cs + src/AgentMemory.Neo4j/Queries/SchemaQueries.cs + new migration",
    "change": "Add Conversation.archived property + conversation_archived_idx (upstream v0.5 soft-archive).",
    "risk": "decision",
    "rationale": "Tie to a soft-archive feature. No value as bare schema without archive read/write paths."
  },
  {
    "file": "src/AgentMemory.Abstractions/Schema/SchemaConstants.cs + src/AgentMemory.Neo4j/Queries/SchemaQueries.cs",
    "change": "Reconcile ReasoningTrace shape: upstream uses error_kind/summary + trace_error_kind_idx; we use outcome/result/started_at/completed_at/task. Decide whether to add error_kind/summary.",
    "risk": "decision",
    "rationale": "MEDIUM confidence on upstream trace props (reasoning.py not read). Only reconcile if cross-impl trace reads are required; otherwise keep our richer shape."
  },
  {
    "file": "src/AgentMemory.Neo4j/Queries/SchemaQueries.cs",
    "change": "Optional: rename index constants fact_category -> fact_category_idx (line 96) and reasoning_step_timestamp -> reasoning_step_timestamp_idx (line 108) for internal naming consistency.",
    "risk": "decision",
    "rationale": "Not a parity item but an internal inconsistency vs every other *_idx name and our own docs. Renaming is a drop+recreate migration on deployed DBs, so it needs a human call."
  },
  {
    "file": "src/AgentMemory.Neo4j/Queries/SchemaQueries.cs",
    "change": "Optional: rename reasoning_step_embedding_idx -> step_embedding_idx to match upstream vector index naming.",
    "risk": "decision",
    "rationale": "Same target/dims/cosine; only the index NAME differs. Renaming is breaking for existing DBs. Recommended: keep ours."
  },
  {
    "file": "(no change) src/AgentMemory.Neo4j/Queries/*.cs",
    "change": "KEEP .NET extensions absent upstream: fulltext indexes (message_content, entity_name, fact_content); nodes Extractor/Schema/Migration; rel types HAS_FACT/HAS_PREFERENCE/IN_SESSION/EXTRACTED_BY; full directional RELATED_TO provenance.",
    "risk": "decision",
    "rationale": "Net-positive extensions beyond upstream. No parity action; affirm and document. 'Upstream has no fulltext index' is HIGH-but-not-absolute confidence (not every upstream module scanned)."
  }
]
```

**Confidence caveats applied:** every row depending on upstream `graph/queries.py`, `reasoning.py`, or the relationship-name list (`[:TOUCHED]`, `[:INSTANCE_OF]` endpoints, POLE+O edge semantics, exact `ReasoningTrace`/`ReasoningStep` props) is marked **verify** because those upstream files were tool-summarized, not read verbatim, and the actual SDK Cypher is not vendored in the upstream repo. Upstream DDL (constraints, btree/point/vector indexes, 1536/cosine) is HIGH confidence. No upstream feature was invented beyond the supplied facts.

Relevant files: `src/AgentMemory.Neo4j/Queries/SchemaQueries.cs`, `src/AgentMemory.Abstractions/Schema/SchemaConstants.cs`, `src/AgentMemory.Neo4j/Schema/Migrations/0002_owner_scope.cypher`.
