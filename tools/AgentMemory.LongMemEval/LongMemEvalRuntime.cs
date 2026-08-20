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
            // The consuming benchmark package catches whatever we throw and records only
            // "Agent execution did not complete", so a stage failure is otherwise invisible: two
            // 4.5-hour runs failed on all 50 questions and left no diagnosable error anywhere.
            // Printed here, at the one chokepoint every stage passes through, so the next failure
            // costs one line of log instead of nine hours.
            Console.Error.WriteLine(
                $"longmemeval: STAGE FAILURE [{stage}] {exception.GetType().Name}: {exception.Message}");
            for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
                Console.Error.WriteLine(
                    $"longmemeval:   caused by {inner.GetType().Name}: {inner.Message}");

            throw new InvalidOperationException(
                $"LongMemEval {stage} stage failed.",
                exception);
        }
    }
}
