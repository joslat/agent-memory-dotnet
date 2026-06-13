// =============================================================================
// neo4j-agent-memory — Neo4j schema (constraints + indexes)
// Source: neo4j-labs/agent-memory @ f29ae8dd8 (2026-06-03), Python package v0.5.0
// Extracted verbatim from src/neo4j_agent_memory/graph/schema.py (SchemaManager)
//   + property names from src/neo4j_agent_memory/graph/queries.py CREATE_* statements.
// Default vector dimensions: 1536 (cosine).
// NOTE: This is the bolt / direct-Neo4j storage schema. The NAMS hosted backend
//       does not expose Cypher schema management (client.graph raises NotSupportedError).
// =============================================================================

// ── Uniqueness constraints ──────────────────────────────────────────────────
CREATE CONSTRAINT conversation_id      IF NOT EXISTS FOR (n:Conversation)    REQUIRE n.id         IS UNIQUE;
CREATE CONSTRAINT message_id           IF NOT EXISTS FOR (n:Message)         REQUIRE n.id         IS UNIQUE;
CREATE CONSTRAINT entity_id            IF NOT EXISTS FOR (n:Entity)          REQUIRE n.id         IS UNIQUE;
CREATE CONSTRAINT preference_id        IF NOT EXISTS FOR (n:Preference)      REQUIRE n.id         IS UNIQUE;
CREATE CONSTRAINT fact_id              IF NOT EXISTS FOR (n:Fact)            REQUIRE n.id         IS UNIQUE;
CREATE CONSTRAINT reasoning_trace_id   IF NOT EXISTS FOR (n:ReasoningTrace)  REQUIRE n.id         IS UNIQUE;
CREATE CONSTRAINT reasoning_step_id    IF NOT EXISTS FOR (n:ReasoningStep)   REQUIRE n.id         IS UNIQUE;
CREATE CONSTRAINT tool_name            IF NOT EXISTS FOR (n:Tool)            REQUIRE n.name       IS UNIQUE;
CREATE CONSTRAINT tool_call_id         IF NOT EXISTS FOR (n:ToolCall)        REQUIRE n.id         IS UNIQUE;
CREATE CONSTRAINT user_identifier      IF NOT EXISTS FOR (n:User)            REQUIRE n.identifier IS UNIQUE;   // v0.4 multi-tenant
CREATE CONSTRAINT consolidation_run_id IF NOT EXISTS FOR (n:ConsolidationRun) REQUIRE n.id        IS UNIQUE;   // v0.5 hygiene
CREATE CONSTRAINT memory_read_audit_id IF NOT EXISTS FOR (n:MemoryReadAudit) REQUIRE n.id         IS UNIQUE;   // v0.5 privacy/audit

// ── Range (btree) indexes ───────────────────────────────────────────────────
CREATE INDEX conversation_session_idx     IF NOT EXISTS FOR (n:Conversation)   ON (n.session_id);
CREATE INDEX message_timestamp_idx        IF NOT EXISTS FOR (n:Message)        ON (n.timestamp);
CREATE INDEX message_role_idx             IF NOT EXISTS FOR (n:Message)        ON (n.role);
CREATE INDEX entity_type_idx              IF NOT EXISTS FOR (n:Entity)         ON (n.type);
CREATE INDEX entity_name_idx              IF NOT EXISTS FOR (n:Entity)         ON (n.name);
CREATE INDEX entity_canonical_idx         IF NOT EXISTS FOR (n:Entity)         ON (n.canonical_name);
CREATE INDEX preference_category_idx      IF NOT EXISTS FOR (n:Preference)     ON (n.category);
CREATE INDEX trace_session_idx            IF NOT EXISTS FOR (n:ReasoningTrace) ON (n.session_id);
CREATE INDEX trace_success_idx            IF NOT EXISTS FOR (n:ReasoningTrace) ON (n.success);
CREATE INDEX trace_error_kind_idx         IF NOT EXISTS FOR (n:ReasoningTrace) ON (n.error_kind);   // v0.3+ TraceOutcome
CREATE INDEX tool_call_status_idx         IF NOT EXISTS FOR (n:ToolCall)       ON (n.status);
CREATE INDEX conversation_archived_idx    IF NOT EXISTS FOR (n:Conversation)   ON (n.archived);     // v0.5 hygiene
CREATE INDEX consolidation_run_kind_idx   IF NOT EXISTS FOR (n:ConsolidationRun) ON (n.kind);       // v0.5
CREATE INDEX memory_read_audit_kind_idx   IF NOT EXISTS FOR (n:MemoryReadAudit) ON (n.kind);        // v0.5

// ── Vector indexes (dimensions = 1536, similarity = cosine) ──────────────────
CREATE VECTOR INDEX message_embedding_idx    IF NOT EXISTS FOR (n:Message)        ON (n.embedding)      OPTIONS {indexConfig: {`vector.dimensions`: 1536, `vector.similarity_function`: 'cosine'}};
CREATE VECTOR INDEX entity_embedding_idx     IF NOT EXISTS FOR (n:Entity)         ON (n.embedding)      OPTIONS {indexConfig: {`vector.dimensions`: 1536, `vector.similarity_function`: 'cosine'}};
CREATE VECTOR INDEX preference_embedding_idx IF NOT EXISTS FOR (n:Preference)     ON (n.embedding)      OPTIONS {indexConfig: {`vector.dimensions`: 1536, `vector.similarity_function`: 'cosine'}};
CREATE VECTOR INDEX fact_embedding_idx       IF NOT EXISTS FOR (n:Fact)           ON (n.embedding)      OPTIONS {indexConfig: {`vector.dimensions`: 1536, `vector.similarity_function`: 'cosine'}};
CREATE VECTOR INDEX task_embedding_idx       IF NOT EXISTS FOR (n:ReasoningTrace) ON (n.task_embedding) OPTIONS {indexConfig: {`vector.dimensions`: 1536, `vector.similarity_function`: 'cosine'}};
CREATE VECTOR INDEX step_embedding_idx       IF NOT EXISTS FOR (n:ReasoningStep)  ON (n.embedding)      OPTIONS {indexConfig: {`vector.dimensions`: 1536, `vector.similarity_function`: 'cosine'}};

// ── Point index (geospatial) ────────────────────────────────────────────────
CREATE POINT INDEX entity_location_idx IF NOT EXISTS FOR (n:Entity) ON (n.location);

// ── Schema-persistence node indexes (:Schema versioned ontology storage) ─────
//   created via SchemaManager.ensure_schema_index() (schema/persistence.py)
CREATE INDEX schema_name_idx IF NOT EXISTS FOR (n:Schema) ON (n.name);
CREATE INDEX schema_id_idx   IF NOT EXISTS FOR (n:Schema) ON (n.id);

// NOTE: There is NO transaction-time axis in this schema — no invalidated_at / expired_at
//       constraint or index exists. Point-in-time (`as_of`) recall (v0.5) is valid-time only
//       (valid_from / valid_until) and currently wired for Preferences only.
// NOTE: :Extractor (provenance) nodes are keyed by name via MERGE only — no managed constraint.
//       No FULLTEXT indexes are defined by the library.
// Totals: 12 uniqueness constraints, 14 managed range indexes (+2 :Schema range indexes from
//       queries.py), 6 vector indexes (1536-dim cosine), 1 point index.
