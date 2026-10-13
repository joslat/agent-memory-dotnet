# Copilot and VS Code Local

These are inactive templates for the project-bound coding service. First follow the
[service guide](../../docs/coding-service.md). Select a model/provider allowed to receive this project's
memory; MCP results and hook context become model input. No client settings are installed by this sample.

## MCP and per-turn instructions

For VS Code, merge `vscode-mcp.example.json` into your workspace's `.vscode/mcp.json` and replace
`my-project`. The token is requested through a password input. For Copilot CLI, merge
`copilot-mcp.example.json` into a private `~/.copilot/mcp-config.json`, replace the project, and set
`MY_PROJECT_MEMORY_TOKEN` in the CLI's environment. Keep existing server entries.
The two clients have different configuration roots; do not interchange these files.
[VS Code reference](https://code.visualstudio.com/docs/agents/reference/mcp-configuration),
[Copilot reference](https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-command-reference).

Review and merge `copilot-instructions.example.md` into the project's existing
`.github/copilot-instructions.md`. The six MCP tools handle recall, sourced writes, status, profile,
history and closing. These instructions depend on the model choosing the tools; they are not an
automatic capture guarantee. Keep the client's normal tool permissions in place.

## Optional session-start profile

Copy `client.example.json` to a private configuration file. Add `"harness": "vsCodeLocal"` for VS Code's
**Local** harness, or `"harness": "copilotCli"` for Copilot CLI. Omission retains the existing Claude
behavior. Adjust repository, token variable and paths. The shared configuration still requires an
outside-repository state directory, but these two hooks neither create nor read pending-turn state.

Merge the corresponding `vscode-hooks.example.json` or `copilot-hooks.example.json` into a hook file
under `.github/hooks/`. Keep the configurations separate: VS Code Local uses a shell command string
(quote paths containing spaces); Copilot CLI uses direct `exec` plus an argument array. Match each
template to its private client configuration. The helper retains the same bounded input/output, Git
root checks, credential filtering, local-only HTTP and five-second default deadline as the Claude hook.

`SessionStart` reads the working profile; no prompt is captured, no transcript is
opened, and no write or recall inference call is made. VS Code receives its nested context envelope;
Copilot receives top-level `additionalContext`. Copilot's template deliberately uses the documented
PascalCase event alias for snake_case input fields.
[VS Code hooks](https://code.visualstudio.com/docs/agents/reference/hooks-reference),
[Copilot hooks](https://docs.github.com/en/copilot/reference/hooks-reference).

## Optional Copilot per-turn recall

The Copilot template additionally registers native `userPromptTransformed` with
`--coding-hook <private-client.json> --event userPromptTransformed`. This explicit event argument
requires `harness: copilotCli`; it is not inferred from arbitrary stdin fields. Omitting the event
keeps the existing session-start behavior. Use a client version supporting this native hook.

The helper checks the Git root and sends only `prompt` as the recall query, with a Copilot-namespaced
session ID. Runtime-expanded `transformedPrompt` text stays in the hook process. It preserves that
text verbatim and appends bounded, sourced, untrusted memory via `modifiedTransformedPrompt`.
Empty recall returns no override. Failures keep the client's original prompt and emit a generic notice.
The helper accepts at most 8,000 query characters, 32,768 transformed characters and 64 KiB of stdin;
recalled context is at most 6,000 characters, and serialized output is capped at 64 KiB. Batched
submissions may invoke the hook for more than one message. The returned content becomes model input
and session history under Copilot's contract; use a model authorized to receive the recalled memory.
[Native transformed-prompt contract](https://docs.github.com/en/copilot/reference/hooks-reference#userprompttransformed).

This adapter creates no pending state, reads no transcript and performs no write. The existing
`UserPromptSubmit` command event still cannot inject context, and VS Code Local remains session-start
only. Neither documented main-agent completion payload supplies the paired reply contract used by the
Claude adapter. Writes therefore still use MCP instructions; fully automatic per-turn capture remains
open. Do not add completion hooks that guess a turn or upload a transcript.

## Validation boundary

Copilot CLI 1.0.96 and the actual helper passed 7/7 protocol checks with local fixtures and a trusted
synthetic repository. An untrusted-repository control completed its model reply without running the hooks.
These checks use canned replies; they do not establish that a real model follows the MCP instructions.
VS Code Local still needs actual-editor acceptance. See the [compatibility matrix](../../docs/coding-clients.md).
Verify the client version, hook diagnostics, tools and a synthetic session before enabling
real project memory. Hook execution requires the client's workspace trust and hook settings. A local
service must run in the same network environment as the client; these templates do not connect a cloud
agent or remote development container to your desktop's loopback service.
