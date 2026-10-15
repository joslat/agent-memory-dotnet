# Coding client compatibility

The coding service and a coding client are separate integrations. A successful model reply does not
prove that the client ran memory hooks or connected its MCP tools. Check both paths before using
project memory. These observations were checked on 13 October 2026.

| Client | Implemented hook path | Writes | Actual-client evidence |
|---|---|---|---|
| Claude Code 2.1.296 | Session profile; prompt capture and recall; matching completion; cleanup when failure event arrives | Paired `prompt_id` hooks, or explicit MCP | 7/7 protocol checks; 17/17 real Bitdeer/Neo4j paired-hook checks. Terminal-error cleanup has the limitation below. |
| Codex CLI 0.160.1 | Session profile; prompt capture and recall; matching completion; interruption cleanup | Paired `turn_id` hooks, or explicit MCP | 7/7 protocol checks; 17/17 real Bitdeer/Neo4j paired-hook checks. App-server 0.162.1 interruption passed 8/8 local-fixture checks; terminal Ctrl+C was not tested. |
| Copilot CLI 1.0.96 | Session profile and native transformed-prompt recall | Explicit MCP | 7/7 protocol checks, plus a real Bitdeer/MCP/Neo4j run with 18/18 bounded checks. |
| VS Code Local 1.142.0-insider | Session profile | Explicit MCP | Actual editor: 6/6 protocol checks; real Bitdeer/Neo4j write, new-chat profile and explicit recall verified. See scope and limits below. |

The shared [host contract v1](coding-contract.md) separates independent capabilities:

| Tested client | MCP tools | Automatic recall | Automatic completed-turn capture | Durable queue acknowledgement | Governed storage |
|---|---|---|---|---|---|
| Claude Code 2.1.296 | Yes | Session profile + prompt | Paired hooks; terminal-error caveat below | Opt-in host journal | Host writer, scope and history |
| Codex CLI 0.160.1 | Yes | Session profile + prompt | Paired hooks; interruption checked separately on app-server 0.162.1 | Opt-in host journal | Host writer, scope and history |
| Copilot CLI 1.0.96 | Yes | Session profile + opt-in transformed prompt | No; explicit writes | Opt-in host journal | Host writer, scope and history |
| VS Code Local 1.142.0-insider | Yes | Session profile only | No; explicit writes | Opt-in host journal | Host writer, scope and history |
| Claude web connector | Not validated | Not validated | Not validated | Not validated | Requires separate cloud acceptance |

These columns describe this integration at the tested client versions. The new opt-in host journal
is shared by HTTP and MCP; the earlier client runs used the non-durable baseline. Its dedicated
crash/restart acceptance is separate from native-client activation. Durable graph records alone do
not make an in-memory queue acknowledgement durable. Governed storage does not verify the truth of
assistant claims. No single maturity badge substitutes for these distinctions. Runtime/live host
checks were performed on Windows; Linux container service checks do not prove Linux/macOS client
hook compatibility. The existing packaged .NET host contains the helper; no separate npm service
is required.

The protocol checks use canned model replies and synthetic HTTP memory responses. They establish
hook invocation, context delivery and the applicable write/recall envelope, **not** model quality,
durable ingestion, interruption behavior in every client, or a complete database-backed workflow.
The separate unit suites cover interruption, duplicate handling, malformed input and failures.

The optional host `recallDiagnostics` capability adds scoped `recall_status` and write-permission
`recall_feedback` tools. Native hooks can ignore the additive recall ID; context injection is unchanged.
A separate real OAuth/MCP host run passed 24/24 receipt, scope, permission, eviction and expiry checks.
This does not claim that every native client automatically displays receipts or submits feedback;
those remain explicit tool calls. [Configuration and limits](coding-service.md#optional-recall-diagnostics-and-feedback).

## Real Copilot acceptance

Copilot 1.0.96 used Bitdeer GLM-5.3-Flash through its custom-provider connection, the actual coding
host, local bge-m3 embeddings and two disposable Neo4j Enterprise databases. Four fresh client
sessions exercised two explicit MCP writes (initial fact and correction), hook-based recall, and a
different-project control. The fresh reply used the corrected fact; the control replied UNKNOWN.
HTTP job-status, history and forget checks plus direct graph inspection confirmed successful writer
completion, retained closed history and non-destructive forgetting. Eighteen checks passed in 67.45 seconds.

This was one synthetic example, with no retrieval judges configured, so it exercised the core retrieval
path and Recommended writer/host closing judge. It is not a quality benchmark or evidence for every
provider, outage, interruption, duplicate or editor workflow. The history and forget operations in this
run came from the test harness through HTTP, not model-selected MCP calls. No personal client setup
or existing database was changed.

Additional real Copilot sessions submitted an identical turn twice and then changed the payload
under that same turn ID. The duplicate returned the same completed job; the changed payload
received `turn_conflict`. Stored content and relationships remained unchanged. Whole-graph equality
failed because the client hooks performed recall, updating access counters and creating
read-audit records. Those original failed assertions remain in the evidence.

With the service stopped, both hooks reported unavailability and the client still answered the
synthetic prompt. MCP tools were disconnected for this case: it establishes hook fail-open behavior,
not resilience of an unavailable MCP connection at client startup.

## Real Claude and Codex paired capture

Each client passed 17/17 bounded checks using real Bitdeer GLM-5.3-Flash, the coding host,
Ollama embeddings and disposable Neo4j. A completion hook queued the captured user/assistant
pair automatically, its job completed, and the fact persisted. A fresh session retrieved it;
a different project answered UNKNOWN. Each run used three fresh sessions, isolated client
settings and no explicit rescue writes. These runs used the current post-build host binary.

For routing, Bitdeer documents both an Anthropic-compatible connection and a Responses API
connection. Configure these in an isolated client profile; do not assume a Chat Completions URL
alone supports either protocol. [Claude connection](https://developers.bitdeer.ai/docs/guides/integrations/claude-code-cli),
[Codex connection](https://developers.bitdeer.ai/docs/guides/integrations/codex).

### Terminal-error limitation

In a separate local protocol fixture, Claude 2.1.296 in restricted print mode received a terminal
HTTP 400 error. SessionStart and UserPromptSubmit fired, but neither Stop nor StopFailure arrived.
No memory write occurred; the pending user capture remained. Therefore immediate failure cleanup
is not established for that invocation, despite the documented StopFailure contract. This is a
client/configuration observation, not a real-provider outage result.

The helper discards captures when a matching failure event arrives. Otherwise its 24-hour lifetime
prevents expired captures from being submitted, and later capture activity prunes expired entries;
there is no background timer that guarantees deletion at 24 hours. Do not treat pending captures
as completed writes or describe cleanup as unconditional.

## Real VS Code Local acceptance

An isolated official VS Code Insiders archive (1.142.0-insider, commit
`e9eaf94e8bb01badde47d2518e16d3e1cdab1dd9`) ran the actual Local chat UI and memory helper.
Six protocol-fixture checks passed, establishing SessionStart, authenticated profile delivery
and absence of automatic capture. Provider-hosted chat modes were not tested.

A separate live run used Bitdeer GLM-5.3-Flash, the actual coding host, local bge-m3 and
disposable Neo4j. The model called MCP `remember` once; its queued job completed through
the writer and the amber release-channel fact persisted. A new chat called MCP `recall`
once and answered amber. The other project's database had no facts. No HTTP rescue write
was used; the harness only read status/graph evidence. Five service/storage checks passed.

Read-only editor adjudication passed seven of eight original assertions. The remaining
assertion incorrectly equated one user turn with one user-role message: VS Code sends separate
environment and current-prompt messages. The new request contained no previous assistant/tool
history or write receipt. However, SessionStart already supplied the fact in the profile;
this establishes profile/recall interoperability, not recall-only answer attribution or retrieval quality.

The test exposed an authenticated stateless MCP GET/DELETE routing defect: the missing project
route produced a false OAuth challenge locally (or 404 remotely). The host now explicitly returns
405 with `Allow: POST` after normal authorization. Regression checks cover valid credentials,
missing/wrong-project credentials and untrusted origins in both transports. The real editor
then discovered all six tools and completed the bounded workflow without that OAuth detour.
This follows the [MCP transport contract](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports).
External HTTPS/OAuth connector acceptance remains separate.

## Codex interruption acceptance

The installed Codex app-server 0.162.1 passed eight bounded checks using the actual memory
helper and deterministic loopback model/service fixtures. After UserPromptSubmit captured a
turn, `turn/interrupt` emitted the matching Interrupt hook and an interrupted turn status.
The helper replaced the captured text with a tombstone; no memory write occurred.
This tests protocol cancellation, not a terminal Ctrl+C, provider outage or database workflow.
[App-server lifecycle](https://learn.chatgpt.com/docs/app-server),
[Interrupt hooks](https://learn.chatgpt.com/docs/hooks#interrupt).

The first attempt cancelled the turn but skipped all untrusted hooks, so it did not prove
cleanup. Reviewing and trusting the four exact hook hashes in the isolated profile enabled
the same test. Do not infer hook execution from successful cancellation alone. The earlier
real paired-hook run used CLI 0.160.1; these are separately versioned results.

## Trust and versions

Copilot 1.0.96 completed a model reply in an untrusted synthetic repository without running repository
hooks. After that test repository was trusted, the same hook definitions delivered profile and recall
context. User-level hooks were also checked separately. Review the repository and use the client's
normal trust flow; do not enable blanket tool permissions merely to activate memory.

An older installed Copilot 0.0.374 was inventoried but was not validated against these current hook
templates. The 1.0.96 acceptance executable was isolated; it did not replace the installed client.
Do not infer support from a client's product name alone.

Codex also requires hook trust, independently of MCP enablement. Our isolated automation used its
documented one-invocation hook-trust option after reviewing the fixture definitions. Normal users
should review hooks through `/hooks`. [Codex hook configuration](https://learn.chatgpt.com/docs/hooks).

## Memory delivered by hooks

All supported recall hooks use the same bounded renderer. It preserves selected facts,
preferences, entities, traces and the working-memory string returned by MCP. SessionStart
continues to read the profile object. Both paths label memory as untrusted, check decoded
strings for configured secrets and keep complete items within a 6,000-character context
budget. Recent/retrieved verbatim message windows are not injected by this helper.

## Supported completion capture

Claude exposes `prompt_id` and final assistant text; Codex exposes `turn_id` and final assistant text.
The helper uses each client's own correlation field. Missing IDs allow recall but prevent automatic
write pairing. A Stop without nonblank final assistant text discards the pending capture and reports
incomplete completion; it is not submitted as a user-only write. [Claude hooks](https://code.claude.com/docs/en/hooks),
[Codex hooks](https://learn.chatgpt.com/docs/hooks).

The documented Copilot `agentStop` and VS Code Local `Stop` contracts do not provide the paired
prompt/final-reply fields this helper requires. Their templates therefore use explicit MCP writes.
An instruction asking a model to call `remember` is not automatic capture. We do not parse unstable
transcripts or invent a completion correlation ID.
[Copilot hook reference](https://docs.github.com/en/copilot/reference/hooks-reference),
[VS Code Local hook reference](https://code.visualstudio.com/docs/agents/reference/hooks-reference).

## Model routing

Memory injected by a hook is sent to the coding client's selected model. The memory service's
provider settings do not select that client model. Verify both connections independently.
Codex custom providers use the Responses API; a Chat Completions endpoint alone is insufficient.
[Codex provider configuration](https://learn.chatgpt.com/docs/config-file/config-advanced).

Copilot supports custom Chat Completions providers and an offline mode that suppresses GitHub
connections. A remote custom endpoint still receives the prompt: offline mode does not make that
endpoint local. [Copilot custom providers](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/use-byok-models).

The [remote connector](coding-remote.md) additionally needs a real HTTPS deployment and external
OAuth issuer/client validation. Local token or synthetic issuer checks do not establish that flow.

Start with the [service guide](coding-service.md) and the client-specific templates for
[Claude](../samples/CodingMemory/README.md), [Codex](../samples/CodingMemory/codex.md), or
[Copilot/VS Code](../samples/CodingMemory/copilot.md). Templates remain inactive until configured.

The optional host `evidence` capability exposes read-scoped `recall_preview` and `recall_expand`
through the existing HTTP/MCP contract. Native hooks still use ordinary recall; discovery alone
does not establish that a particular client autonomously chooses or benefits from preview tools.
See [scope, current-state checks and output bounds](coding-service.md#optional-evidence-preview-and-expansion).
