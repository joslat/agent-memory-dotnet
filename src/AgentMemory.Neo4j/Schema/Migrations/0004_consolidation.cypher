// Migration 0004 — memory-hygiene / consolidation schema (PR #113).
//
// Adds the audit-node id constraint and the conversation-archived index used by the consolidation
// service (archive-expired-conversations + the :ConsolidationRun audit). Fresh deployments pick these
// up via SchemaBootstrapper; this migration brings existing databases to parity. Idempotent
// (IF NOT EXISTS); each statement runs in its own transaction.

CREATE CONSTRAINT consolidation_run_id IF NOT EXISTS FOR (r:ConsolidationRun) REQUIRE r.id IS UNIQUE;
CREATE INDEX conversation_archived_idx IF NOT EXISTS FOR (c:Conversation) ON (c.archived);
