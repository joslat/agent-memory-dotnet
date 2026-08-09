// Migration 0005 — composite index backing the fact merge key (L11).
//
// Every fact write MERGEs on {subject_key, predicate_key, object_key, owner_key}
// (FactQueries.UpsertBatch, FusedPersistenceQueries.FactUpsertBatch) and nothing indexed that
// combination, so each MERGE was an all-:Fact label scan whose cost grew with the size of the store
// rather than the size of the write. Fresh deployments pick this up via SchemaBootstrapper; this
// migration brings existing databases to parity. Idempotent (IF NOT EXISTS); each statement runs in
// its own transaction.
//
// Property order is deliberate and is NOT the MERGE order: owner_key is "*" for every shared fact
// and predicate_key is drawn from a ~110-entry lexicon, so leading with either would give a nearly
// non-discriminating prefix.
//
// ON AN EXISTING DATABASE THIS INDEX POPULATES OVER DATA ALREADY WRITTEN. Neo4j bounds a range-index
// key at roughly 8 KB and nothing has ever bounded fact subject/object length -- ExtractionOptions
// carries only MinNameLength. A store holding one pathological legacy fact will therefore land this
// index in the FAILED state. That is not fatal: fact_merge_key_idx is registered as
// optimization-only (SchemaConformance.IsOptimizationOnly), so bootstrap warns and continues, and
// fact MERGEs simply fall back to the label scan they used before this migration existed. Drop and
// recreate the index after shortening the offending value -- a FAILED index is never rebuilt by
// CREATE ... IF NOT EXISTS.

CREATE INDEX fact_merge_key_idx IF NOT EXISTS FOR (f:Fact) ON (f.subject_key, f.object_key, f.predicate_key, f.owner_key);
