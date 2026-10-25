using Microsoft.Extensions.AI;

namespace AgentMemory.RouterArena.Ask;

/// <summary>Counts the tokens of every call it carries, failed answers included; prices are GLM-5.3-Flash's on Bitdeer.</summary>
internal sealed class MeteredChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    /// <summary>USD per million tokens in and out.</summary>
    public const double InPrice = 0.07, OutPrice = 0.25;

    public long In { get; private set; }
    public long Out { get; private set; }

    /// <summary>What the calls so far cost.</summary>
    public double Cost => Math.Round((In * InPrice + Out * OutPrice) / 1e6, 6);

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        In += response.Usage?.InputTokenCount ?? 0;
        Out += response.Usage?.OutputTokenCount ?? 0;
        return response;
    }

    protected override void Dispose(bool disposing) { }
}
