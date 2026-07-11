# Proposing Multi-User (`owner_id`) Isolation to neo4j-labs/agent-memory

> **Status: FINAL (refinement pass).** This document is a code-grounded review and a
> copy-paste-ready Issue + PR plan for proposing **per-user long-term isolation** to the upstream
> Python project [github.com/neo4j-labs/agent-memory](https://github.com/neo4j-labs/agent-memory).
> It mirrors the design shipped in the .NET port (`R1` = nullable `owner_id` + reader-side
> `MemoryScope`; `R1b` = optional per-application database tier). Upstream specifics that were
> read verbatim are stated as fact; specifics that the investigation could only obtain via the
> fetch tool's *summary* (notably `graph/queries.py`) are explicitly marked
> **(to confirm on the PR branch)**. The .NET reference snippets in §3 and Appendix A were verified
> against the actual files in this repository.

## Confidence & gaps (read this first)

The proposal rests on two bodies of evidence with different confidence levels.

**Upstream (neo4j-labs/agent-memory) — confirmed (read verbatim from `graph/schema.py`):**
- Schema DDL: constraints, btree indexes, vector indexes (1536 dims, cosine), the `entity_location_idx`
  POINT index.
- A first-class `:User` node with a `UNIQUE` constraint on `User.identifier` (constraint name
  `user_identifier`, labeled *Multi-tenant (v0.4)*).
- **`Entity` and `Fact` carry no `owner_id`/`user_id`/`user_identifier` property and no link to `:User`** —
  they are global/shared.
- `Conversation` carries a denormalized scalar `user_identifier` **plus** a `HAS_CONVERSATION` edge;
  `Preference` and `ReasoningTrace` are scoped only by a `:User` edge (`HAS_PREFERENCE` / `HAS_TRACE`),
  with no scalar on the node.

**Upstream — confirmed from official docs / issues / PRs (not source):**
- `user_identifier` is **optional** on reads and **absent from the documented long-term read
  signatures** (`reference/api/long-term.adoc`).
- `multi_tenant=True` enforces an identifier **only on writes** (`how-to/multi-tenancy.adoc`).
- Omitting the identifier on a read falls back to a **global/anonymous scope** — demonstrated by
  open issue [#135](https://github.com/neo4j-labs/agent-memory/issues/135).

**Upstream — to confirm on the PR branch (the fetch tool *summarized* these; not read line-by-line):**
- `graph/queries.py` — the exact Cypher behind `search_entities` / `get_facts` / `get_preferences`,
  including whether vector search uses `db.index.vector.queryNodes` and whether any `WHERE` owner
  clause already exists. **This is the single most load-bearing file for the read-path change.**
- Whether `CREATE_FACT` is a MERGE on the `(subject, predicate, object)` triple or a CREATE-by-`id`
  (decides whether `owner_key` is strictly required vs. forward-compat only).
- `schema/persistence.py` and `graph/query_builder.py` (dynamic per-type Entity labels) were not
  fetched; class/field names in `schema/models.py` and `memory/reasoning.py` beyond the indexed
  properties are approximate.
- Whether upstream has a **versioned migration runner** or simply re-applies `setup_constraints`/index
  setup on connect (decides whether §5.6 ships a migration file or a bootstrap addition).
- Embedding dimension is **configurable** (`DEFAULT_VECTOR_DIMENSIONS = 1536`); a deployment with a
  different embedder uses different vector dims. All snippets here assume the default.

**.NET reference — confirmed (files re-read in this repository for this pass):**
`src/AgentMemory.Neo4j/Queries/FactQueries.cs`, `src/AgentMemory.Neo4j/Infrastructure/CypherBuilder.cs`,
`src/AgentMemory.Neo4j/Schema/Migrations/0002_owner_scope.cypher`,
`src/AgentMemory.Neo4j/Infrastructure/StoreDatabaseNaming.cs` (path confirmed),
`src/AgentMemory.Neo4j/Infrastructure/{MemoryStoreOptions,Neo4jMemoryStoreProvisioner,Neo4jSessionFactory}.cs`
(paths confirmed). The Cypher/C# excerpts in §3 are quoted/adapted from these files.

## Summary

Upstream `agent-memory` has a genuine per-user *identity* concept (`user_identifier=` on the
Bolt SDK; `workspace_id` on the NAMS hosted backend), but **isolation is opt-in and is enforced
only on the write path** — not on long-term recall. Long-term knowledge nodes (`Entity`, `Fact`)
carry **no owner/user property and no link to `:User`**; they are global. When a caller omits the
identifier on a read, recall silently falls back to a **global/anonymous scope** — a real defect,
already filed as [issue #135](https://github.com/neo4j-labs/agent-memory/issues/135). The result:
**User A can recall User B's stored facts/preferences/entities** by default or by omission. This
proposal is the additive, backward-compatible fix the .NET port already implements: a nullable
`owner_id` on long-term nodes (`NULL` = shared/global), an `owner_key` sentinel in the `Fact`
MERGE dedup key, an over-fetch-then-post-filter recall pattern (required because the Neo4j vector
index cannot pre-filter by property), four `owner_*` property indexes, and a **non-backfilling**
migration. `owner_id = None` reproduces today's behavior exactly, so no existing deployment breaks.

## Table of Contents

1. [Problem statement](#1-problem-statement)
2. [Evidence (where the gap lives upstream)](#2-evidence-where-the-gap-lives-upstream)
3. [Design (ported from the .NET implementation)](#3-design-ported-from-the-net-implementation)
4. [Proposed GitHub Issue (copy-paste ready)](#4-proposed-github-issue-copy-paste-ready)
5. [Proposed PR — file-by-file plan](#5-proposed-pr--file-by-file-plan)
6. [Tests the PR should include](#6-tests-the-pr-should-include)
7. [Backward compatibility & rollout](#7-backward-compatibility--rollout)
8. [Open questions / maintainer decisions](#8-open-questions--maintainer-decisions)

---

## 1. Problem statement

**Upstream long-term memory is global and lacks an *enforced* per-user identity on recall.**

The project advertises "multi-tenant scoping (`user_identifier=`)" as a production feature
(README feature list), and a first-class `:User` node exists with a `UNIQUE` constraint on
`User.identifier` (labeled *Multi-tenant (v0.4)* in `graph/schema.py`). But the isolation is
**relationship-based and opt-in**, with three concrete weaknesses:

1. **`Entity` and `Fact` are global.** Neither node carries a `user_identifier` / `user_id` /
   `owner_id` property, and neither links to `:User`. They are shared across all users. (See
   §2 for the exact write params; this is **confirmed** — read verbatim from `graph/schema.py`
   and the `long_term.py` write params.)

2. **Recall is not isolation-enforced.** `user_identifier` is an **optional** kwarg on reads and
   does **not appear** in the documented long-term read signatures (`search_entities`,
   `get_preferences`, `get_facts`, `get_entity`). The `multi_tenant=True` setting enforces an
   identifier **only on writes** (raising `ValueError` when omitted) — it does nothing on reads.
   Omitting the identifier on a read yields a **global/anonymous scope**.

3. **Session is not user identity.** Short-term memory is scoped by `session_id` (a per-conversation
   container). There is no persistent session↔user binding (only an optional `user_id` filter on
   `list_sessions`). So sessions cannot substitute for cross-user isolation on long-term knowledge.

### Concrete leak scenario

```
1. User A (alice@x.com) chats. The pipeline extracts and stores:
      Fact{subject:"Alice", predicate:"salary_is", object:"$210k"}
      Preference{category:"contact", preference:"never call after 6pm"}
   Entity/Fact/Preference nodes are written with NO owner property.

2. User B (bob@y.com) later asks the agent: "what do you know about salaries / contact rules?"
   The agent calls long_term.search_entities(query=...) / get_facts(...) / get_preferences(...).

3. Because the read path does not require — and by default does not apply — an owner filter,
   the query runs against the GLOBAL scope and returns Alice's salary fact and contact preference
   to Bob.
```

This is exactly the failure mode demonstrated in **issue #135** ("Identity not used for
retrieval"): a wrapper called `long_term.search_entities(query=query, limit=limit)` without
threading `user_identifier`, so recall hit "an anonymous global scope and returned `[]`". The same
omission that returns `[]` in a scoped backend returns **another user's data** in the shared
graph, because the long-term nodes are global.

**Evidence URLs** (full list in §2):
- README feature list & repo: <https://github.com/neo4j-labs/agent-memory>
- `docs/.../how-to/multi-tenancy.adoc` (write-only enforcement)
- `docs/.../reference/api/long-term.adoc` (`user_identifier` absent from read signatures)
- `docs/.../how-to/preferences.adoc` (defaults to shared/global)
- Issue [#135](https://github.com/neo4j-labs/agent-memory/issues/135) (smoking gun)

---

## 2. Evidence (where the gap lives upstream)

Confidence per the investigation: **High** for schema DDL (read verbatim from `graph/schema.py`)
and for the "Entity/Fact have no owner" finding; **Medium** for some property/relationship lists
because `graph/queries.py` and several `memory/*.py` files were summarized by the fetch tool, not
read line-by-line. The library implementation ships as the PyPI package `neo4j-agent-memory`; the
GitHub default branch is `main`, and the tree exposes `docs/`, `examples/`, `benchmarks/` — the
`src/neo4j_agent_memory/**` paths below were read from raw GitHub where available and inferred from
the docs otherwise.

### 2.1 Schema — long-term nodes have no owner (`graph/schema.py`) — **confirmed**

Source: `src/neo4j_agent_memory/graph/schema.py`
(raw: `https://raw.githubusercontent.com/neo4j-labs/agent-memory/main/src/neo4j_agent_memory/graph/schema.py`)

- `:User` exists with `UNIQUE` on `User.identifier` (constraint name `user_identifier`,
  "Multi-tenant (v0.4)").
- **`Entity`** properties: `id, name, type, subtype, canonical_name, description, embedding,
  confidence, created_at, updated_at, metadata, aliases, location, merged_into, merged_at` —
  **no owner/user**. Indexes: `entity_type_idx`, `entity_name_idx`, `entity_canonical_idx`,
  `entity_embedding_idx` (VECTOR 1536/cosine), `entity_location_idx` (POINT).
- **`Fact`** properties: `id, subject, predicate, object, confidence, embedding, valid_from,
  valid_until, metadata` — **no owner/user**. Only index: `fact_embedding_idx` (VECTOR 1536/cosine).
  Upstream `Fact` has **no `fact_category_idx` and no `fact_owner_idx`** (both are .NET-side additions).
- **`Preference`** properties: `id, category, preference, context, confidence, embedding,
  created_at, metadata` — **no owner scalar**; user linkage is edge-only via
  `(:User)-[:HAS_PREFERENCE]->(:Preference)`.
- **`Conversation`** is the *only* long-term-adjacent node with a denormalized user scalar:
  `user_identifier` (plus `archived`, with `conversation_archived_idx` in v0.5). It is set in
  `memory/short_term.py::_link_user_to_conversation` (the following Cypher is **paraphrased from the
  fetch-tool summary** — confirm exact form on the PR branch):
  ```cypher
  MERGE (u:User {identifier: $user_identifier})
  WITH u
  MATCH (c:Conversation {id: $conversation_id})
  MERGE (u)-[:HAS_CONVERSATION]->(c)
  SET   c.user_identifier = $user_identifier
  ```
- `ReasoningTrace` is scoped to the user **edge-only** via `(:User)-[:HAS_TRACE]->(:ReasoningTrace)`;
  the node itself has `id, session_id, success, error_kind, summary, task_embedding` — no owner scalar.

### 2.2 Ingest / write path has no owner on Entity/Fact (`memory/long_term.py`)

Source: `src/neo4j_agent_memory/memory/long_term.py`
(raw: `https://raw.githubusercontent.com/neo4j-labs/agent-memory/main/src/neo4j_agent_memory/memory/long_term.py`)

- **Entity write params:** `{id, name, type, subtype, canonical_name, description, embedding,
  confidence, metadata, location}` — no owner/user.
- **Fact write params** (`queries.CREATE_FACT`): `{id, subject, predicate, object, confidence,
  embedding, valid_from, valid_until, metadata}` — no owner/user. **(to confirm on the PR branch:
  the exact MERGE/CREATE form of `CREATE_FACT` — whether facts are MERGE'd on the SPO triple or
  CREATE'd by `id` — which determines whether the `owner_key` change is in `graph/queries.py`.)**
- `Preference` is associated to a user only through the `HAS_PREFERENCE` edge; no scalar on the node.

### 2.3 Retrieval / search path does not require an owner filter

Source: `docs/modules/ROOT/pages/reference/api/long-term.adoc`
(raw: `https://raw.githubusercontent.com/neo4j-labs/agent-memory/main/docs/modules/ROOT/pages/reference/api/long-term.adoc`)

- The literal string `user_identifier` **does not appear** anywhere in the long-term API
  reference. Read signatures are:
  `search_entities(query, entity_types, limit, threshold)`,
  `get_preferences(category, limit)`,
  `get_facts(subject, predicate, limit)`,
  `get_entity(entity_id)` — none expose a user/owner parameter.
- Multi-tenancy spec (`docs/.../how-to/multi-tenancy.adoc`):
  `multi_tenant=True` "enforces that every **write** includes a `user_identifier`; omitting it
  raises a `ValueError`" — **writes only**. And `user_identifier` "is an optional kwarg on the
  short-term, long-term, and reasoning memory APIs."
- Preferences how-to (`docs/.../how-to/preferences.adoc`): `add_preference(category, preference)`
  shown without `user_identifier`; "Without explicit user-level parameters, preferences appear to
  be globally accessible rather than automatically isolated per user."
- **(to confirm on the PR branch):** the actual Cypher behind `search_entities` / `get_facts` /
  `get_preferences` in `graph/queries.py` — specifically whether vector search uses
  `db.index.vector.queryNodes` and whether any `WHERE` owner clause exists. The implementation was
  summarized, not read verbatim. This is the key file the PR must edit (§5.4).

### 2.4 The smoking gun — issue #135

Source: <https://github.com/neo4j-labs/agent-memory/issues/135> (OPEN, "Identity not used for retrieval")
> In `search_memory`, the wrapper calls `long_term.search_entities(query=query, limit=limit)`. It
> fails to pass `user_identifier=self._user_id`. NAMS searches an anonymous global scope and
> returns `[]`.

This proves long-term recall is **not user-bound unless the caller explicitly threads the
identifier**, and that omission falls back to a global/anonymous scope.

### 2.5 Related upstream context

- PR [#136](https://github.com/neo4j-labs/agent-memory/pull/136) (OPEN): "searches are
  workspace-scoped (user_id scopes writes)" — again, **user_id scopes writes**, reads rely on
  workspace.
- PR [#132](https://github.com/neo4j-labs/agent-memory/pull/132) (merged): NAMS `workspace_id` →
  `X-Workspace-Id` per-request scoping (hosted backend only).
- Issues [#131](https://github.com/neo4j-labs/agent-memory/issues/131) (extraction correctness,
  tangential), [#127](https://github.com/neo4j-labs/agent-memory/issues/127) (Bolt vs NAMS
  capability gaps — affects which scoping surface is available).

---

## 3. Design (ported from the .NET implementation)

The .NET port (`AgentMemory.Neo4j`) closed this exact gap in two additive layers. The design below
is the Python/Cypher adaptation; the C#/Cypher excerpts are quoted from the verified .NET files
(see Appendix A). Every element is **additive and lossless**: with `owner_id = None` everywhere,
the system behaves identically to upstream today.

### 3.1 `R1` — nullable `owner_id` on long-term nodes

Add an **optional** `owner_id` property to every long-term node and to `ReasoningTrace`:
`Fact`, `Entity`, `Preference`, `ReasoningTrace`.

- **`owner_id = NULL` means shared/global** (visible to every owner). This is the default and is
  what all pre-migration data carries — hence backward-compatible by construction.
- **`owner_id = <id>`** identifies the owning user. (Upstream's natural value is the existing
  `user_identifier`; the PR can store `owner_id = user_identifier` to reuse the identity concept.)

This is intentionally a **denormalized scalar** alongside (not instead of) the existing
`(:User)-[:HAS_*]` edges, exactly as upstream already does for `Conversation.user_identifier`. The
scalar is what makes a cheap, index-backed `WHERE` filter possible inside vector recall (§3.3) —
an edge traversal cannot be expressed inside `db.index.vector.queryNodes`.

The .NET reader-side scope model is `MemoryScope { OwnerId: string?, IncludeShared: bool = true }`,
with factories `MemoryScope.Global` (no filter) and `MemoryScope.For(ownerId, includeShared)`. The
Python equivalent is two parameters (`owner_id`, `include_shared`) threaded onto reads (§3.3).

### 3.2 `owner_key` — the `Fact` MERGE dedup key (sentinel for shared)

Facts dedup on the `(subject, predicate, object)` triple via MERGE. If two users independently
assert the **same triple**, a plain SPO MERGE collapses them into one node and lets the second
writer overwrite the first's `owner_id` — cross-user bleed. Fix: add a **non-null** merge key:

```
owner_key = coalesce(owner_id, '*')        # '*' is the sentinel for shared/global
```

and MERGE on `{subject, predicate, object, owner_key}`. This keeps a shared fact (`owner_key='*'`)
and each owner's copy (`owner_key=<id>`) as **distinct nodes**, so the same triple from different
owners never merges. (`owner_key` is required because a nullable `owner_id` cannot itself be a MERGE
key: two rows with `owner_id = NULL` would not match each other in a MERGE pattern, defeating dedup
of shared facts. The non-null sentinel fixes this deterministically.)

The verified .NET `FactQueries.Upsert` (verbatim from
`src/AgentMemory.Neo4j/Queries/FactQueries.cs`) — note that `owner_id` and `owner_key` are set
**only `ON CREATE`** and are deliberately **not reassigned `ON MATCH`**, which is the property that
prevents a second writer from rebinding ownership:

```cypher
MERGE (f:Fact {subject: $subject, predicate: $predicate, object: $object, owner_key: $ownerKey})
ON CREATE SET
    f.id                 = $id,
    f.owner_id           = $ownerId,        // owner bound once, on first write
    f.category           = $category,
    f.confidence         = $confidence,
    f.valid_from         = CASE WHEN $validFrom  IS NOT NULL THEN datetime($validFrom)  ELSE null END,
    f.valid_until        = CASE WHEN $validUntil IS NOT NULL THEN datetime($validUntil) ELSE null END,
    f.source_message_ids = $sourceMessageIds,
    f.created_at         = datetime($createdAtUtc),
    f.metadata           = $metadata
ON MATCH SET
    f.id                 = $id,
    f.category           = $category,
    f.confidence         = $confidence,
    f.valid_from         = CASE WHEN $validFrom  IS NOT NULL THEN datetime($validFrom)  ELSE null END,
    f.valid_until        = CASE WHEN $validUntil IS NOT NULL THEN datetime($validUntil) ELSE null END,
    f.source_message_ids = $sourceMessageIds,
    f.updated_at         = datetime($updatedAtUtc),
    f.metadata           = $metadata
    // NOTE: owner_id / owner_key are intentionally NOT reassigned ON MATCH
RETURN f
```

Caller computes `ownerKey = owner_id ?? '*'`. Only `Fact` needs `owner_key` (only `Fact` MERGEs on
a natural composite key; `Entity`/`Preference` MERGE on `id`).

### 3.3 Recall — over-fetch then post-filter (the vector-index constraint)

**Neo4j limitation:** `db.index.vector.queryNodes(indexName, k, $embedding)` returns the global
top-`k` by similarity and **cannot pre-filter by a node property**. If you simply append
`WHERE owner_id = $owner` after the call, the `k` slots can be entirely consumed by higher-scoring
**foreign** rows, and the owner's legitimate matches fall outside `k` — **starvation**.

**Fix (the pattern the .NET port uses):** request `topK > limit` candidates, apply the owner
`WHERE` clause, *then* `LIMIT $limit`:

```cypher
CALL db.index.vector.queryNodes('fact_embedding_idx', $top_k, $embedding)
YIELD node, score
WHERE score >= $min_score
  AND ($owner_id IS NULL                                   // unscoped == today's behavior
       OR node.owner_id = $owner_id
       OR ($include_shared AND node.owner_id IS NULL))     // shared/global visible
RETURN node, score
ORDER BY score DESC
LIMIT $limit
```

Filter semantics (from `MemoryScope`):
- `owner_id` param is **NULL** ⇒ no owner filter (global; today's behavior, backward-compatible).
- `owner_id` set, `include_shared=True` (default) ⇒ `node.owner_id = $owner_id OR node.owner_id IS NULL`.
- `owner_id` set, `include_shared=False` ⇒ `node.owner_id = $owner_id` (strict).

`top_k` should over-fetch beyond `limit`; the exact factor is an
[open question](#8-open-questions--maintainer-decisions). The verified .NET
`FactQueries.SearchByVector` builds this dynamically via the `CypherBuilder` fluent API (the
`.And(..., when: hasOwnerFilter)` clause is omitted entirely when no owner filter is requested, so
the unscoped query is byte-for-byte today's behavior):

```csharp
// src/AgentMemory.Neo4j/Queries/FactQueries.cs (verbatim)
public static string SearchByVector(bool hasOwnerFilter, bool includeShared, int topK) =>
    new CypherBuilder()
        .WithVectorSearch("fact_embedding_idx", "$embedding", "node", topK)  // CALL db.index.vector.queryNodes('fact_embedding_idx', topK, $embedding) YIELD node, score
        .Where("score >= $minScore")
        .And(includeShared
                ? "(node.owner_id = $ownerId OR node.owner_id IS NULL)"
                : "node.owner_id = $ownerId",
             when: hasOwnerFilter)
        .Return("node, score")
        .OrderBy("score DESC")
        .Limit("$limit")
        .Build();
```

> Note: in the .NET `CypherBuilder`, `WithVectorSearch` interpolates `topK` as a **literal integer**
> into the `queryNodes(...)` call (it is an `int`, never user-supplied text). In Python, either bind
> `$top_k` as a parameter or interpolate a validated integer — never interpolate untrusted text.

### 3.4 `owner_*` property indexes

Back the `WHERE owner_id = $owner` post-filter with property indexes so the filter is cheap:

```cypher
CREATE INDEX fact_owner_idx       IF NOT EXISTS FOR (f:Fact)           ON (f.owner_id);
CREATE INDEX entity_owner_idx     IF NOT EXISTS FOR (e:Entity)         ON (e.owner_id);
CREATE INDEX preference_owner_idx IF NOT EXISTS FOR (p:Preference)     ON (p.owner_id);
CREATE INDEX trace_owner_idx      IF NOT EXISTS FOR (t:ReasoningTrace) ON (t.owner_id);
```

### 3.5 Non-backfilling migration

Ship the four indexes for both fresh and existing databases. **Do not rewrite any node.** Pre-existing
`Fact`/`Entity`/`Preference` keep `owner_id = NULL` and are therefore treated as shared/global —
upgrade is lossless and visible to everyone until re-extracted under a concrete owner. The verified
.NET migration (verbatim, `src/AgentMemory.Neo4j/Schema/Migrations/0002_owner_scope.cypher`):

```cypher
// Migration 0002 — owner-scope property indexes (R1, multi-user isolation).
//
// Adds owner_id property indexes that accelerate the owner filter applied during scoped
// vector recall. Fresh deployments already pick these up via SchemaBootstrapper; this
// migration brings existing databases to parity.
//
// NON-BACKFILLING BY DESIGN: pre-existing Fact/Entity/Preference nodes keep owner_id = NULL,
// which the scope model treats as shared/global knowledge (MemoryScope.IncludeShared). No
// node is rewritten, so the upgrade is lossless and backward-compatible — prior memories
// remain visible to every owner until they are re-extracted under a concrete owner_id.
//
// Each statement is idempotent (IF NOT EXISTS) and applied in its own transaction by
// MigrationRunner (schema operations cannot share a transaction with the migration record).

CREATE INDEX fact_owner_idx IF NOT EXISTS FOR (f:Fact) ON (f.owner_id);
CREATE INDEX entity_owner_idx IF NOT EXISTS FOR (e:Entity) ON (e.owner_id);
CREATE INDEX preference_owner_idx IF NOT EXISTS FOR (p:Preference) ON (p.owner_id);
CREATE INDEX trace_owner_idx IF NOT EXISTS FOR (t:ReasoningTrace) ON (t.owner_id);
```

### 3.6 (Optional) `R1b` — per-application database tier

A second, **optional** isolation tier sits *above* the owner: a three-tier model
`store/application ⊃ owner/user ⊃ session`. Two strategies (from the .NET
`MemoryStorageStrategy` enum):

1. **Shared database (default):** one Neo4j database; users isolated logically via `owner_id` (R1).
   Works on **Community Edition** (which supports only one user database).
2. **Database-per-application:** each application id routes to its own Neo4j database. **Requires
   Neo4j Enterprise or AuraDB** (Community supports only one user database). The .NET port resolves
   names with collision-safe truncation: max 63 chars, first char an ASCII letter, remaining
   `[a-z0-9.-]`, with a short hash appended on truncation so two long ids cannot collide onto the
   same physical database (`src/AgentMemory.Neo4j/Infrastructure/StoreDatabaseNaming.cs`). First use
   does `CREATE DATABASE ... WAIT` on the system database then re-bootstraps all constraints/indexes;
   the provisioner throws an actionable `NotSupportedException` on Community Edition.

For the upstream PR, `R1b` is **out of scope for v1** and listed as a follow-up — `R1` alone closes
the leak on Community Edition. Mention it so maintainers know the upgrade path exists and is
additive.

---

## 4. Proposed GitHub Issue (copy-paste ready)

> **Title:** Long-term recall is not isolation-enforced per user (Entity/Fact are global; omitting
> `user_identifier` returns another user's data)

> **Labels:** `bug`, `security`, `multi-tenancy`, `long-term`, `enhancement`
> *(remove any labels that don't exist in the repo's label set before filing.)*

> **Body:**
>
> ### Problem
> `Entity` and `Fact` nodes have no owner/user property and no link to `:User` — they are global
> (`graph/schema.py`; write params in `memory/long_term.py`). The long-term read API
> (`search_entities`, `get_facts`, `get_preferences`, `get_entity`) does not require — and by default
> does not apply — an owner filter; `user_identifier` is **absent from the documented read
> signatures** (`docs/.../reference/api/long-term.adoc`). `multi_tenant=True` enforces an identifier
> **only on writes** (`docs/.../how-to/multi-tenancy.adoc`). So omitting the identifier on a read
> falls back to a **global/anonymous scope**.
>
> ### Impact (security / privacy)
> User A can recall User B's stored facts, entities, and preferences:
> 1. User A's turn extracts `Fact{subject:"Alice", predicate:"salary_is", object:"$210k"}` and a
>    private `Preference` — written with **no owner**.
> 2. User B calls `search_entities(...)` / `get_facts(...)` / `get_preferences(...)` without an
>    identifier (the read API does not require one).
> 3. Recall runs against the global scope and returns Alice's data to Bob.
>
> This is the same failure class as #135 ("Identity not used for retrieval"): the wrapper omitted
> `user_identifier`, recall hit an anonymous global scope. In a shared graph, that anonymous scope
> contains **every** user's long-term knowledge.
>
> ### Proposal (additive, backward-compatible)
> 1. Add a nullable `owner_id` to `Fact`, `Entity`, `Preference`, `ReasoningTrace`. `NULL` = shared/global.
> 2. Add `owner_key = coalesce(owner_id, '*')` to the `Fact` SPO MERGE key so the same triple from
>    two users stays in two distinct nodes (no cross-user overwrite). *(Applies if `CREATE_FACT`
>    MERGEs on the SPO triple; if it CREATEs by `id`, `owner_key` is forward-compat only — see open
>    questions.)*
> 3. Make the long-term **read** path apply an owner filter when an identifier is supplied. Because
>    `db.index.vector.queryNodes` cannot pre-filter by property, **over-fetch `top_k > limit`** then
>    `WHERE (owner_id = $owner OR owner_id IS NULL) ... LIMIT $limit` (avoids starvation).
> 4. Add `fact_owner_idx`, `entity_owner_idx`, `preference_owner_idx`, `trace_owner_idx`.
> 5. Ship a **non-backfilling** migration: indexes only; pre-existing nodes keep `owner_id = NULL`
>    (treated as shared) — lossless upgrade.
>
> ### Backward compatibility
> `owner_id = None` everywhere reproduces today's behavior exactly. Existing data becomes
> shared/global (visible to all), unchanged. No node is rewritten. Opt-in per-user isolation
> requires only passing the existing `user_identifier` on reads. (Optional follow-up: enforce
> reads when `multi_tenant=True`, mirroring the existing write enforcement.)
>
> ### Reference implementation
> The .NET port `agent-memory-dotnet` implements exactly this (`R1`): nullable `owner_id`,
> `owner_key` sentinel, over-fetch+post-filter vector recall, four `owner_*` indexes, and a
> non-backfilling migration `0002_owner_scope.cypher`. Happy to open a PR.

---

## 5. Proposed PR — file-by-file plan

Paths are taken from the gathered facts (`src/neo4j_agent_memory/**`); where the implementation
was only summarized by the fetch tool, the path is marked **(confirm path)**. The PR is intentionally
split into schema / models / write / read / migration so each diff is reviewable.

### 5.1 `src/neo4j_agent_memory/graph/schema.py` — add owner indexes

Add four property indexes to the constraint/index bootstrap (alongside the existing
`setup_constraints` / index setup). Use `IF NOT EXISTS` so fresh databases are idempotent and the
migration (5.6) is a no-op against them.

```python
# new owner-scope property indexes (R1 multi-user isolation)
OWNER_INDEXES = [
    ("fact_owner_idx",       "Fact",           "owner_id"),
    ("entity_owner_idx",     "Entity",         "owner_id"),
    ("preference_owner_idx", "Preference",     "owner_id"),
    ("trace_owner_idx",      "ReasoningTrace", "owner_id"),
]
# emitted as: CREATE INDEX {name} IF NOT EXISTS FOR (n:{Label}) ON (n.{prop})
```

Leave the embedding-dimension config path untouched (vector indexes stay at the configured
`DEFAULT_VECTOR_DIMENSIONS`, default 1536, cosine).

### 5.2 `src/neo4j_agent_memory/schema/models.py` — add `owner_id` to dataclasses

Add an optional `owner_id: str | None = None` field to the `Entity`, `Fact`, `Preference`, and
`ReasoningTrace` models (and to whatever DTO/dataclass `users.py` / `long_term.py` map to Cypher
params). Default `None` so existing constructors and callers are unchanged. **(confirm path:** exact
class names, and whether persistence mapping lives in `schema/persistence.py`, which was not fetched.**)**

### 5.3 Write path — `src/neo4j_agent_memory/memory/long_term.py` + `graph/queries.py` (confirm path)

**Entity / Preference write params:** add `owner_id` (default `None`) to the param dict; set it in
the MERGE/SET. These MERGE on `id`, so no `owner_key` needed.

**Fact write (`queries.CREATE_FACT`, confirm path):** this is the load-bearing change. If the query
MERGEs on the SPO triple today, replace the key with the 4-tuple including `owner_key`, and set
`owner_id`/`owner_key` **only `ON CREATE`** (mirroring the verified .NET `FactQueries.Upsert`):

```cypher
// before (illustrative — confirm exact upstream form on the PR branch):
//   MERGE (f:Fact {subject:$subject, predicate:$predicate, object:$object})
// after:
MERGE (f:Fact {subject:$subject, predicate:$predicate, object:$object, owner_key:$owner_key})
ON CREATE SET f.id=$id, f.owner_id=$owner_id, f.confidence=$confidence, f.embedding=$embedding,
              f.valid_from=$valid_from, f.valid_until=$valid_until, f.metadata=$metadata,
              f.created_at=datetime()
ON MATCH  SET f.confidence=$confidence, f.valid_from=$valid_from, f.valid_until=$valid_until,
              f.metadata=$metadata, f.updated_at=datetime()
              // owner_id / owner_key are NOT reassigned ON MATCH (prevents ownership rebind)
RETURN f
```

In Python, compute the sentinel before the call:
```python
owner_key = owner_id if owner_id is not None else "*"
params = {..., "owner_id": owner_id, "owner_key": owner_key}
```

If upstream `CREATE_FACT` instead CREATEs by `id` (not a SPO MERGE), the dedup concern is different —
**(to confirm on the PR branch)** — but `owner_id` is still set and `owner_key` is set for forward
compatibility (and to make a later switch to SPO-MERGE safe).

### 5.4 Read path — `graph/queries.py` + `memory/long_term.py` (confirm path) — over-fetch + filter

This is the change that actually closes the leak. For each long-term vector search
(`search_entities`, the fact search behind `get_facts`/semantic fact recall, preference search):

1. Add `owner_id: str | None = None` and `include_shared: bool = True` to the read method
   signatures (`long_term.py`), and surface them through the public API. Threading the existing
   `user_identifier` into `owner_id` is the natural default.
2. Rewrite the Cypher to over-fetch and post-filter:

```cypher
CALL db.index.vector.queryNodes($index_name, $top_k, $embedding)
YIELD node, score
WHERE score >= $min_score
  AND ($owner_id IS NULL                                   // unscoped == today's behavior
       OR node.owner_id = $owner_id
       OR ($include_shared AND node.owner_id IS NULL))     // shared/global visible
RETURN node, score
ORDER BY score DESC
LIMIT $limit
```

> Some Neo4j/driver versions reject a parameter as the index name in `queryNodes`. If `$index_name`
> is rejected, select the literal index constant in Python per call rather than parameterizing it
> (do **not** interpolate untrusted text).

3. Compute `top_k` in Python: `top_k = max(limit * OVER_FETCH_FACTOR, limit + OVER_FETCH_FLOOR)`
   (constants are an [open question](#8-open-questions--maintainer-decisions); over-fetch beyond
   `limit` so the owner filter is not starved).

For **non-vector** reads (`get_preferences(category, limit)`, `get_facts(subject, predicate,
limit)`, `get_entity(entity_id)`), add the same `WHERE` owner clause directly (no over-fetch needed
since there is no top-k starvation):

```cypher
MATCH (p:Preference)
WHERE ($category IS NULL OR p.category = $category)
  AND ($owner_id IS NULL OR p.owner_id = $owner_id OR ($include_shared AND p.owner_id IS NULL))
RETURN p LIMIT $limit
```

### 5.5 Reasoning path — `src/neo4j_agent_memory/memory/reasoning.py` (confirm path)

Add `owner_id` to `ReasoningTrace` writes and apply the same owner `WHERE` clause (backed by
`trace_owner_idx`) to trace recall (e.g. the `task_embedding_idx` vector search, using the same
over-fetch pattern as §5.4). The reasoning file was not read; treat field/signature names as
**(to confirm on the PR branch)**.

### 5.6 Migration — `src/neo4j_agent_memory/graph/migrations/` (confirm path)

Add a non-backfilling migration mirroring `0002_owner_scope.cypher` (§3.5). If upstream has no
migration runner, the four `IF NOT EXISTS` index statements can live in the schema bootstrap
(5.1) and the migration step is a documentation note instead. **(confirm path: does upstream have a
versioned migration mechanism, or only `setup_constraints`/index setup re-run on connect?)**

### 5.7 Docs — `docs/modules/ROOT/pages/how-to/multi-tenancy.adoc` + `reference/api/long-term.adoc`

- Document `owner_id` / `include_shared` on the long-term read methods (they are currently absent
  from the reference).
- Update `multi-tenancy.adoc` to state that reads now accept and apply an owner filter, and (if the
  maintainers choose, see §8) that `multi_tenant=True` enforces an identifier on reads too.
- Add the leak scenario + the `NULL = shared/global` semantics.

### 5.8 Optional follow-up PR — `R1b` per-application database tier

Separate, later PR. Add a storage-strategy option (`SharedDatabase` default / `DatabasePerApplication`),
per-request database routing, and a provisioner that does `CREATE DATABASE ... WAIT` + re-bootstrap
on Enterprise/AuraDB, with a clear `NotSupportedError` on Community. Mirrors
`src/AgentMemory.Neo4j/Infrastructure/{MemoryStoreOptions,StoreDatabaseNaming,Neo4jMemoryStoreProvisioner,Neo4jSessionFactory}.cs`.

---

## 6. Tests the PR should include

### 6.1 Unit (Cypher-shape / param tests, no DB)
- `Fact` upsert computes `owner_key = owner_id or '*'` (None → `'*'`; concrete id → id).
- Read methods thread `owner_id`/`include_shared` into params; `owner_id=None` produces the
  unscoped clause (no owner restriction at all — the `AND` is omitted, not emitted as a tautology).
- `top_k` over-fetch computation: `top_k > limit` for all `limit`.

### 6.2 Integration (Neo4j; testcontainers / live)
1. **Isolation — A cannot see B.** Write `Fact`/`Entity`/`Preference` under `owner_id="A"`; recall
   with `owner_id="B", include_shared=False` returns none of A's rows. Recall with `owner_id="A"`
   returns them.
2. **Shared (NULL) visible to all.** Write a node with `owner_id=None`; recall under `owner_id="A"`
   and `owner_id="B"` (both `include_shared=True`) both return it; with `include_shared=False`
   neither does.
3. **Fact no-overwrite across owners.** Two writers assert the **same** `(s,p,o)` triple under
   `owner_id="A"` and `owner_id="B"`. Assert **two distinct** `Fact` nodes exist
   (`owner_key="A"`, `owner_key="B"`), and that re-writing A's triple did not change A's `owner_id`.
   Then a third write with `owner_id=None` yields a third node (`owner_key="*"`).
4. **Over-fetch no-starvation.** Insert `N` high-similarity facts for owner "B" (foreign) and a few
   slightly-lower-similarity facts for owner "A", with `N > limit`. Recall with `owner_id="A",
   limit=k` returns A's facts even though B's would dominate the raw top-`k`. (Regression guard for
   the `queryNodes`-cannot-pre-filter constraint.)
5. **Backward-compat / unscoped.** Recall with `owner_id=None` returns the union of all owners'
   rows (today's behavior) — proves the additive default.
6. **Migration is non-backfilling & lossless.** Seed a DB with owner-less nodes (pre-R1 shape),
   run the migration, assert the four indexes exist and every pre-existing node still has
   `owner_id IS NULL` (none rewritten) and is recalled as shared.

### 6.3 Regression for #135
- The wrapper read path (`search_memory` equivalent) threads the caller's identity into `owner_id`;
  with an identity set, an isolated user sees their own + shared rows, not the global union, and a
  non-empty result is returned for that user's own data (the inverse of the `[]` symptom in #135).

---

## 7. Backward compatibility & rollout

- **Additive, zero breaking change.** Every new field/param defaults to `None`/`True`. Existing
  call sites compile and behave identically. `owner_id = None` on read == today's global recall, and
  the owner `AND` clause is omitted entirely (not emitted as a no-op), so the unscoped query is
  unchanged.
- **Migration is non-backfilling.** Only creates indexes (`IF NOT EXISTS`). No node is rewritten;
  pre-existing data keeps `owner_id = NULL` and is treated as shared/global — **lossless**. Existing
  knowledge stays visible to everyone until re-extracted under a concrete `owner_id`.
- **Forward-compatible API.** Landing the `owner_id`/`include_shared` parameters (even if some
  callers ignore them) before a major release avoids a future breaking signature change — the same
  reasoning that put R1 into the .NET API before its first NuGet publish.
- **Community Edition works** with R1 alone (single database, logical isolation). R1b
  (database-per-application) is the only part needing Enterprise/AuraDB and is an opt-in follow-up.
- **SemVer.** Pure addition ⇒ a **minor** release. If maintainers choose to make `multi_tenant=True`
  *also enforce reads* (raise when an identifier is omitted on a read), that is a **behavior change
  under the existing flag** — arguably still minor since it's gated by an opt-in flag, but worth a
  changelog callout and possibly its own setting (`enforce_read_isolation`).

---

## 8. Open questions / maintainer decisions

1. **Reuse `user_identifier` or introduce `owner_id`?** The cleanest path is to store
   `owner_id = user_identifier` (one identity concept). Confirm whether the maintainers want a
   distinct `owner_id` namespace or to overload the existing identifier.
2. **Enforce reads under `multi_tenant=True`?** Today the flag enforces writes only. Should it also
   require an identifier on long-term reads (raise on omission), or stay opt-in with `None = global`?
   A separate `enforce_read_isolation` flag avoids changing the meaning of the existing one.
3. **Over-fetch factor.** What `top_k` multiplier/floor balances recall completeness vs cost?
   Upstream should pick a default and possibly make it configurable per `MemorySettings`. (No
   benchmark is asserted here — the right value depends on dataset cardinality and owner skew, so
   this should be measured, not guessed.)
4. **`Fact` MERGE shape.** Is `CREATE_FACT` a SPO MERGE or a CREATE-by-id? This decides whether
   `owner_key` is strictly required (SPO MERGE) or only forward-compat (CREATE-by-id).
   **(to confirm on the PR branch — `graph/queries.py` was summarized, not read verbatim.)**
5. **Should `Entity` be owner-scoped, shared, or hybrid?** Entities (e.g. public companies) are
   often legitimately shared. Options: (a) always owner-scope on write; (b) leave entity `owner_id`
   `NULL` (shared) by default and scope only `Fact`/`Preference`; (c) per-call choice. The .NET port
   adds `owner_id` to all three but defaults to shared via NULL — confirm upstream's preference.
6. **Migration mechanism.** Does upstream have a versioned migration runner, or are
   constraints/indexes re-applied on connect? Determines whether §5.6 is a migration file or a
   bootstrap addition.
7. **`ReasoningTrace`/`Preference` — scalar vs edge.** Upstream scopes these by `:User` edge today.
   Adding a denormalized `owner_id` scalar is what enables index-backed filtering inside
   `queryNodes`; confirm maintainers accept the (intentional) denormalization, consistent with the
   existing `Conversation.user_identifier` precedent.
8. **NAMS hosted backend.** This plan targets the Bolt/Cypher path. How `owner_id` maps onto the
   NAMS `workspace_id` / `userId` model (and whether NAMS already isolates at the service layer) is
   a separate, backend-specific question (see #127, #132, #136).

---

### Appendix A — .NET reference artifacts (for the PR author)

These files in `agent-memory-dotnet` are the concrete reference for each change. All paths were
re-verified against the working tree for this pass.

| Concern | .NET file |
| --- | --- |
| `Fact` `owner_key` MERGE + scoped vector search | `src/AgentMemory.Neo4j/Queries/FactQueries.cs` |
| Over-fetch vector-search builder (`db.index.vector.queryNodes(topK)`) | `src/AgentMemory.Neo4j/Infrastructure/CypherBuilder.cs` (`WithVectorSearch`, `Where`, `And(..., when:)`) |
| Owner indexes + non-backfilling migration | `src/AgentMemory.Neo4j/Schema/Migrations/0002_owner_scope.cypher` |
| `R1b` database naming (collision-safe truncation) | `src/AgentMemory.Neo4j/Infrastructure/StoreDatabaseNaming.cs` |
| `R1b` options / provisioner / session routing | `src/AgentMemory.Neo4j/Infrastructure/{MemoryStoreOptions,Neo4jMemoryStoreProvisioner,Neo4jSessionFactory}.cs` |
| Full design rationale | `docs/archive/Memory_Review_and_Implementation_Plan.md` |

### Appendix B — Schema gap at a glance

Upstream columns are **confirmed** from `graph/schema.py`.

| Node | Upstream owner property | Upstream user link | This proposal |
| --- | --- | --- | --- |
| `Conversation` | `user_identifier` (scalar) | `(:User)-[:HAS_CONVERSATION]->` | unchanged |
| `ReasoningTrace` | none | `(:User)-[:HAS_TRACE]->` | add `owner_id` + `trace_owner_idx` |
| `Preference` | none | `(:User)-[:HAS_PREFERENCE]->` | add `owner_id` + `preference_owner_idx` |
| `Entity` | **none** | **none** | add `owner_id` + `entity_owner_idx` (default shared via NULL) |
| `Fact` | **none** | **none** | add `owner_id` + `owner_key` + `fact_owner_idx` |
| `Message` | none | (via Conversation) | unchanged |
