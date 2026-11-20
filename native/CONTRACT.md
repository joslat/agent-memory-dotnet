# AgentMemory native contract, version 1

The JSON documents that cross the C ABI in [`include/agentmemory_native.h`](include/agentmemory_native.h). Every document is UTF-8 JSON, one object at the top. The library refuses a document with a repeated property name at any depth, an unknown property, a missing required property or a value of the wrong kind, before it does anything else: a configuration with `AM_V1_INVALID_ARGUMENT` and an error document, a request with a job that ends at once with status `invalid`. Limits: 64 KiB per configuration or request, depth 16.

The contract is for any host: an agent, an agent framework (LangChain, LangGraph) or the speech-to-speech server. Nothing in it belongs to one of them. Operation names follow the AgentMemory MCP tools, so the same operation has the same name in both transports.

## Opening a context: `am_v1_open`

```json
{
  "schemaVersion": 1,
  "profile": "read_only",
  "owner": { "mode": "fixed", "id": "owner-1" },
  "store": {
    "uri": "bolt://neo4j:7687",
    "username": "neo4j",
    "password": "…",
    "database": "neo4j",
    "transactionTimeoutMs": 5000,
    "verifyConnectivity": true
  },
  "embedding": { "dimensions": 768, "model": "nomic-embed-text-v1.5" },
  "limits": { "maxRunningJobs": 4, "maxPendingJobs": 32, "defaultTimeoutMs": 2000 }
}
```

| Property | Meaning |
|---|---|
| `profile` | `read_only` is the only profile of version 1.0: recall of facts and preferences, no writes of any kind (no access stamps, no working-memory rebuild). |
| `owner.mode` | `fixed`: every request reads this one owner, and a request that names an owner is refused (the speech server: one configured owner). `per_request`: every request must name its owner (a multi-user application whose own trusted code knows who is asking). Recall without an owner is refused in both modes. |
| `store` | The Neo4j connection. The password is used to connect and never written to a result or an error. `verifyConnectivity` (default `true`) makes `am_v1_open` fail at once on a wrong address or password. |
| `embedding` | `dimensions` must match the store's vector index; `model` is the caller's declaration of which embedding model made the stored vectors. The store does not record the model, so it cannot be verified. Version 1.0 does not embed: every request carries its query vector. |
| `limits` | `maxRunningJobs` run at once (default 4); up to `maxPendingJobs` are admitted in all, running and queued (default 32). A job holds its place until its work has really finished, even after a timeout, cancel or release. `defaultTimeoutMs` (default 2000) applies when a submit passes 0. At most `maxPendingJobs` + 256 jobs may be held by the caller unreleased; past that, submit returns `AM_V1_OVERLOADED`. |

All of `store.database` (default `neo4j`), `store.transactionTimeoutMs`, `store.verifyConnectivity`, `embedding.model` and `limits` are optional, and so are three driver bounds that make a dead or unreachable store fail fast instead of holding capacity for the driver's own 60 s and 30 s: `store.connectionTimeoutMs` (default 3000), `store.connectionAcquisitionTimeoutMs` (default 5000) and `store.maxRetryTimeMs` (default 1000; 0 means one attempt). The driver's transaction calls take no cancellation, so these bounds, not a cancel, decide how long abandoned work keeps running.

One context per store and database per process: a second open of the same store returns `AM_V1_BUSY` until the first is closed.

An open that fails returns an error document: `{"schemaVersion":1,"error":{"code":"…","message":"…"}}`.

## Requests: `am_v1_submit`

### `memory_get_context`: recall facts and preferences

```json
{
  "schemaVersion": 1,
  "operation": "memory_get_context",
  "requestId": "turn-17/1",
  "owner": "user-42",
  "sessionId": "session-9",
  "queryVector": [0.012, -0.044, 0.31],
  "maxFacts": 5,
  "maxPreferences": 5,
  "minScore": 0.7
}
```

`owner` only in the `per_request` mode. `requestId` is the caller's own correlation value, echoed in the result. `queryVector` must have the configured number of dimensions. `maxFacts` and `maxPreferences` are 0-50, `minScore` 0-1.

Recall reads the owner's own memory only (shared memory excluded), at the current valid time, with every expansion, transcript, trace and graph section off.

### `capabilities_get`

`{"schemaVersion":1,"operation":"capabilities_get"}` returns the library version, ABI version, profile, owner mode, embedding declaration and the supported operations.

## Results: `am_v1_poll`

```json
{
  "schemaVersion": 1,
  "operation": "memory_get_context",
  "requestId": "turn-17/1",
  "status": "ok",
  "facts": [
    {
      "id": "f-1", "subject": "user", "predicate": "works_in", "object": "Zurich",
      "confidence": 0.9, "category": null, "validFrom": null, "validUntil": null,
      "createdAt": "2026-11-20T01:15:00+00:00", "metadata": { "trust_level": "verified" }
    }
  ],
  "preferences": [
    {
      "id": "p-1", "category": "style", "text": "Prefers concise answers", "context": null,
      "confidence": 0.9, "createdAt": "2026-11-20T01:15:00+00:00", "metadata": {}
    }
  ],
  "truncated": false,
  "timing": { "queuedMs": 0, "runMs": 31 }
}
```

`status` is the operation's outcome:

| Status | Meaning |
|---|---|
| `ok` | The recall ran; `facts` and `preferences` hold what it found, possibly nothing. |
| `timeout` | The time allowed ran out. No items. |
| `cancelled` | The caller cancelled the job. No items. |
| `unavailable` | The store could not be reached or refused the connection. No items. |
| `invalid` | The request was well-formed JSON but cannot run (for example a vector of the wrong length). |
| `error` | Anything else; `error.code` and `error.message` say what. |

Every status other than `ok` carries `"error": {"code": "…", "message": "…"}`. Metadata is returned exactly as stored. Results never contain vectors, passwords or stack traces.
