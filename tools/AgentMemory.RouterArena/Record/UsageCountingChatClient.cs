using Microsoft.Extensions.AI;

namespace AgentMemory.RouterArena.Record;

/// <summary>
/// Counts what a run asks of the chat model: calls, input and output tokens (as the provider reports them). store-sessions
/// reads it before and after each turn, so a turn's cost is recorded with its writes. The update judge (System One) is not
/// a chat client and is not counted here; a turn's milliseconds include it.
/// </summary>
internal sealed class UsageCountingChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    private long _calls;
    private long _input;
    private long _output;

    public (long Calls, long Input, long Output) Snapshot() =>
        (Interlocked.Read(ref _calls), Interlocked.Read(ref _input), Interlocked.Read(ref _output));

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        Count(response.Usage);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        UsageDetails? usage = null;
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            foreach (var content in update.Contents.OfType<UsageContent>())
                usage = content.Details;
            yield return update;
        }
        Count(usage);
    }

    private void Count(UsageDetails? usage)
    {
        Interlocked.Increment(ref _calls);
        Interlocked.Add(ref _input, usage?.InputTokenCount ?? 0);
        Interlocked.Add(ref _output, usage?.OutputTokenCount ?? 0);
    }
}
