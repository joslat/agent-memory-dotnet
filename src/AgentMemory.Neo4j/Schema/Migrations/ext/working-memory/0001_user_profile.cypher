// ext/working-memory/0001 — adopt upstream's :User identity node for the compiled profile block.
//
// The constraint is UPSTREAM'S, by name and by property. Upstream v0.5.0 defines :User with
// {id, identifier, attributes}, uniquely keyed on `identifier` via a constraint called
// `user_identifier`. The design for this feature proposed a new `user_owner_unique` constraint on
// `owner_id` instead; the snapshot check it mandated says otherwise, and adopting a label but keying
// it on a different property would make the adoption nominal -- the same spelling carrying a
// different meaning, which is exactly what the parity verifier cannot catch because it compares
// names, not semantics.
//
// So: MERGE on `identifier` (upstream's key, holding the owner id), and carry `owner_id` alongside
// for .NET's own scoping convention. Both spellings agree on every node this writes.
//
// Idempotent (IF NOT EXISTS); one statement per transaction, per the runner's contract.

CREATE CONSTRAINT user_identifier IF NOT EXISTS FOR (u:User) REQUIRE u.identifier IS UNIQUE;
