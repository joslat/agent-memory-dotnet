using Microsoft.Extensions.AI;

namespace AgentMemory.Cli.Perf;

/// <summary>
/// Counts embedding requests and items for the turn in flight, then delegates.
/// </summary>
/// <remarks>
/// A decorator is the right shape <em>here</em> — unlike the database and recall counters, which are
/// emitted from inside the product — because <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> is a
/// Microsoft.Extensions.AI type whose implementations we do not own and cannot instrument in place.
/// </remarks>
public sealed class CountingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _inner;

    public CountingEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> inner) => _inner = inner;

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Materialize once: `values` may be a lazy sequence, and counting it by enumerating separately
        // would either double-enumerate it or consume it before the inner generator sees it.
        var materialized = values as IList<string> ?? values.ToList();

        var turn = PerfCollector.Current;
        if (turn is not null)
        {
            turn.Add("embed.requests");
            turn.Add("embed.items", materialized.Count);
            turn.Add("embed.chars", materialized.Sum(v => (long)(v?.Length ?? 0)));
        }

        return _inner.GenerateAsync(materialized, options, cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : _inner.GetService(serviceType, serviceKey);

    public void Dispose() => _inner.Dispose();
}

/// <summary>
/// Counts model completions and tokens for the turn in flight, then delegates. Same reasoning as
/// <see cref="CountingEmbeddingGenerator"/>: <see cref="IChatClient"/> is not ours to instrument.
/// </summary>
public sealed class CountingChatClient : IChatClient
{
    private readonly IChatClient _inner;

    public CountingChatClient(IChatClient inner) => _inner = inner;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await _inner.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        Record(response);
        return response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        PerfCollector.Current?.Add("llm.calls");
        await foreach (var update in _inner
            .GetStreamingResponseAsync(messages, options, cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            yield return update;
        }
    }

    private static void Record(ChatResponse response)
    {
        var turn = PerfCollector.Current;
        if (turn is null) return;

        turn.Add("llm.calls");
        if (response.Usage is { } usage)
        {
            turn.Add("llm.tokens_in", usage.InputTokenCount ?? 0);
            turn.Add("llm.tokens_out", usage.OutputTokenCount ?? 0);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : _inner.GetService(serviceType, serviceKey);

    public void Dispose() => _inner.Dispose();
}
