# Local coding memory service

The opt-in coding mode gives native coding clients one project-bound HTTP/MCP service. Run it with
`agent-memory-mcp --coding-config <private.json>` (or the equivalent `dotnet run --project
tools/AgentMemory.McpHost -- --coding-config <private.json>`). The ordinary MCP host is unchanged.
This mode is built and wired. A native Claude hook client and templates are available in
[the coding sample](../samples/CodingMemory/README.md), with an optional [local memory page](#local-memory-page).
[Optional remote HTTPS/OAuth](coding-remote.md) is implemented with a separate token contract;
external identity-provider setup and deployment remain separate work. Local tests cover transport, page interactions and
persistence. The [client compatibility matrix](coding-clients.md) separates actual CLI protocol checks
from model quality and database-backed client acceptance.
Copilot CLI and VS Code Local have separate [MCP and hook templates](../samples/CodingMemory/copilot.md).
Both support session-start profile reads; Copilot additionally supports explicit transformed-prompt
recall. Their automatic completion writes remain unsupported by the required paired-event contract;
use explicit MCP writes. Actual editor and remote-connector acceptance remain separate.
Codex has [paired hooks and a disabled MCP template](../samples/CodingMemory/codex.md); the memory
service's provider setting does not configure Codex's model connection.

The coding MCP transport is stateless: clients send operations with POST. Authenticated
GET stream probes and DELETE session requests return **405 Method Not Allowed** with
`Allow: POST`; they do not start an OAuth flow. Invalid project credentials still fail
authentication, and origin/host checks still apply. The same method behavior applies to
the optional remote transport after its OAuth authorization succeeds.

The [versioned host contract](coding-contract.md) specifies shared wire shapes, identities,
native completion rules, bounded receipts and recovery limitations.

## Configuration

Keep the configuration outside source control. It is a strict UTF-8 JSON object, at most 16 KiB;
unknown or duplicate fields are rejected. Secrets are environment-variable references, never JSON values.

```json
{
  "ownerId": "your-owner-id",
  "url": "http://127.0.0.1:5234",
  "projects": {
    "my-project": "MY_PROJECT_MEMORY_TOKEN"
  }
}
```

Set `MY_PROJECT_MEMORY_TOKEN` to a unique cryptographically random token of 32–256 printable ASCII
characters, without spaces. Each configured project needs a distinct token. Project names are lowercase
letters/digits separated by single hyphens, start with a letter and have at most 32 characters. Up to
32 projects share the configured owner. Treat tokens as credentials with write and soft-forget access.

Set `NEO4J_URI`, `NEO4J_USERNAME` and `NEO4J_PASSWORD`. An administrator must create each
`coding-<project>` database beforehand on a Neo4j deployment supporting multiple databases. This mode
does not provision databases or isolate projects within a Community single database. Startup bootstraps
the schema and checks vector dimensions in each project before listening, with a two-minute total
startup deadline. The database credentials need those schema permissions. A failed project prevents
startup; migrations already applied to an earlier project are not rolled back.

This initial mode supports explicitly selected Bitdeer or local Ollama inference:

- Set `AI_INFERENCE_PROVIDER=bitdeer` and `BITDEER_API_KEY`. The shared resolver supplies its model and
  embedding defaults; `BITDEER_MODEL` / `AGENTMEMORY_EXTRACTION_MODEL` can select models. The endpoint must be
  `https://api-inference.bitdeer.ai/v1`.
- Or set `AI_INFERENCE_PROVIDER=ollama` and `OLLAMA_MODEL`, with a running local server. Its endpoint
  must be `http://127.0.0.1:<port>/v1` (default port 11434).
- Embeddings may independently select either supported provider with `AI_EMBEDDING_PROVIDER` and
  the shared `AI_EMBEDDING_*` settings. Supply `AI_EMBEDDING_DIMENSIONS` for unknown models; it must match
  the existing store. Remote endpoint overrides, URL credentials and query strings are rejected.

Provider auto-detection is rejected in coding mode. Legacy host flags cannot be combined with
`--coding-config`; legacy read-only, graph-query and no-bootstrap environment settings are rejected
when enabled. Raw inference logging and chat-judge overrides are rejected too. Coding mode uses the
Recommended memory/writer preset; extractor fallback is off unless explicitly enabled. Legacy memory-tuning environment
settings do not customize that preset. The mode clears ambient ASP.NET configuration and binds only the
configured IPv4 loopback address, with at most 64 concurrent connections. Do not expose it through a proxy.

### Optional durable write receipts

The default queue is in-process. To retain accepted work and receipts across host restarts,
explicitly add a dedicated private local directory to the coding JSON:

```json
"writeJournal": {
  "directory": "C:\\private-data\\agent-memory\\coding-journal",
  "retainedJobs": 512
}
```

Linux example: `/var/lib/agent-memory/journal`. Omit `writeJournal` to retain baseline behavior:
no journal service or file I/O, unchanged baseline tool descriptions and no extra receipt field.
This uses embedded SQLite in the coding host only; it adds no server or core library dependency.
Disabling the option leaves existing journal files intact and does not recover their queued work.
The service must own the directory. Newly created directories are private (Unix 0700; Windows
current service account, SYSTEM and administrators). Permissive existing directories are
rejected; Windows also checks existing journal-file ACLs. Unix directory access protects its files,
and the database is set to mode 0600. Links and Windows UNC paths are rejected.
Use a local filesystem, not a network share or a synchronized folder. The journal is bound to the
configured owner and Neo4j URI; reusing it for another deployment fails startup.

For a container with a read-only root filesystem, provide a writable private mount whose root
belongs to the image's service UID. Provision that mount once, outside the running memory process;
do not run the service as root or grant it filesystem-management capabilities. Mounting an empty
root-owned volume over a service path does not grant the non-root service write access.

With the option on:

- `remember` returns `durability: "journal"` only after the request and receipt commit locally.
  It still returns 202, and acceptance still does not mean extraction succeeded.
- The worker commits Running before calling the existing writer. Recovery resumes only entries
  still Queued. A previously Running entry becomes Unknown (`restart_during_write`) and is never
  automatically re-extracted. Persisted sources alone do not prove extraction completed.
- Terminal receipts, duplicate IDs and payload conflicts survive restart within retention. Original
  source IDs and payload hashes are unchanged. A duplicate can return its existing terminal result.
- Queued recovery checks the current owner/project, server write permission and content policy.
  Removed projects or disabled remote writes cancel that queued work (`recovery_not_authorized`).
  Tokens authorize acceptance; their later expiry/revocation does not cancel already accepted work.
- An unavailable journal stops admission/execution. An uncertain state transition reports Unknown,
  `journal_unavailable` and `durability: "uncertain"`; restart after repairing storage to recover.
  A failed acceptance may have committed before the caller saw the failure; retry the identical
  turn ID and payload, never create a new ID merely because the response was lost.
- Orderly shutdown retains never-started work for the next host; interrupted active work becomes
  Unknown. The baseline queue instead cancels waiting work on shutdown.

There is one worker and 64 waiting slots. Retention is 64..4096 receipts (default 512); oldest
terminal receipts are evicted when new admission needs space. Active work is never evicted.
Receipt availability and duplicate recognition end at eviction; graph history remains intact.
Conversation text is cleared from terminal journal entries, while hashes, scope and results remain.
SQLite secure-delete is enabled; this is not encryption or a promise to erase backups/filesystem
snapshots. Keep the directory out of source control and use appropriate private storage.

The database is capped at 64 MiB, entries at 128 KiB, and retained row count at the configured limit.
The rollback journal is transient additional disk use. Integrity/version/owner mismatches fail closed
without deleting evidence. One host holds the journal's exclusive process lease; multiple independent
journals aimed at the same graph are **not** a supported multi-writer coordination scheme.

The journal uses SQLite DELETE journaling and EXTRA synchronization. The graph and journal are
separate transactions: this does not provide exactly-once extraction. Process restart checks cannot
prove sudden-power-loss behavior on every filesystem/device. See
[SQLite commit guarantees](https://www.sqlite.org/atomiccommit.html) and
[synchronization settings](https://www.sqlite.org/pragma.html#pragma_synchronous).

A bounded real Linux-container acceptance used Qwen/Qwen3.8-27B through Bitdeer, local bge-m3,
Neo4j and Keycloak. All 27 checks passed, including forced host-process termination, fresh-session
retrieval after recovery, duplicate/conflict retention and write opt-out before recovery. The
interrupted writer was held under a real graph lock before source creation. Two separate GLM-5.3-Flash
writer runs exceeded 120/240-second observation windows; they remain failures. No default model,
prompt or semantic threshold changed, and this is not a quality benchmark or power-loss test.

### Optional recall diagnostics and feedback

Add `"recallDiagnostics": { "retainedReceipts": 256, "lifetimeSeconds": 600 }` to the coding
configuration to enable bounded recall observations. Omit it for unchanged baseline response/tool
bytes, no receipt service or feedback operations, and no additional retrieval diagnostics request.
This option is independent of `writeJournal`; recall receipts live only in this host process.

Successful `recall` adds `recallId` alongside the existing response. The selected context is unchanged.
`recall_status { "session": "s", "recallId": "..." }` reads the corresponding receipt; omit the ID for
that session's latest retained receipt. Both operations use the authenticated owner/project and the
same validated client session. A wrong scope, evicted/expired ID or host restart returns the same
`recall_not_found` (404). A session name is a client correlation key, not a separate security principal:
clients authorized as the same owner/project can share its receipts if they know the session.

A receipt records elapsed recall milliseconds, the engine's token estimate (not billing), token
truncation and latency-budget exhaustion separately, selected IDs/provenance, and available section
search/limit/return/timeout measurements. Missing measurements stay null. Gate outcome is the actual
measured `judge`, `floor`, `everything`, `floor (fallback)`, or `unknown` when no recognized trace is
available. Provider fallback error text is never retained: the reason is `not_configured` only for the
known missing-judge case, otherwise `unspecified`. An empty result alone does not explain its cause.

There are at most 64 item IDs and four source-message IDs per item, each at most 256 characters;
`idsTruncated` reports omission. Sections follow the item lists delivered by the coding recall response.
Working memory and GraphRAG are presence flags; forgotten-topic summaries are not resurrected as item
IDs. Internal candidates/relationships not delivered by that response are not presented as delivered
items. A receipt is an observation at recall time, not fresh evidence or authorization to expand content.
It retains no query, recalled prose, embedding, arbitrary metadata or provider exception text.

`recall_feedback { "session": "s", "recallId": "...", "label": "useful" }` requires write permission.
Labels are `useful`, `irrelevant`, `stale`, or `incorrect`; no free-text feedback is accepted. One current
label is retained per receipt; identical submissions are idempotent, and changes do not extend expiry.
Feedback is not a memory correction, model training, threshold update or correctness certification.
Use the existing governed memory operations to correct or close a memory.

Retention is 1..1024 receipts (default 256), with a 1..3600-second lifetime (default 600). Admission,
lookup and feedback remove expired entries; capacity evicts the oldest admission. Expired observations
are never returned, although bounded process memory may hold them until the next receipt operation.
A restart clears all observations and feedback. Optional `coding.recall.receipt` and
`coding.recall.feedback` activities use the existing `AgentMemory` ActivitySource with generated
receipt IDs, bounded labels and measured counts/timing; no conversation or memory text is tagged.

A bounded real Linux-host run with Keycloak PKCE/OAuth, Neo4j and Ollama bge-m3 passed 24/24 checks:
selected IDs matched delivered context, session/project scope held, feedback required write permission,
and eviction/expiry worked. This reused synthetic stored memories and exercised no new writer or
judge call. It proves this delivery path, not better retrieval quality or model calibration.

### Configurable retrieval

Configured judges are used by default (`"useRetrievalGate": true`). Set it to `false` to
use similarity-floor retrieval without retrieval judge calls. This leaves the separately
configured `closingJudgeName` active for storage. With no configured judges, recall uses
the existing core path. A failed judge leaves the remaining answering judges, or the floor
if none answer; this does not discover or enable another endpoint.

Recommended now disables automatic query splitting. Set `"automaticQuerySplitting": true`
to opt back in. This controls the core's automatic subquery searches, independently of the
gate and of the optional compound-question experiment (which coding mode leaves off).
The general MCP host's `AgentMemory:RetrievalRouter` settings do not override coding JSON.

### Configurable extractor fallback

Set `"allowExtractorFallback": true` in the coding-service JSON to permit configured extractors
when the store-aware writer fails. The default is false. Legacy environment settings cannot enable
it, and alternate DI compositions must also carry explicit host permission. An enabled writer is
still required before source messages are created; this setting handles a failed writer call, not
an absent writer registration. Extractors can need the same inference provider and may also fail.

Every `remember`/`write_status` receipt includes `extractionPath`, independently of `state`:

| Path | Meaning |
|---|---|
| `unknown` | No completed result, an unknown prior attempt, or a custom pipeline that did not report a path. |
| `skipped` | The turn needed no extraction, such as a greeting skipped by the novelty gate. |
| `writer` | The store-aware writer was attempted without extractor fallback. |
| `writerFallback` | The writer failed and extractors were attempted. Check `state` for success, partial completion or failure. |
| `extractors` / `preExtracted` | Library import/extractor paths; the standard coding host always requests a conversation turn. |

The path comes from the actual extraction result, never from the preferred configuration. Provider
error text is not copied into this receipt. Extractors do not make the writer's store-aware
add/replace/correct/confirm decisions, so a successful fallback remains visibly different.

Retained duplicates return the same job and path. After restart/eviction, a stored source without
a retained receipt remains unknown and is not automatically replayed. Cancellation does not trigger
fallback; interrupted jobs remain unknown. `succeeded` means ingestion completed, not that a new
memory was stored or that the model's interpretation was correct.

Host-model **closing-judge** fallback is separate and remains enabled by the Recommended preset.
When the configured update judge is absent or fails, the writer can ask its chat model to judge
named closings. A negative closing verdict is not an outage to bypass with another judge.

## Optional retrieval and update judges

Add a `judges` array to enable the retrieval gate. Set `"closingJudgeName": "jev"` alongside it to
permit that specific endpoint to judge storage closings, regardless of array order. Omitting or setting
the name to null uses the host-model closing judge only; it does not disable retrieval. Empty or unknown
names are rejected. Retrieval training alone does not qualify a model to judge storage updates.

**Migration:** earlier coding-host versions used the first retrieval endpoint for closing decisions.
Add its exact name as `closingJudgeName` to retain that external judge. The general library preserves
legacy selection unless `MemoryGateOptions.UpdateJudgeName` is explicitly set. For example:

```json
"closingJudgeName": "jev",
"judges": [
  {
    "name": "jev",
    "endpoint": "https://api.typesafe.ai/v1/systemone",
    "model": "jev-latest",
    "keyVariable": "JEV_API_KEY",
    "weight": 0.8
  },
  {
    "name": "laya",
    "endpoint": "http://127.0.0.1:8765/v1/systemone",
    "model": "laya",
    "weight": 0.2
  }
]
```

The remote endpoint requires its populated key variable; a local System One server can omit one.
Configure only servers you have running. There are at most four judges, with unique names and positive
finite weights. The only supported remote judge endpoint is the TypeSafe HTTPS endpoint above; local
judges use IPv4 loopback HTTP and `/v1/systemone`. Compound-question retrieval remains disabled.

## Requests

Send `Authorization: Bearer <project-token>`. MCP uses `/projects/<project>/mcp`; HTTP uses POST to
`/projects/<project>/<operation>` with a JSON object. Both expose the same operations and reject
unrecognized arguments. Owner, database and project are bound by the route and credential, not tool input.

| Operation | Arguments | Result |
|---|---|---|
| `recall` | `session`, `query` | Scoped, embedding-free memory context |
| `remember` | `session`, `turnId`, `userText`, optional `assistantText`, `commit`, `source` | HTTP 202 / a queued job; acceptance is not completion |
| `write_status` | `jobId` | Retained scoped status; 404 means no known job, not success |
| `history` | Optional memory `id` | Up to 50 records including invalidated memories |
| `profile` | None | Current owner's profile in this project |
| `forget` | `kind` (`entity`, `fact`, `preference`), `id` | Non-destructive invalidation result |

`commit` must be a full 40- or 64-character hexadecimal commit ID. `source` is a short reference,
not a file to upload or read; references are caller assertions. A session is project/owner scoped.
Reuse a turn ID only for the identical payload: a changed retained payload returns 409. Ingestion
learns from the user target; an optional assistant reply is stored for later context.

## Limits and failure behavior

Bodies are capped at 64 KiB, including chunked requests. Text fields are bounded to at most 8,000
characters; multiline/bulk and transcript-shaped inputs can be rejected. Browser origins are refused
by default. The optional local page below permits same-origin HTTP requests; MCP stays native-only.
This mode is not a remote MCP connector.

Input checks reject recognizable secret formats and configured credential substrings before admission,
recall or source persistence, including selected prior conversation text. Authentication stores token
hashes; the content checker retains credential values privately in process memory for matching.
These heuristics cannot prove arbitrary prose contains no secrets, audit old graph data, or filter
model-generated output. Select and redact client input accordingly. Provider calls send the text needed
by the writer, embeddings and configured judges to those providers. Building or configuring this mode
does not authorize sending sensitive data to a provider.

The default in-process queue has one worker, capacity 64 and at most 512 retained jobs. Overflow returns 503.
Job states distinguish queued/running, succeeded, partial, failed, unknown and cancelled. After eviction
or restart, an existing source means prior-attempt-unknown and is not automatically re-extracted.
Partial source writes can remain after failures. The baseline offers no durable jobs, exactly-once
guarantee or multi-instance coordination. Shutdown cancels waiting jobs and marks interrupted writes
unknown. The opt-in journal above adds bounded durable receipts and conservative recovery; it does
not provide exactly-once extraction or distributed coordination.

Failures return stable codes, without exception payloads. Coding mode suppresses framework/provider
log providers and emits only generic startup diagnostics to stderr; request text is not an operational
debug log. The service is one local process; monitoring, client activation and remote deployment need
their own integration work.

## Local memory page

Set `"enableBrowserUi": true` in the private coding-service configuration and restart the service.
Open `http://127.0.0.1:5234/projects/<project>/ui` (use your configured port). Paste that project's
token to connect. The static page contains no memories or credentials; every data operation requires
the bearer token. The page keeps it in JavaScript memory, without cookies or browser storage, and
clears it and displayed data on disconnect or navigation away.

The page shows the owner's working profile and up to 50 history records, including closed records,
sources and supersession links. Look up a known memory ID to narrow the view. It is not a full export.
**Close memory** uses the scoped non-destructive forget operation. **Correct** submits a sourced user
turn through the existing writer, with a full Git commit ID you supply; it does not directly overwrite
the selected record or guarantee that the writer accepts the correction. Submitting a correction
uses the configured model providers; viewing history/profile and closing do not invoke inference.

Check the correction's retained job status, then refresh history. The page distinguishes acceptance,
success, partial completion, failure, unknown completion and cancellation. A missing status is not
success. Repeating the last identical correction text/commit/target in the same connected tab reuses its turn
ID, including after an ambiguous network failure; this is not durable retry protection across tabs or
restarts. The page has a 15-second request timeout and never submits automatically.

This option defaults to false: no page routes are registered, and all browser origins remain rejected.
When enabled, only exact same-origin HTTP requests are allowed, still subject to project bearer
authorization and loopback Host checks. Cross-origin requests and browser MCP calls remain rejected.
Assets are embedded in the tool, with no CDN or repository file serving. A restrictive content security
policy blocks framing and inline scripts; stored content is rendered as text. Local tests cover routing
and authorization; synthetic browser checks cover page interactions. A separate actual headless
Chrome 155.0.8059.40 run passed 13/13 bounded checks with the real host, Bitdeer writer, local
embeddings and disposable Neo4j: profile/history, correction and completed job status, retained
closed history, wrong-project rejection, isolation and disconnect. The initial synthetic fact was
seeded through HTTP; correction and closing came from the page controls. This is wiring acceptance,
not a model-quality benchmark or remote deployment support.
