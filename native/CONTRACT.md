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
| `profile` | `read_only`: recall of facts and preferences, no writes of any kind (no access stamps, no working-memory rebuild). `explicit_write`: the same recall, plus remembering the owner's own words through a durable journal (see [Explicit writes](#explicit-writes-profile-explicit_write)). |
| `owner.mode` | `fixed`: every request reads this one owner, and a request that names an owner is refused (the speech server: one configured owner). `per_request`: every request must name its owner (a multi-user application whose own trusted code knows who is asking). Recall without an owner is refused in both modes. |
| `store` | The Neo4j connection. The password is used to connect and never written to a result or an error. `verifyConnectivity` (default `true`) makes `am_v1_open` fail at once on a wrong address or password. `uri` schemes: `bolt://` and `neo4j://` in the clear; `bolt+s://` and `neo4j+s://` over TLS, with the store's certificate checked against the system's trusted roots and the host name in the URI; `bolt+ssc://` and `neo4j+ssc://` over TLS accepting any certificate (encrypted, but the store is not authenticated). A certificate the system refuses fails as `unavailable` with code `store_tls`. |
| `embedding` | `dimensions` must match the store's vector index; `model` names the embedding model that made the stored vectors (the store does not record it, so it cannot be verified). Optional `endpoint` `{ "url", "apiKey", "timeoutMs" }`: an OpenAI-compatible `/v1/embeddings` URL (Ollama, llama.cpp, vLLM, OpenAI) that the library asks for `model` vectors, so requests may send `query` text; every vector it returns must have `dimensions` numbers. The API key is sent only to that URL. |
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
  "minScore": 0.7,
  "maxMessages": 5,
  "maxEntities": 10,
  "maxRelationships": 10,
  "timeWindows": true,
  "maxConnectionFacts": 8
}
```

Exactly one of `query` (text, up to 8,000 characters; needs `embedding.endpoint` in the configuration, and the library embeds it and reports any embedding failure as such) and `queryVector`. `owner` only in the `per_request` mode. `requestId` is the caller's own correlation value, echoed in the result. `queryVector` must have the configured number of dimensions. `maxFacts` and `maxPreferences` are 0-50, `minScore` 0-1 (default 0.7). Scores are Neo4j vector-index scores, `(1 + cosine) / 2`: an unrelated item (cosine 0) scores 0.5, so a useful threshold sits well above 0.5.

Recall reads the owner's own memory only (shared memory excluded), at the current valid time, with every expansion, transcript, trace and graph section off unless a request asks for one of these:

| Property | Default | Adds to the result |
|---|---|---|
| `maxMessages` (0-20) | 0 | `messages`: the messages of this request's `sessionId` most like the question (not other sessions). |
| `maxEntities` (0-50) | 0 | `entities`: the people, places and things most like the question. |
| `maxRelationships` (0-50) | 0 | `relationships`: how those relate ("Sam is the user's sister"). |
| `timeWindows` | false | `windowFacts`: the facts of a time the question names ("yesterday", "last week"), by their date fields (a date only inside a fact's text is not one). |
| `maxConnectionFacts` (0-32) | 0 | More `facts`: when a recalled fact connects the owner to someone by a relation the question names ("Anna \| sister of \| user" for "where does my sister live?"), up to this many of that one's own live facts ("Anna \| lives in \| Madrid"), one hop, at most three connections. |

Person and event details are not offered: they need identity-evidence storage, which the native profiles have not.

With none of them, a recall is exactly what it was before they existed, and its new result arrays are empty.

### `capabilities_get`

`{"schemaVersion":1,"operation":"capabilities_get"}` returns the library version, ABI version, profile, owner mode, embedding declaration and the supported operations. From ABI 1.1 it also returns `features`: the optional exports and request properties this context accepts, by name (`am_v1_wait`, `get_context.timeWindows`, `get_context.maxConnectionFacts`, `writer.contextTurns`, `remember.origin`, ...). A binding checks `operations` and `features` before it offers something, and a 1.0 library, which has no `features`, offers none of the 1.1 additions.

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
  "windowFacts": [],
  "messages": [ { "id": "m-1", "role": "user", "text": "Yesterday I had dinner with the speakers.", "at": "2026-11-19T20:00:00+00:00" } ],
  "entities": [ { "id": "e-1", "name": "Sam", "type": "PERSON", "description": null } ],
  "relationships": [ { "id": "r-1", "source": "Sam", "type": "SISTER_OF", "target": "user", "description": null } ],
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

## The schema (profile `admin`)

A store must hold AgentMemory's schema before any other profile can use it. The `admin` profile prepares it with no
.NET installation:

```json
{"schemaVersion": 1, "profile": "admin",
 "store": {"uri": "bolt://localhost:7687", "username": "neo4j", "password": "..."},
 "embedding": {"dimensions": 1024}}
```

`owner` is optional (no owner's memory is read). It offers `capabilities_get` and two operations:

| Operation | What it does | Result |
|---|---|---|
| `schema_status` | Reads what the store holds of the schema; changes nothing | `schema`: `complete`, `empty`, `expected` (how many constraints and indexes the bootstrap creates), `missing` (their names), `dimensions` (configured), `dimensionsMatch`, `vectorIndexes` (each vector index with its dimensions); `bootstrapped: false` |
| `schema_bootstrap` | Creates what is missing and applies the schema migrations, exactly as the managed bootstrap does (`AgentMemory.Cli bootstrap`); what is there is left as it is, so a second run changes nothing | the same `schema`, read after it; `bootstrapped: true`. Give it a job time of its own (a first bootstrap takes seconds): the Python binding's `schema_bootstrap()` waits up to 120 s |

The other profiles do not offer either operation, and `admin` offers no recall or write. One context per store and
database per process holds for `admin` too: close it before opening the store with another profile.

## Explicit writes (profile `explicit_write`)

The owner says "remember that …", and the host can tell truthfully whether it was saved, is being saved, failed, or is uncertain. Writes go through one durable journal per state directory; one writer runs them in acceptance order.

Configuration, in addition to the read-only properties:

```json
{
  "profile": "explicit_write",
  "owner": { "mode": "fixed", "id": "owner-1" },
  "embedding": { "dimensions": 1024, "model": "bge-m3",
                 "endpoint": { "url": "http://ollama:11434/v1/embeddings" } },
  "writer": { "model": "llama3.2:3b", "reasoningEffort": "low", "contextTurns": 4,
              "endpoint": { "url": "http://ollama:11434/v1/chat/completions", "apiKey": "…", "timeoutMs": 60000 } },
  "state": { "directory": "/var/lib/agentmemory" },
  "limits": { "maxQueuedWrites": 16 }
}
```

| Property | Meaning |
|---|---|
| `owner.mode` | Must be `fixed`: one configured owner writes. |
| `embedding.endpoint` | Required: the library embeds what it writes. |
| `writer` | The model that turns the owner's words into facts and preferences: any OpenAI-compatible `/v1/chat/completions` endpoint. It runs AgentMemory's store-aware writer (it sees the owner's most relevant stored memories, so a change replaces what it changes), with no fallback to other extractors, the update judge on, and entity identity off; no setting turns identity on. `timeoutMs` (default 60000) bounds one model call. `reasoningEffort` (`none`, `low`, `medium` or `high`; default: not sent) is sent as `reasoning_effort` to models that think before answering: GLM-5.3-Flash took 2.1 s and 607 thinking tokens on a small extraction by default, 0.5 s and 3 with `low`. Leave it out for an endpoint or model that refuses the field. `contextTurns` (0-32, default 0) gives the writer that many earlier turns of the same session as read-only context, so "his name is Sam" can reach "I met somebody at dinner"; nothing is ever kept from a context turn. A turn that cannot carry a fact (a plain question, "ok, thanks") is stored but costs no model call and ends `applied` with `nothing_to_keep`. |
| `state.directory` | An absolute path. The library creates it (mode 0700; on Windows, an access list that grants only the creating user and SYSTEM, inherits nothing from the parent folder, and is inherited by the files inside) and keeps the journal there (`journal.jsonl`, mode 0600), held with an exclusive lock: a second open of the same directory, in this process or another, returns `AM_V1_BUSY` with code `state_locked`. A journal damaged before its last line refuses the open (`journal_corrupt`); an unfinished last line (an append that never returned) is cut off. |
| `limits.maxQueuedWrites` | The most writes accepted and not yet finished (default 16); past it, `memory_remember` is refused with `write_queue_full` and nothing is accepted. |

### `memory_remember`

```json
{"schemaVersion": 1, "operation": "memory_remember", "requestId": "r-7",
 "sourceId": "local-demo/session-3/item-17/rev-1", "sessionId": "session-3",
 "text": "Please remember that I prefer concise answers.", "occurredAt": "2026-11-20T10:00:00Z"}
```

`sourceId` (up to 256 characters) is the host's stable identity for this spoken item and revision. The same `sourceId` with the same session, text and time returns the existing receipt (`duplicate: true`) and runs nothing, which is the right retry after a lost answer; the same `sourceId` with anything else changed is refused as `source_conflict`. `occurredAt` is when the words were said, with an explicit offset: dates the words state are read against it. No request may name an owner.

The job ends when the write ends or the job's time runs out. From the moment the write is accepted (durably appended to the journal), every result is a receipt with `status: "ok"`, even when the caller stops waiting, cancels, or the context closes: the write goes on without the job. A result without a receipt means nothing was accepted: `invalid` (the request), or `unavailable` with `journal_unavailable`, `write_queue_full` or `closing`.

### Receipts

```json
{"schemaVersion": 1, "operation": "memory_remember", "requestId": "r-7", "status": "ok",
 "receipt": {"operationId": "op-…", "sourceId": "local-demo/session-3/item-17/rev-1", "duplicate": false,
             "state": "applied", "reason": null, "searchable": true,
             "occurredAt": "…", "acceptedAt": "…", "startedAt": "…", "finishedAt": "…",
             "facts": [], "preferences": ["…"], "closed": []},
 "timing": {"queuedMs": 0, "runMs": 2140}}
```

| State | Meaning | Recall |
|---|---|---|
| `accepted` | Durable, waiting for the writer. Survives a crash and runs after the next open. | Paused |
| `running` | The writer is working on it. | Paused |
| `applied` | Done and verified: every fact and preference the store links to the remembered message is live, owned by the owner and embedded with the store's dimensions. `searchable` is true when there is at least one; an applied write with none (`reason: "nothing_to_keep"`) stored nothing to recall. Only this state supports saying "remembered". | Open |
| `rejected` | It failed, and the store shows nothing recall reads came from it; `reason` says why (`model_unavailable`, `model_gave_no_usable_reply`, …). Submit again under a new `sourceId` to try again. | Open |
| `partial` | Some of it was written, but not everything succeeded or verified. | Paused until resolved |
| `unknown` | The store failed while writing (a transaction may still commit), the evidence could not be read, or the process ended while it ran. It is never run again. | Paused until resolved |
| `kept` | A partial or unknown write resolved by keeping what it wrote, as it is (not verified). | Open |
| `cancelled` | Forgotten (or erased) before it ran: it never wrote anything. | Open |
| `forgotten` | A remember whose memories a later `memory_forget` closed: recall no longer returns them. They are closed, not deleted. | Open |
| `erased` | The owner was erased since: what it wrote is deleted, and its words are gone from the journal. | Open |

Every receipt has `kind` (`remember`, `forget` or `erase`). `closed` lists the stored memories this operation closed: for a remember, those it replaced (the update judge confirmed each one); for a forget, those it stopped using. `targetOperationId` is the remember a forget names; `deleted` is how many nodes an erase deleted. `memory_operation_get` takes `operationId` or `sourceId` and returns the same receipt. `memory_operation_resolve` takes `operationId` and `"resolution": "keep"`; removing what a write produced arrives with the privacy controls.

While any write is accepted, running, partial or unknown, `memory_get_context` ends `unavailable` with `reads_paused`, and `am_v1_state` reports `READ_ALLOWED` off; `WRITE_QUIESCENT` is off while one is accepted or running. Closing never interrupts a running write: `am_v1_close` returns `AM_V1_BUSY` until it ends; writes still accepted run after the next open. The journal holds the remembered words (they are the evidence) and nothing is logged.

### Forgetting and erasing

| Operation | Request | What it does |
|---|---|---|
| `memory_forget` | `{"operationId": "op-…"}` or `{"memoryIds": ["…", …]}` (1-50 ids) | Stops using memories: closes every fact and preference one remember produced, or the owner's facts and preferences by id. They stay as history (as-of reads), never as current recall. Called **forgotten**, never deleted. A remember that has not started yet is cancelled instead (it never runs); a partial or unknown one can be forgotten, which also reopens recall. Ids that are no memory of the owner are listed in `reason` (`not_found: …`). |
| `memory_erase_owner` | `{"confirm": true}` | Deletes every node of the owner (memories, messages, conversations, traces), then rewrites the journal without any remembered words (ids, hashes and states stay, so an old source sent again comes back as its `erased` receipt and never runs). `applied` only when nothing of the owner is left in the store. Refused with `writes_pending` (nothing accepted) while any operation is accepted or running. Each erase that starts raises the deletion epoch of `am_v1_state`. |

Both are journal operations like a remember: accepted durably first, run by the one writer in order, recall paused until they end, receipts with the same states. Neither has a voice form in this library: the host decides which of its controls may ask for them.

## Learning (profile `learning`)

Everything `explicit_write` offers, with two differences. Every remember is applied once by its operation id: the
library's core writes the memories, closings, confirmations and a receipt (on the remembered message) in one store
transaction, so a write is in the store completely or not at all. That changes recovery and recall:

- A remember that was running when a process ended, or whose store call failed, is put back in the queue (journal
  record `retry`, with a pause that grows with each retry, at most 10) and runs again: its receipt in the store decides
  whether anything is left to do, so it is never applied twice. `unknown` remains only after 10 failed retries.
- Recall is not paused while a remember is pending: a read sees the store before or after one, never between. An
  uncertain remember (`partial`, `unknown`) and a pending forget or erase still pause it.

`memory_remember` also takes `"origin": "observed"` for words the host admits without the owner asking (automatic
learning); the receipt reports the origin. `explicit_write` refuses `observed` with `not_in_profile`.
