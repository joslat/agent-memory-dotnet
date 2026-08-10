// Migration 0006 — index MemoryReadAudit.memory_id (BUG-A2).
//
// HistoryQueries resolves an item's read audit three times over with
// OPTIONAL MATCH (audit:MemoryReadAudit {memory_id: n.id}), and nothing indexed memory_id -- only
// id (a uniqueness constraint) and kind. Every history row therefore triggered an
// all-:MemoryReadAudit label scan. Fresh deployments pick this up via SchemaBootstrapper; this
// migration brings existing databases to parity. Idempotent (IF NOT EXISTS); each statement runs in
// its own transaction.
//
// This one degrades with TIME rather than with data size, which is what makes it easy to miss: a
// recall writes roughly 25 audit rows, so the scanned label grows on every read while the number of
// memories stays flat. A store that is fast on day one is slow on day ninety with an unchanged graph.
//
// Existing stores may already hold a large :MemoryReadAudit population, so this index populates over
// real data. memory_id is an identifier, so it is far below the range-index key cap; no length guard
// is needed here, unlike the fact merge key in 0005.

CREATE INDEX memory_read_audit_memory_id_idx IF NOT EXISTS FOR (a:MemoryReadAudit) ON (a.memory_id);
