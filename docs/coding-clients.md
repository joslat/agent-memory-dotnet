# Coding client compatibility

The coding service and a coding client are separate integrations. A successful model reply does not
prove that the client ran memory hooks or connected its MCP tools. Check both paths before using
project memory. These observations were checked on 13 October 2026.

| Client | Implemented hook path | Writes | Actual-client evidence |
|---|---|---|---|
| Claude Code 2.1.296 | Session profile; prompt capture and recall; matching completion; failure cleanup | Paired `prompt_id` hooks, or explicit MCP | Actual CLI and helper processes passed 7/7 protocol checks with local service/model fixtures. |
| Codex CLI 0.160.1 | Session profile; prompt capture and recall; matching completion; interruption cleanup | Paired `turn_id` hooks, or explicit MCP | Actual CLI and helper processes passed 7/7 protocol checks with local service/model fixtures. |
| Copilot CLI 1.0.96 | Session profile and native transformed-prompt recall | Explicit MCP | 7/7 protocol checks, plus a real Bitdeer/MCP/Neo4j run with 18/18 bounded checks. |
| VS Code Local | Session profile | Explicit MCP | Synthetic contract coverage; actual editor workflow remains unverified. Select the Local harness: provider-hosted sessions have different contracts. |

The protocol checks use canned model replies and synthetic HTTP memory responses. They establish
hook invocation, context delivery and the applicable write/recall envelope, **not** model quality,
durable ingestion, interruption behavior in every client, or a complete database-backed workflow.
The separate unit suites cover interruption, duplicate handling, malformed input and failures.

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

## Supported completion capture

Claude exposes `prompt_id` and final assistant text; Codex exposes `turn_id` and final assistant text.
The helper uses each client's own correlation field. Missing IDs allow recall but prevent automatic
write pairing. [Claude hooks](https://code.claude.com/docs/en/hooks),
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
