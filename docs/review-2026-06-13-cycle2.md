# Throat-check review — 2026-06-13 (cycle 2)

**Scope:** focused adversarial review of the code shipped *since* cycle-1 — the new `AgentMemory.Analytics`
GDS package and the public invalidate/supersede surface (service + CLI + MCP). **Method:** 3 dimension
scanners (GDS correctness/resource-safety, GDS isolation, invalidate/supersede robustness) → per-finding
adversarial verification (skeptic defaults to *reject*). Unlike cycle-1, verification completed without
rate-limiting. **4 findings confirmed, all fixed in this cycle.**

## Findings (ranked)

| # | Severity | Area | Title | Effort | Status |
|---|---|---|---|---|---|
| 1 | 🟧 Medium | GDS correctness | Projection lifecycle spans 3 sessions → breaks under cluster routing | M | ✅ fixed |
| 2 | 🟧 Medium | GDS correctness | Non-idempotent projection under managed-tx retry → leak + hard fail | M | ✅ fixed |
| 3 | 🟧 Medium | API robustness | Supersede has no `loser==winner` guard (self-supersede footgun) | S | ✅ fixed |
| 4 | 🟡 Low | GDS isolation | Projection filters edges by endpoint owner, not the edge's `owner_id` | S | ✅ fixed |

---

### 1 — GDS projection lifecycle spans three independent sessions → breaks under cluster routing
**Medium · `src/AgentMemory.Analytics/GdsGraphScope.cs`**

`WithProjectionAsync` ran the projection (Write session), the algorithm stream (**Read** session), and the
drop (Write session) as three separate transaction-runner calls — each opening its own session. GDS in-memory
named graphs are **member-local** (not replicated across cluster members). Under a routing URI (`neo4j://`,
as used by a clustered/Aura deployment), the Read stream can route to a follower while the projection ran on
the leader → `gds.pageRank.stream` fails with "graph does not exist." (Default `bolt://` is unaffected, which
is why the single-instance integration tests pass — hence Medium, not High.)

**Fix:** run the algorithm stream under a **Write** session too (`tx.WriteAsync`), pinning all three
operations to the leader that holds the just-created catalog graph. Short-lived admin ops, so leader-pinning
is acceptable. (The verifier's preferred single-pinned-session option needs a new runner abstraction; this is
the minimal, equally-correct form.)

### 2 — Non-idempotent projection under managed-transaction retry
**Medium · `src/AgentMemory.Analytics/GdsGraphScope.cs`**

The projection runs inside `tx.WriteAsync` → `ExecuteWriteAsync`, which the driver **auto-retries** on
transient errors. The graph name is generated once per call (captured by the closure), so a retry reuses the
same name; if the first attempt created the catalog graph before the transient error, the retry hits
"graph already exists" — a hard failure plus a leaked graph (the `finally` drop only runs after the projection
returns). Narrow window, but real.

**Fix:** make the projection lambda idempotent — run a defensive `gds.graph.drop($graphName, false)` (no-op
when absent) **before** the project, inside the same retried lambda, so a retry cleans the prior partial graph.

### 3 — Supersede has no `loser == winner` guard (self-supersede footgun)
**Medium · `src/AgentMemory.Neo4j/Queries/FactQueries.cs`, `PreferenceQueries.cs`**

No layer (CLI, MCP, service, repo, Cypher) rejects `loserId == winnerId`. When they're equal, both `MATCH`es
bind the **same** node; the same-owner guard trivially passes; the node is stamped `invalidated_at`
(+ `valid_until` for facts) — dropping a perfectly good memory from live recall — and a `:SUPERSEDED_BY`
**self-loop** is created, while `count(loser) > 0` reports `superseded:true`. One fat-fingered/duplicated id
self-invalidates a memory and reports success. (Reversible — the node is kept — hence Medium not High.)

**Fix:** add `AND loser <> winner` to both supersede queries so the self case matches zero rows and correctly
returns `false` (callers then report "nothing superseded" / exit 1). Covering it in Cypher catches *all*
callers, not just the two UI entry points.

### 4 — GDS projection filters edges by endpoint owner, not the edge's own `owner_id`
**Low · `src/AgentMemory.Analytics/GdsQueries.cs`**

The scoped projection's relationship query filters only the two endpoint entities and never the edge's own
`owner_id`. But `RELATED_TO` edges carry `owner_id`, and every *other* RELATED_TO read in the library scopes
by that property (`RelationshipQueries.OwnerAnd`). So a `bob`-owned edge between two **shared** nodes enters
alice's scoped PageRank/Louvain, perturbing her scores/communities. **Node** isolation is airtight (no foreign
node id/content is ever returned); this is a structural inference side-channel over already-visible nodes —
the same class/severity as cycle-1 finding #1.

**Fix:** bind the relationship variable and add `(r.owner_id = $ownerId OR r.owner_id IS NULL)` (or strict
`r.owner_id = $ownerId` when `includeShared=false`), gated behind `hasOwnerFilter` so the unscoped projection
is unchanged.

---

## Disposition
All four fixed in the accompanying commit, with tests. The GDS fixes were re-validated against a real
GDS-enabled Neo4j; the supersede guard has a self-supersede integration test.
