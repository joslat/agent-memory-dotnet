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

    /// <summary>
    /// Asks the live provider how wide its embeddings are, and checks the answer against what was
    /// configured.
    /// </summary>
    /// <param name="generator">The embedding generator.</param>
    /// <param name="expected">
    /// The width the provider contract resolved, when there is one. A MISMATCH THROWS.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// <b>The probe stays the value used, and the resolved one is the check.</b> The probe is ground
    /// truth — it asks the model that will actually write the vectors — but it is one call, made
    /// once, and it cannot notice that the store was BUILT at a different width. The resolved value
    /// is what configures the store. When they disagree, one of them is wrong about an index that
    /// cannot report its own corruption, so this refuses rather than picking a winner: the shipped
    /// dimensions table has a bad row, or AI_EMBEDDING_DIMENSIONS is set to the wrong number.
    /// </remarks>
    internal static async Task<int> ProbeEmbeddingDimensionsAsync(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        int? expected = null,
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

        if (expected is { } configured && configured != dimensions)
        {
            throw new InvalidOperationException(
                $"The embedding model returned {dimensions}-wide vectors but the provider contract "
                + $"resolved {configured}. The store's vector index is built from the resolved value, "
                + "so continuing would write vectors it cannot match. Either the shipped "
                + "known-dimensions table has a wrong row for this model, or AI_EMBEDDING_DIMENSIONS "
                + "is set to the wrong number.");
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
