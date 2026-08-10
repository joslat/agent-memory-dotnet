// Migration 0010 — index Fact.owner_key, the only seekable predicate dedup-on-create has.
//
// FactQueries.FindDuplicate runs on EVERY FACT WRITE and had no index entry point at all. owner_key
// is column 4 of fact_merge_key_idx and a composite cannot be seeked from a non-prefix column;
// toLower(subject) and toLower(predicate) cannot be indexed because Neo4j 5 has no functional
// indexes; invalidated_at IS NULL and embedding IS NOT NULL are both unindexable.
//
// Profiled on 5.26 with 20,000 facts across 200 owners: dedup-on-create planned a full
// NodeByLabelScan of all 20,000 rows, per fact written. With this index the same query plans a
// NodeIndexSeek at 100 rows.
//
// Adding subject_key to the query instead was tried first and does NOT work: filtering the
// composite's leading column alone still plans a full scan.
//
// HONEST LIMIT: in a single-tenant store owner_key is "*" for every shared fact, so the seek returns
// the whole label and this buys nothing beyond its write cost. It pays for itself in the multi-tenant
// deployment the owner-isolation work exists to serve.
//
// Fresh deployments pick this up via SchemaBootstrapper; this migration brings existing databases to
// parity. Idempotent (IF NOT EXISTS); each statement runs in its own transaction.

CREATE INDEX fact_owner_key_idx IF NOT EXISTS FOR (f:Fact) ON (f.owner_key);
