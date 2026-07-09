# ADR 0010 - Non-Destructive Temporal Memory

Status: Accepted

Date: 2026-07-09

## Context

Agent memory changes over time. Facts become outdated, preferences are superseded, contradictions are detected, and old records may become less relevant. Hard deletion loses auditability and makes as-of recall impossible.

The project also aligns with upstream graph-memory patterns that prefer supersession and invalidation over destructive forgetting.

## Decision

Prefer non-destructive temporal memory.

The system should:

- use `invalidated_at` for transaction-time belief state,
- use `valid_from` and `valid_until` where valid-time semantics apply,
- create `SUPERSEDED_BY` relationships when one fact/preference replaces another,
- keep superseded/invalidated records for audit and as-of recall,
- make destructive delete explicit and scoped.

Memory decay should influence ranking or soft invalidation by default rather than erasing evidence.

## Consequences

Positive consequences:

- Memory remains auditable.
- As-of recall can reconstruct prior belief state.
- Contradictions and preference changes preserve history.
- Recovery and debugging are easier.

Tradeoffs:

- The database retains more records.
- Queries must filter live versus invalidated records correctly.
- Maintenance tooling is needed for hygiene and optional pruning.

## Alternatives Considered

### Hard-delete stale memory by default

Rejected. It destroys provenance and undermines temporal recall.

### Overwrite records in place

Rejected. It hides what changed and when.

### Keep only current memory and archive externally

Rejected. The graph itself needs temporal relationships and as-of query support.

## Verification Anchors

- `src/AgentMemory.Abstractions/Schema/SchemaConstants.cs` defines `invalidated_at`, `valid_from`, `valid_until`, and `SUPERSEDED_BY`.
- `src/AgentMemory.Neo4j/Queries/FactQueries.cs` implements invalidate and supersede paths.
- `docs/schema.md` documents temporal semantics.
- `docs/ROADMAP.md` lists bitemporal/decay capability as shipped.
