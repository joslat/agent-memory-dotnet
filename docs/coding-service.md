# Local coding memory service

The opt-in coding mode gives native coding clients one project-bound HTTP/MCP service. Run it with
`agent-memory-mcp --coding-config <private.json>` (or the equivalent `dotnet run --project
tools/AgentMemory.McpHost -- --coding-config <private.json>`). The ordinary MCP host is unchanged.
This mode is built and wired. A native Claude hook client and templates are available in
[the coding sample](../samples/CodingMemory/README.md), with an optional [local memory page](#local-memory-page).
[Optional remote HTTPS/OAuth](coding-remote.md) is implemented with a separate token contract;
external identity-provider setup and deployment remain separate work. Local tests cover transport, page interactions and
persistence, not model quality or a real client workflow.
Copilot CLI and VS Code Local have separate [MCP and hook templates](../samples/CodingMemory/copilot.md).
Both support session-start profile reads; Copilot additionally supports explicit transformed-prompt
recall. Automatic completion writes and actual-client validation remain unfinished.
Codex has [paired hooks and a disabled MCP template](../samples/CodingMemory/codex.md); the memory
service's provider setting does not configure Codex's model connection.

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
Recommended memory/writer preset and disables extractor fallback. Legacy memory-tuning environment
settings do not customize that preset. The mode clears ambient ASP.NET configuration and binds only the
configured IPv4 loopback address, with at most 64 concurrent connections. Do not expose it through a proxy.

### Why extractor fallback is disabled

This is a coding-host policy, not a limitation of the memory library. The library can fall back
from a failed store-aware writer to extractors when `FallBackToExtractorsWhenWriterFails` is enabled.
That changes how memories are selected and persisted: extractors do not make the writer's
store-aware add/replace/correct/confirm decisions.

The coding job receipt currently reports ingestion status, not the extraction tier that produced
it. Turning fallback on would therefore let an extractor-written turn look like ordinary completion
under the host's writer-only policy. The host sets fallback off and the ingestion adapter checks it
before creating new source records, including when the adapter is composed outside the standard host.
Legacy environment settings cannot override it, and there is currently no coding-config switch for it.

Keep this policy unless the host explicitly adds configurable fallback with a per-job indication
that extractors were used and failure-path tests covering provenance, outcomes and duplicate turns.
Enabling a core option or removing the adapter guard alone is insufficient. A failed attempt can
leave source messages; an existing source without a retained receipt remains unknown and is not
automatically replayed. `succeeded` reports completed ingestion, not proof that a new memory was stored.

This restriction does **not** disable the host-model **closing-judge** fallback. When the configured
update judge is absent or fails, the Recommended writer can still ask its chat model to judge its
named closings. A negative closing verdict is not a failure to bypass with another judge.

## Optional retrieval and update judges

Add a `judges` array to the configuration to enable the existing gate. Without it, recall uses the
normal retrieval path and the writer's closing fallback uses the configured chat model. For example:

```json
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

The in-process queue has one worker, capacity 64 and at most 512 retained jobs. Overflow returns 503.
Job states distinguish queued/running, succeeded, partial, failed, unknown and cancelled. After eviction
or restart, an existing source means prior-attempt-unknown and is not automatically re-extracted.
Partial source writes can remain after failures. There are no durable jobs, exactly-once guarantee or
multi-instance coordination. Shutdown cancels waiting jobs and marks interrupted writes unknown.

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
and authorization, and a synthetic Chrome check covers page interactions; this is not live model-quality
validation or remote deployment support.
