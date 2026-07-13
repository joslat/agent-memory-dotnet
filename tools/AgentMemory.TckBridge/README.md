# AgentMemory.TckBridge

A thin ASP.NET Core Minimal API HTTP bridge implementing the **Bronze, Silver, and Gold tiers** of the upstream
[`neo4j-labs/agent-memory-tck`](https://github.com/neo4j-labs/agent-memory-tck) bridge protocol, so the
Python TCK conformance runner can drive this .NET implementation out-of-process (one `POST` per adapter
method, snake_case JSON wire contract).

Operator/conformance tooling, not a published library (`IsPackable=false`, excluded from the NuGet
meta-package).

## Running

```bash
dotnet run --project tools/AgentMemory.TckBridge
```

Listens on `http://localhost:3001` by default. Set `ASPNETCORE_URLS` (or pass `--urls`) to override — an
explicit value always wins over the built-in default.

## Configuration

Resolved from `appsettings`, environment variables (double-underscore convention, e.g. `Neo4j__Uri`), or
the command line, same as `AgentMemory.Cli`:

| Setting | Fallback |
|---|---|
| `Neo4j:Uri` | `bolt://localhost:7687` |
| `Neo4j:Username` | `neo4j` |
| `Neo4j:Password` | `password` |
| `Neo4j:Database` | `neo4j` |
| `EmbeddingDimensions` | `1536` |

## Endpoints (Bronze)

The Bronze tier is defined by the TCK as "schema and short-term memory", so the bridge serves 12 endpoints:
9 short-term endpoints plus 3 long-term create endpoints exercised by the schema tests.

### Short-term memory

| Route | Purpose |
|---|---|
| `POST /setup` | Bootstrap schema/indexes; waits for vector indexes to come online |
| `POST /teardown` | Wipe all graph data |
| `POST /clear_all_data` | Wipe all graph data (same effect as `/teardown`) |
| `POST /add_message` | Append a message, auto-creating/reusing the session's conversation |
| `POST /get_conversation` | Fetch a session's messages in chronological order |
| `POST /search_messages` | Vector-search messages within a session |
| `POST /list_sessions` | List known sessions with summary stats |
| `POST /delete_message` | Delete a message by id |
| `POST /clear_session` | Clear a single session's short-term memory |

### Schema tier (long-term)

| Route | Purpose |
|---|---|
| `POST /add_entity` | Create a long-term entity; asserts the round-tripped schema shape |
| `POST /add_preference` | Create a long-term preference; asserts the round-tripped schema shape |
| `POST /add_fact` | Create a long-term fact (subject/predicate/object triple) |

Embeddings are produced by `StubEmbeddingGenerator` (no external model calls), which is deterministic
within a single process run — the same text yields the same vector while the bridge is up. It is not a
semantic model and vectors are not guaranteed stable across restarts (it seeds on `string.GetHashCode()`,
which is per-process randomized in .NET); that is fine for the TCK, whose Bronze search scenarios use
`threshold=0.0`. Long-term records default `Confidence` to `1.0`.

## Endpoints (Silver)

The Silver tier adds long-term search/lookup/relationship endpoints and the full reasoning-memory surface:
12 endpoints (5 long-term + 7 reasoning).

### Long-term memory (search, lookup, relationships)

| Route | Purpose |
|---|---|
| `POST /search_entities` | Vector-search entities by query text |
| `POST /search_preferences` | Vector-search preferences by query text, with an optional post-search `category` filter |
| `POST /get_entity_by_name` | Look up an entity by exact name (aliases included); returns `null` if not found |
| `POST /get_related_entities` | BFS traversal of related entities up to a given `depth`, optionally filtered by `relationship_type` |
| `POST /add_relationship` | Create a relationship edge between two entities |

`add_relationship` is nominally classified as a **Gold**-tier endpoint in `bridge-protocol.adoc`, but the
Silver `get_related_entities` scenarios depend on it to set up their fixture data, so the bridge serves it
alongside the other Silver long-term endpoints.

### Reasoning memory

| Route | Purpose |
|---|---|
| `POST /start_trace` | Start a new reasoning trace for a session |
| `POST /add_step` | Append a step (thought/action/observation) to a trace |
| `POST /record_tool_call` | Record a tool invocation against a step |
| `POST /complete_trace` | Mark a trace complete with an outcome/success flag |
| `POST /get_trace_with_steps` | Fetch a trace with its full steps and tool calls; returns `null` if not found |
| `POST /list_traces` | List traces, optionally filtered by session |
| `POST /get_tool_stats` | Aggregate call/success/failure counts and average duration per tool |

Entity/preference search use `minScore=0.0` (matching the Bronze search endpoints' threshold posture), and
IDs that round-trip through the Python TCK client's `UUID()` formatting (entity/trace/step ids) are
normalized to the bridge's stored id format before lookup, the same treatment already applied to
`delete_message`'s `message_id` in the Bronze tier.

## Endpoints (Gold)

The Gold tier adds cross-memory integration scenarios. Most of the 18 Gold tests already pass on the
Silver bridge via the existing cross-memory endpoints plus `add_relationship`; two endpoints were added
specifically for Gold:

| Route | Purpose |
|---|---|
| `POST /merge_duplicate_entities` | Folds a duplicate (source) entity into a canonical (target) one; the target survives and keeps its id. Owner-isolation guarded (mirrors `add_relationship`): both entities must be shared, else 400/404. Rejects a self-merge (`source_id == target_id`) with 400. |
| `POST /get_similar_traces` | Embeds the query task and vector-searches shared-bucket reasoning traces via `IReasoningMemoryService.SearchSimilarTracesAsync`; returns `[]` on an empty store; maps `success_only` to a success filter. |

## Scope

Full Bronze tier (93/93), full Silver tier (67/67), and full Gold tier (18/18) — schema, short-term
memory, long-term search/lookup/relationships, reasoning memory, and cross-memory integration. Only
**Platinum** (hosted-service operations) remains unimplemented — out of scope for a self-hosted library.

## Conformance

With the upstream Python TCK tooling installed and this bridge running against a live Neo4j:

```bash
pytest -m bronze --bridge-url http://localhost:3001
```

Verified result: **93 passed, 0 failed** (the full Bronze tier).

```bash
pytest -m silver --bridge-url http://localhost:3001
```

Verified result: **67 passed, 0 failed** (the full Silver tier).

```bash
pytest -m gold --bridge-url http://localhost:3001
```

Verified result: **18 passed, 0 failed** (the full Gold tier) — **178/178 total** across all three tiers.

All runs were against upstream [`neo4j-labs/agent-memory-tck`](https://github.com/neo4j-labs/agent-memory-tck)
commit `4603b91f` driving this bridge over HTTP against a live Neo4j 5.26. Only Platinum remains
unimplemented and unrun.

## Design rationale

The full design rationale, protocol notes, and open verification items are recorded in the maintainers'
internal implementation plan (not part of the published docs).
