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

Only `SessionStart` is wired. It reads the working profile; no prompt is captured, no transcript is
opened, and no write or recall inference call is made. VS Code receives its nested context envelope;
Copilot receives top-level `additionalContext`. Copilot's template deliberately uses the documented
PascalCase event alias for snake_case input fields.
[VS Code hooks](https://code.visualstudio.com/docs/agents/reference/hooks-reference),
[Copilot hooks](https://docs.github.com/en/copilot/reference/hooks-reference).

The documented Local prompt event does not promise additional-context output. Copilot's command
prompt hook discards its output. Neither documented main-agent completion payload supplies our
Claude-style prompt/reply correlation contract. Per-turn recall and writes therefore use MCP instructions;
fully automatic per-turn integration remains open. Do not relabel these templates as Claude hooks or
add Stop/PreCompact hooks that guess a turn or upload a transcript.

## Validation boundary

The helper is tested with documented synthetic events and a fake local service. This is not evidence
that your installed client supports the current hook schema or that the selected model follows the
instructions. Verify the client version, hook diagnostics, tools and a synthetic session before enabling
real project memory. Hook execution requires the client's workspace trust and hook settings. A local
service must run in the same network environment as the client; these templates do not connect a cloud
agent or remote development container to your desktop's loopback service.
