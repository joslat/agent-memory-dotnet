// Migration 0008 — index Fact.predicate_key (the relation-completeness retrieval path).
//
// This is 0005's defect repeating one column over. fact_merge_key_idx is
// ON (subject_key, object_key, predicate_key, owner_key), and Neo4j uses a composite index only when
// the query filters a matching PREFIX -- so a filter on predicate_key alone, sitting at column 3, got
// nothing from it. Fresh deployments pick this up via SchemaBootstrapper; this migration brings
// existing databases to parity. Idempotent (IF NOT EXISTS); each statement runs in its own transaction.
//
// FactQueries.cs:88 (SearchByCanonicalPredicates) filters f.predicate_key IN $predicateKeys and had NO
// index entry point whatsoever. Neither companion predicate can rescue it: f.invalidated_at IS NULL
// (:89) is unindexable because a range index stores no nulls, and the owner clause under the default
// scope is (f.owner_id = $ownerId OR f.owner_id IS NULL) (:84, MemoryScope.IncludeShared defaults true)
// whose IS NULL disjunct disqualifies a fact_owner_idx seek. The plan was a full :Fact label scan
// across all owners, then ORDER BY confidence DESC over the whole result -- cost scaling with the total
// number of facts in the store.
//
// Conditional per turn, but unconditionally a full scan when it fires: RecallOptions
// .ExpandFactsByPredicate defaults false (RecallOptions.cs:69) and Neo4jMemoryContextProvider.cs:234-235
// flips it on per-turn whenever decision.RequiresRelationCompleteness -- i.e. automatically for every
// aggregation, "list all" or "how many" question.
//
// SINGLE-PROPERTY IS CORRECT; a composite would not help. (predicate_key, owner_id) cannot serve the
// default owner-or-shared shape for the same IS NULL reason, and invalidated_at is not seekable at all,
// so there is no second column worth carrying. predicate_key is drawn from a ~110-entry canonical
// lexicon, so it is far below the range-index key cap and needs no length guard.

CREATE INDEX fact_predicate_key_idx IF NOT EXISTS FOR (f:Fact) ON (f.predicate_key);
