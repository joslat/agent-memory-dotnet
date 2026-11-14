using Microsoft.Extensions.AI;

namespace AgentMemory.Inference;

/// <summary>
/// Sends the configured reasoning effort with every request that does not choose its own (ILC-32). The effort travels as
/// <see cref="ChatOptions.Reasoning"/>, which the provider's client maps to its own parameter (OpenAI protocol:
/// <c>reasoning_effort</c>); a caller's options are copied, never changed.
/// </summary>
internal sealed class ReasoningEffortChatClient(IChatClient inner, ReasoningEffort effort) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(messages, WithEffort(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(messages, WithEffort(options), cancellationToken);

    private ChatOptions WithEffort(ChatOptions? options)
    {
        if (options?.Reasoning is not null) return options;
        var copy = options?.Clone() ?? new ChatOptions();
        copy.Reasoning = new ReasoningOptions { Effort = effort };
        return copy;
    }
}
