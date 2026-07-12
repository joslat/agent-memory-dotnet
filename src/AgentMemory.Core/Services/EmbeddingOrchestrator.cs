using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Core.Services;

/// <summary>
/// Default implementation of <see cref="IEmbeddingOrchestrator"/> that delegates to an
/// <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/>. Blank inputs are skipped and generation
/// failures are logged and degraded to empty vectors so callers always receive an array aligned
/// with their input.
/// </summary>
internal sealed class EmbeddingOrchestrator : IEmbeddingOrchestrator
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly ILogger<EmbeddingOrchestrator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EmbeddingOrchestrator"/> class.
    /// </summary>
    /// <param name="generator">The underlying embedding generator used to produce vectors.</param>
    /// <param name="logger">The logger used to record generation failures.</param>
    public EmbeddingOrchestrator(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        ILogger<EmbeddingOrchestrator> logger)
    {
        _generator = generator;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<float>();

        try
        {
            var result = await _generator.GenerateAsync([text], cancellationToken: cancellationToken).ConfigureAwait(false);
            return result[0].Vector.ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Honor caller cancellation — do not mask it as a successful empty vector.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Embedding generation failed for text (length={Len}); returning empty vector.", text.Length);
            return Array.Empty<float>();
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0)
            return Array.Empty<float[]>();

        // Blank inputs are not sent to the generator; their slots stay empty so the result aligns
        // positionally with the input list.
        var results = new float[texts.Count][];
        for (int i = 0; i < results.Length; i++)
            results[i] = Array.Empty<float>();

        var nonBlankIndices = new List<int>(texts.Count);
        var nonBlankTexts = new List<string>(texts.Count);
        for (int i = 0; i < texts.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(texts[i]))
            {
                nonBlankIndices.Add(i);
                nonBlankTexts.Add(texts[i]);
            }
        }

        if (nonBlankTexts.Count == 0)
            return results;

        try
        {
            var generated = await _generator.GenerateAsync(nonBlankTexts, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (generated.Count != nonBlankTexts.Count)
            {
                // Contract expects one vector per input; a mismatch means the generator misbehaved.
                // Surface it (the unmatched trailing slots stay empty) rather than failing silently.
                _logger.LogWarning(
                    "Batch embedding generator returned {Returned} vectors for {Requested} inputs; unmatched slots left empty.",
                    generated.Count, nonBlankTexts.Count);
            }
            for (int j = 0; j < nonBlankIndices.Count && j < generated.Count; j++)
                results[nonBlankIndices[j]] = generated[j].Vector.ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Honor caller cancellation — do not mask it as successful empty vectors.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Batch embedding generation failed for {Count} texts; returning empty vectors.", nonBlankTexts.Count);
            // results already initialized to empty vectors — positional alignment preserved.
        }

        return results;
    }
}
