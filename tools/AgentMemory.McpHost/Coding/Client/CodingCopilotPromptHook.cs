using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static AgentMemory.McpHost.Coding.Client.CodingHookInput;

namespace AgentMemory.McpHost.Coding.Client;

/// <summary>Explicit native Copilot prompt transformation. Reads memory; never captures a turn or opens a transcript.</summary>
internal sealed class CodingCopilotPromptHook(CodingHookOptions options, CodingContentPolicy policy,
    HttpClient client, Func<string, CancellationToken, Task<string>> commit)
{
    public async Task<CodingHookReply> HandleAsync(JsonElement input, CancellationToken cancellationToken = default)
    {
        if (options.Harness != CodingHookHarness.CopilotCli)
            throw new ArgumentException("Unsupported prompt-transform harness.");
        // The native payload has no event discriminator; the CLI requires an explicit event argument.
        // Never reinterpret a different harness's event or a subagent event as a primary prompt.
        if (Text(input, "hook_event_name") is not null || Text(input, "agent_id") is not null || Text(input, "agentId") is not null)
            return new();
        var session = Text(input, "sessionId") ?? throw new ArgumentException("Missing hook session.");
        var prompt = Text(input, "prompt") ?? throw new ArgumentException("Missing hook prompt.");
        var transformed = Text(input, "transformedPrompt") ?? throw new ArgumentException("Missing transformed prompt.");
        CodingInput.RequireText(session, 256, nameof(session));
        CodingInput.RequireText(prompt, 8000, nameof(prompt));
        CodingInput.RequireText(transformed, 32768, nameof(transformed));
        policy.CheckText(session);
        policy.CheckText(prompt);
        var cwd = Text(input, "cwd") ?? throw new ArgumentException("Missing hook working directory.");
        if (!Path.IsPathFullyQualified(cwd) || !CodingHookOptions.IsWithin(options.RepositoryRoot, cwd))
            throw new ArgumentException("Hook is outside its configured repository.");
        _ = await commit(cwd, cancellationToken).ConfigureAwait(false);
        session = "copilot:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(session)));
        // Only the user query reaches the memory service, never runtime-expanded files or instructions.
        var response = await new CodingHookHttp(options, client)
            .PostAsync("recall", new { session, query = prompt }, HttpStatusCode.OK, cancellationToken).ConfigureAwait(false);
        var context = CodingHookContext.Render(response, false, policy);
        if (context.Length == 0) return new();
        var reply = new CodingHookReply
        {
            ModifiedTransformedPrompt = transformed + "\n\n<agent-memory-context>\n" + context + "\n</agent-memory-context>",
        };
        if (JsonSerializer.SerializeToUtf8Bytes(reply, CodingApi.Json).Length > CodingBodyLimit.MaximumBytes)
            throw new ArgumentException("Oversized transformed-prompt output.");
        return reply;
    }
}
