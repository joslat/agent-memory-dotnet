using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.Logging;

namespace AgentMemory.Core.Extraction;

/// <summary>Owns model selection and approval for the existing named and similarity closing policies.</summary>
internal sealed class ClosingPolicy(
    ExtractionOptions options, IMemoryUpdateJudge? updateJudge, IMemoryUpdateJudgeFallback? fallbackJudge, ILogger logger)
{
    public bool CanJudgeSimilarity => updateJudge is { IsEnabled: true };

    public bool CanJudgeNamed => updateJudge is { IsEnabled: true } || fallbackJudge is { IsEnabled: true };

    public double Threshold(bool named) => named ? options.NamedClosingThreshold : options.UpdateJudgeThreshold;

    /// <summary>Missing answers are handled by the caller; invalid scores never authorize a closing.</summary>
    public bool Approves(double probability, bool named) =>
        double.IsFinite(probability) && probability is >= 0 and <= 1 && !(probability < Threshold(named));

    /// <summary>
    /// Evaluates one source's existing batch. Callers check availability before discovering candidates.
    /// The original score dictionary is returned so custom key lookup and missing-answer semantics are preserved.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, double>> EvaluateAsync(
        MemoryUpdateRequest request, CancellationToken cancellationToken = default)
    {
        if (!request.Named)
            return await updateJudge!.JudgeAsync(request, cancellationToken).ConfigureAwait(false);

        if (updateJudge is { IsEnabled: true } judge)
        {
            if (fallbackJudge is not { IsEnabled: true })
                return await judge.JudgeAsync(request, cancellationToken).ConfigureAwait(false);
            try
            {
                return await judge.JudgeAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Memory writer: the update judge failed ({Error}); the host's chat model judges these closings instead.",
                    ex.Message);
            }
        }
        return await fallbackJudge!.JudgeAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
