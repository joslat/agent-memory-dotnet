# AgentMemory.TckBridge

A thin ASP.NET Core Minimal API HTTP bridge implementing the **Bronze tier** of the upstream
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

Embeddings are produced by the deterministic `StubEmbeddingGenerator` (no external model calls), so search
behavior is reproducible offline. Long-term records default `Confidence` to `1.0`.

## Scope

Full Bronze tier (schema + short-term memory). Silver/Gold/Platinum tiers (long-term
search/reasoning/relationship endpoints) are future follow-up slices and are not implemented by this bridge.

## Conformance

With the upstream Python TCK tooling installed and this bridge running against a live Neo4j:

```bash
pytest -m bronze --bridge-url http://localhost:3001
```

Verified result: **93 passed, 0 failed** (96 Silver/Gold/Platinum scenarios deselected) — the full Bronze
tier, run against upstream [`neo4j-labs/agent-memory-tck`](https://github.com/neo4j-labs/agent-memory-tck)
commit `4603b91f` driving this bridge over HTTP against a live Neo4j 5.26.

## Design rationale

See [`docs/core/tck-bridge-implementation-plan.md`](../../docs/core/tck-bridge-implementation-plan.md) for
the full design rationale, protocol notes, and open verification items.
