# Observability: the trace a turn produces

AgentMemory emits OpenTelemetry-compatible spans on one `ActivitySource` named **`AgentMemory`**
(`AgentMemoryDiagnostics.SourceName`). Nothing is recorded unless a listener is attached, and a span
with no listener costs one null check. Span names and attribute keys live in one place,
`AgentMemory.Abstractions.Diagnostics.MemoryTelemetry`, so dashboards and code cannot drift apart.

## Enabling it

```csharp
builder.Services.AddOpenTelemetry().WithTracing(t => t
    .AddSource(AgentMemoryDiagnostics.SourceName)          // AgentMemory
    .AddSource("Experimental.Microsoft.Agents.AI")         // MAF agent + tool spans (agent.AsBuilder().UseOpenTelemetry())
    .AddSource("Experimental.Microsoft.Extensions.AI")     // model + embedding calls, token usage (UseOpenTelemetry())
    .AddOtlpExporter());
```

With the Microsoft Agent Framework and Microsoft.Extensions.AI telemetry enabled, one trace covers a whole
agent turn: MAF's `invoke_agent` span is the parent, and AgentMemory's two hook spans sit under it, before
and after the model call (`chat`) and its tools (`execute_tool`).

## The span tree

```text
memory.hook.recall                       PRE hook (session, conversation, owner_scoped, app_scoped)
├─ memory.route                          the recall policy's decision (policy, should_recall, categories, intent)
├─ memory.recall.embedding               the query embedding
│  └─ memory.embed                       inputs / cache hits / sent
├─ memory.recall.total
│  ├─ memory.recall.{section}            one per memory type: memory.type, memory.results.count
│  │  └─ memory.recall.{section}_vector  the vector search behind it
│  │     └─ memory.db.tx → memory.db.query
│  └─ memory.recall.access_tracking
└─ memory.compose                        admission, trust labels, dedup, formatting → memory.context.items
memory.hook.ingest                       POST hook (identity, memory.ingest.messages)
├─ memory.store.messages
├─ memory.store.extract
│  ├─ memory.extract.unified | memory.extraction.{kind}
│  │  └─ memory.extract.attempt          one per model call: outcome, finish reason, tokens, items kept/dropped, error
│  ├─ memory.extract.resolution
│  └─ memory.persist.total
│     └─ memory.db.tx → memory.db.query
memory.background.access_tracking        own trace, LINKED to the recall that queued it
memory.background.enrichment             own trace, LINKED to the ingestion that queued it
```

## Attributes

| Key | On | Meaning |
|---|---|---|
| `memory.session.id`, `memory.conversation.id` | hook spans | Which conversation. The session id is read from W3C baggage (`memory.session.id`) when a host propagates one. |
| `memory.owner_scoped`, `memory.app_scoped` | hook spans | Whether the turn was scoped to an owner / routed to an application store. **Booleans on purpose:** owner and application ids are tenant data, and a plain hash of a guessable id is only a pseudonym. |
| `memory.type` | recall legs | `working`, `episodic`, `semantic`, `entity`, `preference`, `reasoning`, `prospective`, `bitemporal` |
| `memory.results.count` | recall legs | Items the leg returned |
| `memory.route.*` | `memory.route` | `policy`, `should_recall`, `categories`, `intent` |
| `memory.route.temporal.as_of`, `memory.route.fanout.fired` / `.rules` / `.legs` | `memory.hook.recall` | What routing resolved after the policy: the point in time a temporal question resolved to, and whether the fan-out planner split the query (which rules, how many legs) |
| `memory.context.items` | `memory.hook.recall`, `memory.compose` | Context messages handed to the model |
| `memory.compose.flagged` / `.excluded` / `.deduplicated` | `memory.compose` | Recalled items flagged as instruction-like but kept, excluded by the admission policy, and dropped because the live thread already carries them |
| `memory.embed.inputs` / `.cache_hits` / `.sent` | `memory.embed` | Inputs, those answered by `MemoryOptions.EmbeddingCacheCapacity`, those sent to the provider |
| `memory.extract.outcome` | `memory.extract.attempt` | `ok`, `salvaged`, `syntax_error`, `schema_error`, `truncated`, `filtered` |
| `db.system`, `db.mode`, `db.query.fingerprint`, `db.operation.name` | db spans | `neo4j`, read/write, and a stable query name (never the Cypher text) |
| `db.server.available_ms`, `db.server.consumed_ms` | `memory.db.query` | The server's own timings, read from the result summary the caller consumed |
| `db.rows` | `memory.db.query` | Records read |
| `db.result.read` = `none` / `partial` | `memory.db.query` | The caller stopped reading: nothing read, or some records (with `db.rows`); the span ended when the next query started, a retry began, or the transaction ended |
| `db.attempts`, `db.retries` + `db.attempt` events | `memory.db.tx` | How often the driver ran the transaction callback (managed retries were invisible before) |
| `error.type` + `exception` event | any span that failed | The exception type; the status description never carries its message |

## Query fingerprints

Every `memory.db.query` names its query without its text: the registered constant
(`FactQueries.SearchByVector`), a recognised method-built query, or, for AgentMemory's own queries no
marker recognises, `unregistered:<index-or-label>:<hash>` (string literals and numbers are normalised first,
so the name is a query shape: top-K variants share it, and no value can become a dimension). Cypher a caller
passes to the graph-query service is never given a structural name: it is `unknown` unless its text matches a
registered query. Cypher a host runs itself through `INeo4jTransactionRunner` is named like AgentMemory's own
(a normalised shape, never its text or values).

## Timing notes

- A `memory.db.query` span ends when its result has been **read** (consumed or exhausted), not when the
  driver returned a cursor; a result nobody finishes reading ends when the next query starts (the driver
  buffers it then), when a retry begins, or with its transaction, tagged `db.result.read = none | partial`.
- `db.transaction_entry_ms_est` is an upper bound on session and transaction acquisition (the driver does
  not expose pool wait time on its own).

## Privacy

Spans carry names, counts, timings, types and booleans. Message text, memory content, prompts, Cypher text,
and owner or application ids are never attributes. Exceptions are recorded by type.

## Cost

Measured: about 0.7 µs per sampled span and about 17 ns per span with no listener; a full agent turn with
every source enabled adds roughly 0.1 ms.
