// Migration 0002 — owner-scope property indexes (R1, multi-user isolation).
//
// Adds owner_id property indexes that accelerate the owner filter applied during scoped
// vector recall. Fresh deployments already pick these up via SchemaBootstrapper; this
// migration brings existing databases to parity.
//
// NON-BACKFILLING BY DESIGN: pre-existing Fact/Entity/Preference nodes keep owner_id = NULL,
// which the scope model treats as shared/global knowledge (MemoryScope.IncludeShared). No
// node is rewritten, so the upgrade is lossless and backward-compatible — prior memories
// remain visible to every owner until they are re-extracted under a concrete owner_id.
//
// Each statement is idempotent (IF NOT EXISTS) and applied in its own transaction by
// MigrationRunner (schema operations cannot share a transaction with the migration record).

CREATE INDEX fact_owner_idx IF NOT EXISTS FOR (f:Fact) ON (f.owner_id);
CREATE INDEX entity_owner_idx IF NOT EXISTS FOR (e:Entity) ON (e.owner_id);
CREATE INDEX preference_owner_idx IF NOT EXISTS FOR (p:Preference) ON (p.owner_id);
CREATE INDEX trace_owner_idx IF NOT EXISTS FOR (t:ReasoningTrace) ON (t.owner_id);
