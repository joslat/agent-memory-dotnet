using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace AgentMemory.Samples.Shared;

/// <summary>
/// Wraps a real <see cref="IChatClient"/> to print the memory the Neo4j memory context provider recalled
/// and merged into the request as <c>&lt;recalled_memory category="..."&gt;</c> blocks — right before the
/// live model call, so recall is visible instead of implicit. Checks both <see cref="ChatRole.System"/> and
/// <see cref="ChatRole.User"/> messages: a recalled block renders at one or the other depending on its
/// trust level and the host's configured <c>ContextFormatOptions.MinimumTrustForSystemRole</c> (#92 Phase 4).
/// </summary>
public sealed partial class MemoryTraceChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    [GeneratedRegex("""<recalled_memory category="(?<category>[^"]+)">(?<body>.*?)</recalled_memory>""",
        RegexOptions.Singleline)]
    private static partial Regex RecalledMemoryPattern();

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        PrintRecalledMemory(messages);
        return base.GetResponseAsync(messages, options, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        PrintRecalledMemory(messages);
        return base.GetStreamingResponseAsync(messages, options, cancellationToken);
    }

    private static void PrintRecalledMemory(IEnumerable<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            if (message.Role != ChatRole.System && message.Role != ChatRole.User) continue;
            if (string.IsNullOrEmpty(message.Text)) continue;

            var match = RecalledMemoryPattern().Match(message.Text);
            if (!match.Success) continue;

            var category = match.Groups["category"].Value;
            var body = WebUtility.HtmlDecode(match.Groups["body"].Value);
            SampleConsole.WriteRecalledMemory(category, body);
        }
    }
}
