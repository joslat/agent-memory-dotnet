using Microsoft.Extensions.AI;

namespace AgentMemory.LongMemEval;

internal static class LongMemEvalRuntime
{
    internal const string DimensionProbe =
        "AgentMemory LongMemEval embedding dimension probe";

    internal static IChatClient CreateCompatibleChatClient(IChatClient inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return new DefaultTemperatureChatClient(inner);
    }

    internal static async Task<int> ProbeEmbeddingDimensionsAsync(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var generated = await generator
            .GenerateAsync([DimensionProbe], cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (generated.Count != 1)
        {
            throw new InvalidOperationException(
                $"The real embedding provider returned {generated.Count} vectors for the dimension probe; expected exactly one embedding.");
        }

        var dimensions = generated[0].Vector.Length;
        if (dimensions <= 0)
        {
            throw new InvalidOperationException(
                "The real embedding provider returned an empty embedding for the dimension probe.");
        }

        return dimensions;
    }

    internal static async Task<T> ExecuteStageAsync<T>(
        string stage,
        Func<Task<T>> operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"LongMemEval {stage} stage failed.",
                exception);
        }
    }
}
