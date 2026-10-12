using System.Text.Json;
using System.Net;
using static AgentMemory.McpHost.Coding.Client.CodingHookInput;

namespace AgentMemory.McpHost.Coding.Client;

internal sealed class CodingSessionStartHook(CodingHookOptions options, CodingContentPolicy policy,
    HttpClient client, Func<string, CancellationToken, Task<string>> commit)
{
    public async Task<CodingHookReply> HandleAsync(JsonElement input, CancellationToken cancellationToken = default)
    {
        // Copilot is configured with its documented PascalCase input alias. Output still uses its own contract.
        if (Text(input, "hook_event_name") != "SessionStart" || Text(input, "agent_id") is not null) return new();
        var session = Text(input, "session_id") ?? throw new ArgumentException("Missing hook session.");
        CodingInput.RequireText(session, 256, nameof(session));
        policy.CheckText(session);
        var cwd = Text(input, "cwd") ?? throw new ArgumentException("Missing hook working directory.");
        if (!Path.IsPathFullyQualified(cwd) || !CodingHookOptions.IsWithin(options.RepositoryRoot, cwd))
            throw new ArgumentException("Hook is outside its configured repository.");
        _ = await commit(cwd, cancellationToken).ConfigureAwait(false);
        var response = await new CodingHookHttp(options, client).PostAsync("profile", new { }, HttpStatusCode.OK, cancellationToken).ConfigureAwait(false);
        var context = CodingHookContext.Render(response, true, policy);
        if (context.Length == 0) return new();
        return options.Harness switch
        {
            CodingHookHarness.VsCodeLocal => new() { HookSpecificOutput = new { hookEventName = "SessionStart", additionalContext = context } },
            CodingHookHarness.CopilotCli => new() { AdditionalContext = context },
            _ => throw new ArgumentException("Unsupported session-start harness."),
        };
    }
}
