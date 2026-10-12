using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net;
using static AgentMemory.McpHost.Coding.Client.CodingHookInput;

namespace AgentMemory.McpHost.Coding.Client;

internal sealed record CodingHookReply
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AdditionalContext { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? HookSpecificOutput { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SystemMessage { get; init; }
}

internal sealed class ClaudeCodingHook(CodingHookOptions options, CodingContentPolicy policy, CodingHookState state,
    HttpClient client, Func<string, CancellationToken, Task<string>> commit)
{
    private readonly CodingHookHttp _http = new(options, client);

    public async Task<CodingHookReply> HandleAsync(JsonElement input, CancellationToken cancellationToken = default)
    {
        var name = Text(input, "hook_event_name");
        if (name is not ("SessionStart" or "UserPromptSubmit" or "Stop" or "StopFailure") || Text(input, "agent_id") is not null)
            return new();
        var session = Text(input, "session_id") ?? throw new ArgumentException("Missing hook session.");
        CodingInput.RequireText(session, 256, nameof(session));
        policy.CheckText(session);
        var promptId = Text(input, "prompt_id");
        if (promptId is not null)
        {
            CodingInput.RequireText(promptId, 256, nameof(promptId));
            policy.CheckText(promptId);
        }
        var cwd = Text(input, "cwd") ?? throw new ArgumentException("Missing hook working directory.");
        if (!Path.IsPathFullyQualified(cwd) || !CodingHookOptions.IsWithin(options.RepositoryRoot, cwd))
            throw new ArgumentException("Hook is outside its configured repository.");
        var hash = await commit(cwd, cancellationToken).ConfigureAwait(false);
        if (name == "StopFailure")
        {
            if (promptId is not null) state.Discard(session, promptId);
            return new();
        }
        if (name == "Stop")
        {
            if (promptId is null) return new() { SystemMessage = "Memory write skipped: missing prompt_id." };
            if (input.TryGetProperty("stop_hook_active", out var active) && active.ValueKind == JsonValueKind.True) return new();
            string? job = null;
            await state.ConsumeAsync(session, promptId, async (saved, ct) =>
            {
                var finished = saved with { AssistantText = Text(input, "last_assistant_message") };
                policy.Check(finished);
                var result = await _http.PostAsync("remember", finished, HttpStatusCode.Accepted, ct).ConfigureAwait(false);
                job = Text(result, "jobId");
                if (!Guid.TryParseExact(job, "N", out _)) throw new JsonException("Invalid job receipt.");
            }, cancellationToken).ConfigureAwait(false);
            return job is null ? new() : new() { SystemMessage = $"Memory write queued ({job}); use write_status to check completion." };
        }
        object body;
        string? notice = null;
        if (name == "UserPromptSubmit")
        {
            var prompt = Text(input, "prompt") ?? throw new ArgumentException("Missing hook prompt.");
            CodingInput.RequireText(prompt, 8000, nameof(prompt));
            policy.CheckText(prompt);
            if (promptId is not null)
            {
                var captured = new CodingRememberRequest
                {
                    Session = session, TurnId = promptId, UserText = prompt, Commit = hash,
                    Source = "claude/UserPromptSubmit; origin unverified",
                };
                policy.Check(captured);
                await state.CaptureAsync(captured, cancellationToken).ConfigureAwait(false);
            }
            else notice = "Memory recall only: missing prompt_id prevents safe write pairing.";
            body = new { session, query = prompt };
        }
        else body = new { };
        var profile = name == "SessionStart";
        var response = await _http.PostAsync(profile ? "profile" : "recall", body, HttpStatusCode.OK, cancellationToken).ConfigureAwait(false);
        var context = CodingHookContext.Render(response, profile, policy);
        return new()
        {
            SystemMessage = notice,
            HookSpecificOutput = context.Length == 0 ? null : new { hookEventName = name, additionalContext = context },
        };
    }

}
