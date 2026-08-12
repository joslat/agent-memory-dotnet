// Migration 0011 — index ReasoningTrace.trace_kind, the promotion marker procedural memory turns on.
//
// A trace and a procedure are the same record read two ways: an episode says what happened once, a
// procedure says what to do next time. They differ by RETRIEVAL KEY, so the marker has to be seekable
// for the "procedures only" search to be anything other than a post-filter over the whole label.
//
// Named trace_kind and NOT kind, deliberately: `kind` already means "audit-node discriminator" both
// here and upstream, and overloading a property whose meaning is shared with another implementation is
// the changed-semantics hazard the schema-parity check exists to catch. A new PROPERTY is ungated by
// that verifier; a new label or relationship type is not, which is why promotion is a property rather
// than a :Procedure label or a PROMOTED_FROM edge -- strictly more risk, zero more function.
//
// Mirrors trace_success_idx, the sibling single-property index on this label.
//
// It also carries the retention exemption: PruneSessionTraces orders by started_at with age as its
// ONLY criterion and fires on every trace creation once MaxTracesPerSession is set, so a promoted
// procedure without this marker is deleted by recency and the capability does not exist. The prune's
// clause is coalesce(t.trace_kind, 'episode') <> 'procedure' -- NULL-safe on purpose, because a trace
// written before this property existed must still be prunable, or a retention cap silently stops
// capping.
//
// Fresh deployments pick this up via SchemaBootstrapper; this migration brings existing databases to
// parity. Idempotent (IF NOT EXISTS); each statement runs in its own transaction.

CREATE INDEX trace_kind_idx IF NOT EXISTS FOR (t:ReasoningTrace) ON (t.trace_kind);
