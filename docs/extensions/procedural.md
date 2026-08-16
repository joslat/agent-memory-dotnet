# `procedural` — promoted reasoning traces

**Id:** `procedural` · **Version:** 1 · **Status:** shipped (retro-wrapped)

A trace and a procedure are the same record read two ways: an episode says what happened once, a
procedure says what to do next time. They differ by **retrieval key**, and this extension owns that
key.

> **This is a retro-wrap.** The schema already shipped, in base migration `0011_trace_kind`, so
> activating this extension changes nothing about any database. It exists to prove the extension
> abstraction against a feature that is already live, to give `trace_kind` and `trace_kind_idx` an
> **owner** in the `schema-check` report, and to carry the TCK profile slot.

## Shape

| Kind | Name | Notes |
|---|---|---|
| Property | `ReasoningTrace.trace_kind` | `'episode'` (implicit, when absent) or `'procedure'` |
| Labels | *none* | |
| Relationship types | *none* | |
| Migration scripts | *none* | `0011_trace_kind` is **base-resident** — see below |
| Base-resident migrations | `0011_trace_kind` | Ownership only; the script stays in the base sequence |
| Depends on | *nothing* | |
| TCK profile | *none declared yet* | The machinery ships ahead of the case convention it defines |

**Why no `ext/procedural/0001`.** `0011` has already been applied to every database that ran
migrations. Re-declaring it under an extension key would replay an index creation under a second
version key — harmless in effect (`IF NOT EXISTS`) but a lie in the bookkeeping, and it would make one
physical index appear twice in migration history. Ownership is recorded; the script does not move.

## Cypher

The index, from base migration `0011_trace_kind`:

```cypher
CREATE INDEX trace_kind_idx IF NOT EXISTS FOR (t:ReasoningTrace) ON (t.trace_kind);
```

The marker must be **seekable**, not merely present: without an index, a "procedures only" search is a
post-filter over the whole label.

The retention exemption that makes the capability exist at all:

```cypher
coalesce(t.trace_kind, 'episode') <> 'procedure'
```

`PruneSessionTraces` orders by `started_at` with age as its **only** criterion and fires on every trace
creation once `MaxTracesPerSession` is set. A promoted procedure without this marker is deleted by
recency. The `coalesce` is NULL-safe deliberately — a trace written before the property existed must
still be prunable, or a retention cap silently stops capping.

## Semantics

**R2 — write-path isolation.** `trace_kind` is written **only** by the promotion service. No
trace-repository create path sets it, so a TCK case creating a reasoning trace can never produce a
procedure.

**R3 — base-read neutrality.** Absent means `'episode'` by `coalesce`, so a promoted trace is an
ordinary trace on every read path except the prune's explicit exemption. Nothing else in the base
schema discriminates on it.

## Parity delta

| Axis | Entries |
|---|---|
| `AddNetSupersetProperties` | `trace_kind` |
| `AddNetOnlyLabels` | *(none)* |
| `AddNetOnlyRelationshipTypes` | *(none)* |
| `RemoveUpstreamOnlyLabels` | *(none)* |
| `ReserveUpstreamPropertyNames` | *(none)* |

The delta is **documentary**: a property is ungated by the parity verifier, which is precisely why the
0011 header chose a property over a `:Procedure` label or a `PROMOTED_FROM` edge — a label or a
relationship type is parity-gated, so either would have been strictly more risk for zero more function.

**Named `trace_kind` and deliberately not `kind`.** `kind` already means "audit-node discriminator"
both here and upstream. Overloading a property whose meaning is shared with another implementation is
the changed-semantics hazard the parity check *cannot* catch — it compares spellings, not meanings.

## Conformance

No extension TCK profile has shipped yet. `procedural` runs under the base **178-case** gate, which it
passes with the extension both off and on — as it must, since activating a retro-wrap applies no
schema.

The profile this extension will declare, when the case convention lands: *a promoted trace survives a
`MaxTracesPerSession` prune that deletes its episode siblings.*
