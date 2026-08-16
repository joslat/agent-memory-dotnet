// ext/arithmetic/0001 — recompute-in-place identity for derived facts.
//
// This was numbered 0012 in the design. So was delta-recall's migration: two designs each correctly
// reasoned "next free after 0011", and a database enabling one and later the other would have had two
// scripts fighting over one key in the unique-constrained (:Migration {version}) bookkeeping — one of
// them silently skipped as "already applied", leaving an index missing that nobody could see was
// missing. The ext/<id>/ namespace dissolves the collision structurally rather than by asking two
// authors to coordinate.
//
// WHY derivation_key AND NOT the fact merge key: an aggregate's value changes on every recompute, so
// identity on {subject_key, predicate_key, object_key, owner_key} would spawn a fresh node per
// observation and leave one dead aggregate behind each time. Identity is
// SHA-256(subject_key|predicate_key|operator|owner_key) — the object is deliberately absent.
//
// The key is computed in C#, never in Cypher: MemoryTripleCanonicalizer lowercases AND collapses
// whitespace runs while Cypher's toLower does neither, and the two disagree outright on U+0130. A key
// computed in two places is a key that will eventually be computed two ways.
//
// A RANGE index rather than a uniqueness constraint, deliberately. A constraint would be stronger, but
// it would also turn a concurrent double-write into a thrown exception mid-ingestion; the MERGE already
// converges on one node, and the accountant is best-effort by design — it must never fail an ingestion.
//
// Idempotent (IF NOT EXISTS); one statement per transaction, per the runner's contract.

CREATE INDEX fact_derivation_key_idx IF NOT EXISTS FOR (f:Fact) ON (f.derivation_key);
CREATE INDEX fact_kind_idx IF NOT EXISTS FOR (f:Fact) ON (f.fact_kind);
