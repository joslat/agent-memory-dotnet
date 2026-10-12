# Codex with project memory

These inactive templates target the documented Codex CLI hook/MCP contracts. They do not install a
plugin, trust a hook, connect project memory or change your model configuration.

## Model and memory connections

Recalled memory becomes input to Codex's selected model. The memory service's Bitdeer/Ollama setting
does **not** select Codex's provider. Verify the active client model, provider and downstream routing
before connecting real data; a hook's model name alone is not proof of its destination. Keep integration
disabled if that data path is not authorized.

Codex supports custom provider configuration, but its current custom wire protocol is `responses`.
Do not assume a Chat Completions endpoint works, or that a gateway avoids OpenAI downstream. Configure
and validate the authorized provider separately; this sample does not assert Bitdeer compatibility.
[Official configuration reference](https://learn.chatgpt.com/docs/config-file/config-reference).

## MCP

Merge `codex-mcp.example.toml` into the appropriate user-level Codex configuration, retaining other
servers. Replace `my-project` and set the named token variable in Codex's environment. The example
stays `enabled = false`; enable it only after the data path and local service are ready. Authentication
uses an environment-variable reference, with no token in TOML. Review and merge the optional instruction
fragment into your existing `AGENTS.md` rather than replacing repository rules.
[Official MCP reference](https://learn.chatgpt.com/docs/extend/mcp?surface=cli).

## Paired hooks

Copy `codex-client.example.json` outside source control and configure its paths. The private state
directory must be outside repositories; pending prompts are temporarily stored there in plaintext,
with the [same bounds and permissions](README.md#pending-state-and-limits) as the Claude helper.
Use a distinct state directory for each client. The two-second helper deadline leaves room for the
shorter interruption hook; process startup and abrupt termination can still prevent cleanup.

After authorization, merge the hook template into `.codex/hooks.json` and review/trust it using Codex's
`/hooks` interface. Hook trust is separate from MCP enablement: disabling the MCP entry does not disable
HTTP hooks. Keep template files out of active configuration until both paths are intended to run.
[Official hooks reference](https://learn.chatgpt.com/docs/hooks).

Our helper uses `turn_id` for Codex, never a Claude `prompt_id` alias. SessionStart reads the profile;
UserPromptSubmit captures a bounded turn and recalls typed context; a matching Stop queues its user
text and optional final reply. Interrupt discards the matching pending capture. Source provenance says
it is unverified. Codex service sessions and pending-file keys are separated from Claude's; existing
Claude filenames and behavior stay compatible.

Missing turn IDs allow recall only. Subagent and compaction events are ignored. The helper neither reads
transcripts nor guesses summaries; Stop returns a queued receipt without asking Codex to continue.
Job completion still needs `write_status`. An interruption after service acceptance cannot undo an
already queued job. Failed/missed cleanup falls back to the existing state expiry on next access.

## What has been checked

Synthetic contract tests and the actual helper executable with fake local HTTP cover capture, matching
completion, interruption, duplicates, isolation and credential checks. These tests do not run a Codex
model session, establish provider compatibility, or measure memory quality. Validate the actual client
and authorized provider with synthetic data before real use. No settings are installed automatically.
