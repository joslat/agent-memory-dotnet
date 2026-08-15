# `working-memory` — the compiled per-owner profile block

**Id:** `working-memory` · **Version:** 1 · **Status:** shipped, off by default

Everything else the system retrieves is probabilistic: query embedding → global vector top-K → owner
post-filter → similarity threshold. This tier is a **point-read by owner**, so it cannot be starved.

That matters because starvation is measured, not theoretical: an owner's own facts inside the global
top-60 averaged **7, minimum 1**, and one real question retrieved **zero** facts from a graph holding
504 of its own — all live, all above the similarity floor.

> **This is the first extension whose parity delta *removes* an upstream-only label.** `:User` leaves
> `UpstreamOnlyLabels`; `NetOnlyLabels` stays empty. Adoption **narrows** divergence.

## Shape

| Kind | Name | Notes |
|---|---|---|
| Label | `User` | **Adopted from upstream**, not invented |
| Property | `User.identifier` | Upstream's own unique key; holds the owner id |
| Property | `User.working_memory` | The rendered block |
| Property | `User.working_memory_built_at` | Moves only when the **content** moves |
| Property | `User.working_memory_hash` | SHA-256 of the block; powers the rebuild short-circuit |
| Migration | `ext/working-memory/0001_user_profile.cypher` | |
| Depends on | *nothing* | |
| TCK profile | *none declared yet* | |

### Keyed by `identifier` — a correction to the design

The design proposed a new constraint `user_owner_unique` on `owner_id`, and instructed the implementer
to check the upstream snapshot before writing any identity property. **The check changed the design.**
Upstream v0.5.0's `:User` carries `id, identifier, attributes` and is uniquely keyed on `identifier`
by a constraint named `user_identifier`.

Adopting a label while keying it on a different property would make the adoption *nominal*: the same
spelling carrying a different meaning — exactly the hazard the parity verifier **cannot** catch,
because it compares names and not semantics. So this reuses upstream's key and upstream's constraint
name, and writes `owner_id` alongside so .NET's own scoping convention still reads naturally. Both
spellings agree on every node.

## Cypher

```cypher
CREATE CONSTRAINT user_identifier IF NOT EXISTS FOR (u:User) REQUIRE u.identifier IS UNIQUE;
```

```cypher
MERGE (u:User {identifier: $ownerId})
ON CREATE SET u.id = $id, u.created_at = datetime($now)
SET u.owner_id = $ownerId,
    u.working_memory = $block,
    u.working_memory_built_at = datetime($now),
    u.working_memory_hash = $hash,
    u.updated_at = datetime($now)
```

Selection is supersession-resolved and validity-gated (`invalidated_at IS NULL`, plus the
`valid_from`/`valid_until` window), and **every `ORDER BY` ends in `id ASC`** — not tidiness: the block
must be byte-stable between input changes, or a rebuild that merely reshuffled equal-ranked rows would
change the hash, write, move `built_at`, and defeat prompt-prefix caching.

## Semantics

**R2 — write-path isolation.** `:User` is written only by `IWorkingMemoryService.RebuildAsync`, called
from the long-term write epilogue. No repository create path touches the label.

**R3 — base-read neutrality.** Absent by construction: `:User` is edge-disconnected from every memory
node, and no base query pattern matches the label.

### GUARD G3 — the null-owner skip is TCK-load-bearing

The TCK bridge's `/add_fact` and `/add_preference` route through `LongTermMemoryService`, so the
rebuild epilogue fires during a conformance run whenever this extension is on. **Bridge writes are
ownerless.** Without the skip, `MERGE (:User {identifier: null})` runs and a null unique key turns
Bronze *and* Gold cases into 500s.

The guard is one `string.IsNullOrWhiteSpace` check — precisely the line a future simplification deletes
as redundant. It is therefore tested against a live database at the seam it protects, and proven
red-first: removing it fails exactly the three ownerless cases.

### Staleness is the kill rule, not a footnote

Structured recall scores 8/9 on knowledge-update — the weakest measured non-episodic type. A block
asserting the **old** value of an updated fact would *manufacture* failures in exactly that type.

So: full eager rebuild, no partial invalidation (invalidation over a graph is the clever answer that
goes stale); rebuild awaited **inline** so the contract is "after the write call returns, the block is
current"; and on rebuild failure the block is **cleared**, because absence degrades to today's
behaviour while staleness manufactures errors. A live canary asserts that superseding `Acme` → `Globex`
through the production path leaves a block containing `Globex` and **not** `Acme`.

## Conformance

No extension TCK profile has shipped yet; `working-memory` runs under the base **178-case** gate. G3 is
what makes that pass with the extension ON, and it is the reason this extension's gate run is
load-bearing rather than ceremonial.

## Parity delta

| Axis | Entries |
|---|---|
| `RemoveUpstreamOnlyLabels` | `User` |
| `AddNetSupersetProperties` | `working_memory`, `working_memory_built_at`, `working_memory_hash` |
| `ReserveUpstreamPropertyNames` | `working_memory`, `working_memory_built_at`, `working_memory_hash` |
| `AddNetOnlyLabels` | *(none — the point is that this stays empty)* |
| `AddNetOnlyRelationshipTypes` | *(none)* |

`ReserveUpstreamPropertyNames` carries meeting proposal P3: we are writing these names onto a node
upstream owns, so we ask upstream not to take them for something else.

## Cost, priced

The structured baseline is 403 tokens per question. A ~300-token block roughly doubles it — and is
still about 1/400th of full-history. That is a declared increase, which is why `MaxTokens` is a hard
budget enforced by dropping whole trailing lines: **entities first, then preferences, then facts**.
Facts are the head of the question distribution, so they are the last thing sacrificed.

**Unmeasured.** No LongMemEval run has been performed. The design's §7 gates — the canary at scale, the
≤320-token cost gate, band behaviour — are all unrun, and the `workingMemoryBlockPresent` void witness
is unexercised.
