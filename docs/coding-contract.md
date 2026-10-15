# Coding host contract v1

`AM-CODING/1` names the existing shared coding-service contract. This document versions the
contract; it does **not** add a `version` field, new tool, header or handshake. Existing strict
clients and payload hashes remain compatible. Unknown operation fields are rejected. Future
optional capabilities must explicitly declare their availability and preserve this baseline
when disabled. MCP protocol negotiation is separate from this application contract.

The same `CodingApi` dispatches local HTTP operations and MCP tools. Native adapters translate
supported host events into these operations; they must not implement their own extraction,
retrieval routing or authorization. See [service setup](coding-service.md),
[tested client capabilities](coding-clients.md) and [remote authorization](coding-remote.md).

## Identity and provenance

The host authenticates an **owner and project**. Request text, repository paths, branch names,
commit assertions and tool arguments cannot select another owner or database. The configured
project maps to its pre-provisioned database; reads exclude other owners and shared memory.

The existing durable source identities are retained:

- Session: `coding-` plus lowercase SHA-256 of the default System.Text.Json UTF-8 serialization
  of `[ownerId, projectId, session]`.
- Source message: the same hash construction over `[normalizedSession, turnId, role]`.
- Payload conflict check: lowercase SHA-256 of the default System.Text.Json UTF-8 serialization
  of the `CodingRememberRequest` record, in its existing property order: `Session`, `TurnId`,
  `UserText`, `AssistantText` (including null), `Commit`, `Source`. This is an implementation
  compatibility format, not arbitrary client JSON canonicalization. Send the fields; clients
  do not supply a trusted hash.

Claude's existing session and prompt IDs are unchanged. Codex uses its own `turn_id` and
namespaces the external session as `codex:` plus SHA-256 of its raw UTF-8 session text before
service normalization. It must not substitute Claude's `prompt_id` field. Copilot and VS Code
use their existing profile/explicit-write paths; they do not synthesize automatic completion IDs.

The hook verifies the exact configured Git top-level and a full HEAD object ID. Its private
capture key includes service URL, configured project, repository root, session, turn and (for
non-Claude clients) harness. A linked worktree needs its own configured root/private state;
sharing a project deliberately shares project memory. Worktree separation is not tenant
isolation, and branch names never grant authority.

`commit` and `source` persisted by the service are **caller assertions**. The service never
opens a source path/URL and cannot prove that a patch, test, symbol or file range is correct.
The helper checks its repository locally; this is not a server-side signature or verification
of the client's claims. There is no first-class repository/worktree/revision-range object in
v1. Reserve that design for an opt-in metadata extension with new hash/version semantics;
do not silently alter existing payload hashes or add core schema labels for it.

## Operations and bounds

Local HTTP uses `POST /projects/{project}/{operation}` with the argument object. MCP exposes
the same operation names at `/projects/{project}/mcp`; remote mode exposes MCP only. Tools return
a JSON body in a text content block and set `isError` for failed operations. Authentication and
request-size errors can occur before tool dispatch. Authenticated stateless MCP GET/DELETE
return 405 with `Allow: POST`.

| Operation | Arguments | Result / constraint |
|---|---|---|
| `recall` | `session` (1..256), `query` (1..8000) | Selected context, total items, truncation and estimated tokens; current owner/project scope. |
| `remember` | `session`, `turnId` (1..256 each), `userText` (1..8000), optional nonblank `assistantText` (1..8000), `commit` (40 or 64 hex characters), `source` (1..512) | 202 job receipt, not a completed memory write. Requires write permission. |
| `profile` | `{}` | Current owner/project working-memory object or null. |
| `write_status` | `jobId` (1..128) | Retained receipt for the authenticated owner/project, or 404. |
| `history` | optional `id` (1..256) | At most 50 records, including invalidated history, within scope. |
| `forget` | `kind` (`entity`, `fact`, `preference`), `id` (1..256) | Non-destructive invalidation result. Requires write permission. |

Operation arguments are case-sensitive; unknown fields and invalid enum spellings/numeric enums
are rejected. Text must be nonblank. Source references cannot contain control characters or URLs.
Transport bodies are bounded at 64 KiB; the content policy rejects recognized/configured secrets,
bulk text and transcript patterns. These filters are heuristic, not a confidentiality guarantee.
The hook's injected memory budget is independently 6,000 characters, complete items only, labelled
untrusted. It does not inject verbatim message windows. Working memory in `recall.context` is a
string; SessionStart's `profile` is an object with `text`.

## Native lifecycle normalization

These are semantic states over existing adapter events, not new fields sent by every host.

| Event / outcome | Adapter behavior | Memory write? |
|---|---|---|
| Session start | Read profile after repository/configuration checks. | No. |
| Prompt with supported correlation ID | Save bounded user text privately, then recall. | No; capture is pending. |
| Prompt without supported correlation ID | Recall and report that pairing is unavailable. | No automatic write. |
| Matching final Stop with nonblank final assistant text | Pair saved user text and final assistant text; submit once. | Queued, until status proves completion. |
| Stop without final assistant text | Discard matching pending capture and report incomplete completion. | No; do not downgrade to a deliberate user-only write. |
| Claude StopFailure / Codex Interrupt | Discard matching pending capture. | No. |
| Recursive stop hook, subagent or unsupported event | Ignore; do not infer completion from a transcript. | No. |
| Explicit `remember` tool/HTTP request | Validate a deliberate turn/digest; assistant text is optional. | Queued, with the same host write permission and pipeline. |

A final reply is a completion signal, **not a verified successful coding outcome**. Failed test
results may be relevant memories when reported in a valid completed conversation; the helper does
not classify success by scanning assistant prose. Host error/interruption events are separately
non-capture paths. Automatic writes are limited to documented, actually tested event shapes.

Pending hook records have a 24-hour submission lifetime and bounded capacity (128). Later capture
activity prunes expired records; there is no deletion timer. Successful 202 acknowledgement replaces
the local pending text with a tombstone. An unavailable service fails open for the coding client and
claims no completed write. A client that never emits its terminal error event can leave a pending
record until cleanup; see the actual Claude limitation in the compatibility page.

## Write receipts and recovery

Baseline v1 uses a bounded **in-process** queue (64 waiting entries, 512 retained jobs by default).
Within retention, the same scoped turn and same payload return the same receipt; changed content
or provenance produces `turn_conflict`. Other owner/project receipts are not visible.

| State | Meaning |
|---|---|
| `queued` / `running` | Accepted or executing; no completed ingestion claim. |
| `succeeded` | Pipeline reported success. It can have extracted no durable facts. |
| `partial` / `failed` | Pipeline reported partial completion/failure. Inspect the stable error code. |
| `unknown` | A prior attempt or interrupted write cannot be established as complete. |
| `cancelled` | Shutdown prevented queued work from starting. |

`extractionPath` reports the observed pipeline path independently of state. Baseline receipts
are lost on restart/eviction. A persisted source after restart proves an attempt began; it does
not prove extraction committed. The writer returns Unknown instead of replaying extraction.
The optional `writeJournal` capability adds a `durability` receipt field (`journal` or `uncertain`)
and conditionally describes retention/recovery in the existing tools. Off preserves the v1 wire.
[Journal configuration](coding-service.md#optional-durable-write-receipts) defines its bounded
recovery and failure contract. Neither path makes an exactly-once extraction guarantee.

## Optional recall observations

The off-by-default `recallDiagnostics` capability adds `recallId` only to successful recall responses,
plus `recall_status` and write-scoped `recall_feedback`. The v1 context/identity contract is unchanged.
Receipts are owner/project/session-scoped, bounded and expiring process memory; restart loses them.
Feedback is an explicit observation, not a graph write or automatic learning. See
[configuration, wire fields and limits](coding-service.md#optional-recall-diagnostics-and-feedback).

## Optional evidence delivery

The off-by-default `evidence` capability adds read-scoped `recall_preview` and `recall_expand`.
It preserves ordinary recall, native injection and the existing retrieval/gate policy. Expiring
owner/project/session handles retain selected IDs only; expansion rereads current ownership,
liveness and parent/source association. Unselected IDs cannot authorize reads. Evidence is untrusted;
source/commit assertions remain caller-provided, never verified code. Count, serialized JSON-body
byte and deadline limits are explicit. See [wire details and limits](coding-service.md#optional-evidence-preview-and-expansion).

## Errors and readiness

The shared dispatcher reports `invalid_input` (400), `insufficient_scope` (403),
`unknown_operation` / `job_not_found` / optional `recall_not_found` / `preview_not_found` (404), `turn_conflict` (409), `queue_unavailable` or
`memory_unavailable` (503), and optional `evidence_timeout` (504). Transport authorization, size and method errors are separate. Hook
failure messages omit configuration, raw provider exceptions and content. Do not log tokens or
full captured text to diagnose availability.

There is no anonymous health/doctor endpoint. The existing host startup checks every configured
project's schema and vector dimensions before listening; failure prevents startup. An authenticated
`profile {}` request checks that the scoped service can read, not that extraction or model calls
will succeed. A bounded synthetic remember/status/recall test is the separate write-path check.
Never infer write readiness from MCP discovery or a model replying successfully.

## Conformance and change policy

The current executable fixtures cover both local HTTP and MCP dispatch, wrong scope, strict
arguments, duplicate/conflict handling, completion pairing, incomplete/failure/interrupted events,
legacy pending-file compatibility, context budgets and decoded secrets. See the `Coding*`,
`ClaudeCodingHookTests` and `CodexCodingHookTests` suites under `tests/AgentMemory.Tests.Unit/McpServer`.
Actual client/provider/storage results and their limits are linked from the capability matrix;
fixtures do not replace those acceptance runs.

Compatible fixes enforce the documented intent without changing source IDs or existing hash bytes.
An incompatible identity, field or persistence interpretation needs a new application-contract
version and migration. Durable receipts, diagnostics and evidence expansion must be explicit
capabilities, tested both on and off, and must not imply that every client supports every feature.
