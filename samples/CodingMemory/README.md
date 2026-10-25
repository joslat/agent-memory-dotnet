# Claude Code with local project memory

> **Experimental.** Coding mode ships for evaluation only; see the note in
> [coding service](../../docs/coding-service.md).

For Copilot CLI or VS Code Local, use the separate [client guide](copilot.md) and templates.
For Codex, use the [paired-hook and disabled MCP templates](codex.md); review its model/provider path first.
For an authenticated remote MCP connector, see the [HTTPS/OAuth guide](../../docs/coding-remote.md)
and inactive `remote-service.example.json`. Remote mode has a separate token contract and defaults to read-only.

These are templates, not installed client settings. Use a build of the host that includes
`--coding-hook`; an older released tool may not contain it. See the [service setup](../../docs/coding-service.md)
first. No provider or client is activated by building the sample.

See the [client compatibility matrix](../../docs/coding-clients.md) for exact tested versions and
the distinction between actual CLI protocol checks and real model/database acceptance.

To inspect project memory locally, the service also has an optional [memory page](../../docs/coding-service.md#local-memory-page).
Enable `enableBrowserUi` in its private configuration, open `/projects/<project>/ui` on the local service,
and connect with that project's token. It can show history, propose corrections and close memories.

1. Copy `client.example.json` into a private location outside the repository and edit its paths/project.
   Use an owner-private state directory outside all source-controlled folders. The hook stores bounded
   pending prompt text there in plaintext. On Windows it inherits that directory's access permissions;
   new Unix state files are owner-readable/writable. Paths through links are rejected.
2. Set the same project-token environment variable used by the service. `secretVariables` lists any
   additional credential variables to exclude from local capture; missing optional variables are ignored.
   Keep token values out of JSON and command lines.
3. Edit the executable/config paths in `claude-hooks.example.json`, then merge its entries into your
   chosen Claude Code settings file when ready to activate. Do not replace unrelated settings.
   The template uses `dotnet` with the built host DLL; a matching installed tool can instead use
   `command: "agent-memory-mcp"` and omit the DLL argument.
4. For explicit tools, merge `claude-mcp.example.json` into the selected project's `.mcp.json` and set
   the project URL/token variable. Review Claude's normal workspace/server trust prompts.

The template uses direct executable arguments rather than shell interpolation. Claude documents
`prompt_id` correlation, `Stop` completion input and `StopFailure` separately. Automated events can
also reach `UserPromptSubmit`; captured source provenance is therefore unverified. See the
[hook reference](https://code.claude.com/docs/en/hooks). MCP header variables are documented in the
[MCP configuration reference](https://code.claude.com/docs/en/mcp#environment-variable-expansion-in-mcpjson).

## What the client does

| Event | Behavior |
|---|---|
| SessionStart | Inject a bounded project profile |
| UserPromptSubmit | Capture a bounded prompt and current Git commit; recall relevant typed memories |
| Stop | Queue the matching captured turn plus bounded assistant text; report the job ID without continuing the agent |
| StopFailure | Discard that pending turn; never store the API error as a reply |

Subagent events and repeated Stop continuations are ignored. Missing prompt IDs allow recall but disable
automatic write pairing. An uncommitted/invalid Git HEAD, another repository (including a nested one),
rejected input or unavailable service produces a generic non-blocking notice. A failed recall can leave
the captured prompt available for its later Stop. No transcript or source file is opened; Git is used
only to verify repository identity and read HEAD.

Context excludes verbatim message windows and includes whole facts/preferences/entities/traces and
profile blocks, capped at 6,000 characters with an untrusted-data label. Oversized individual items are
omitted, never split into partial claims. This does not make recalled text immune to prompt injection.
Responses and hook input are each limited to 64 KiB. The default total hook deadline is five seconds;
keep the outer hook timeout slightly longer.

## Pending state and limits

At most 128 pending records/receipts share a state directory. Pending turns expire after 24 hours and
are discarded on the next read/cleanup; this is not a background erasure timer. Completion removes prompt
text from the record and leaves a small tombstone against duplicate events. Old completed records can be
reclaimed for new captures. Exclusive file access prevents concurrent completion submissions. A network
failure retains the pending turn for a later retry; service-side turn identity still governs duplicates.
Neither client state nor server acceptance proves that extraction succeeded: use `write_status` with the
returned ID. The client does not poll the model job to completion.

Bulk prompts, transcripts, recognizable secrets and configured credential values are rejected.
An oversized or rejected assistant reply rejects the paired submission too. These checks are heuristics;
configure additional credential variables and do not send sensitive material without authorization.
Hook context goes to the coding agent's selected model, in addition to the memory service's configured
providers. Select both provider paths deliberately.

PreCompact does not contain a ready-made decision digest. Pending capture already survives compaction;
this client makes no extra write and does not synthesize or upload a transcript summary. Use the explicit
`remember` tool for a deliberate, sourced decision digest. Real client activation, model-backed behavior
and latency/quality measurements remain deployment validation work.
