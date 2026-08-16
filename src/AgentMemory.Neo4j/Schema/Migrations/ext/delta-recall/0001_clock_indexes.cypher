// ext/delta-recall/0001 — range indexes over the clocks the delta queries seek on.
//
// These were numbered 0012 in the design. So was arithmetic-memory's migration: two designs each
// correctly reasoned "next free after 0011", and a database enabling one and later the other would
// have had two scripts fighting over one key in the unique-constrained (:Migration {version})
// bookkeeping. The ext/<id>/ namespace dissolves the collision structurally rather than by asking
// two authors to coordinate.
//
// WHY THESE ARE SEEKABLE, given that `invalidated_at IS NULL` is famously unindexable here: Neo4j
// range indexes store no nulls, which is exactly why a NULL check cannot use one. The delta
// predicates are the opposite shape -- range predicates over NON-null values (`invalidated_at >
// $since`) -- which a range index serves directly. The owner clause's `owner_id IS NULL` disjunct
// does not disqualify the plan, because the time range supplies the seek.
//
// Behaviour-neutral by construction: an index changes plans, never results. That is why this
// extension is Gold-safe with the flag ON.
//
// Idempotent (IF NOT EXISTS); one statement per transaction, per the runner's contract.

CREATE INDEX fact_created_at_idx IF NOT EXISTS FOR (f:Fact) ON (f.created_at);
CREATE INDEX fact_invalidated_at_idx IF NOT EXISTS FOR (f:Fact) ON (f.invalidated_at);
CREATE INDEX fact_valid_from_idx IF NOT EXISTS FOR (f:Fact) ON (f.valid_from);
CREATE INDEX fact_valid_until_idx IF NOT EXISTS FOR (f:Fact) ON (f.valid_until);
CREATE INDEX preference_created_at_idx IF NOT EXISTS FOR (p:Preference) ON (p.created_at);
CREATE INDEX preference_invalidated_at_idx IF NOT EXISTS FOR (p:Preference) ON (p.invalidated_at);
CREATE INDEX entity_created_at_idx IF NOT EXISTS FOR (e:Entity) ON (e.created_at);
