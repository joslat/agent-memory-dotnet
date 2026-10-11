using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using Microsoft.Extensions.Logging;

namespace AgentMemory.Core.Extraction;

/// <summary>Reinforces the writer's confirmations after persistence, without inserting another memory.</summary>
internal sealed class ConfirmedMemoryReinforcement
{
    private readonly IFactRepository _factRepository;
    private readonly IPreferenceRepository _preferenceRepository;
    private readonly double _reinforceAlpha;
    private readonly ILogger _logger;

    public ConfirmedMemoryReinforcement(IFactRepository factRepository, IPreferenceRepository preferenceRepository,
        double reinforceAlpha, ILogger logger)
    {
        _factRepository = factRepository;
        _preferenceRepository = preferenceRepository;
        _reinforceAlpha = reinforceAlpha;
        _logger = logger;
    }

    /// <summary>
    /// AMWRITE001. The stored facts and preferences a store-aware writer said the turn says again (its "confirm"): each live
    /// one of this owner is reinforced as a repeated write reinforces it (one more <c>mention_count</c>, confidence raised by
    /// <see cref="MemoryOptions.ConfidenceReinforcementAlpha"/>), through the dedup path (<c>MarkDeduplicatedAsync</c>). The
    /// writer stores nothing twice, so without this a memory said again and again stayed at one mention. A failure is logged
    /// and leaves the memory as it was.
    /// </summary>
    public async Task ApplyAsync(ExtractionStageResult extraction, string? ownerId, CancellationToken cancellationToken)
    {
        if (!extraction.WrittenByWriter || extraction.ConfirmedFactIds.Count + extraction.ConfirmedPreferenceIds.Count == 0) return;
        bool Owned(string? owner) => string.IsNullOrEmpty(ownerId) ? string.IsNullOrEmpty(owner) : owner == ownerId;
        var reinforced = 0;
        try
        {
            foreach (var id in extraction.ConfirmedFactIds.Distinct(StringComparer.Ordinal))
            {
                var fact = await _factRepository.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
                if (fact is null || fact.InvalidatedAtUtc is not null || !Owned(fact.OwnerId)) continue;
                if (await _factRepository.MarkDeduplicatedAsync(id, Math.Min(1.0, fact.Confidence + _reinforceAlpha), cancellationToken).ConfigureAwait(false) is not null)
                    reinforced++;
            }
            foreach (var id in extraction.ConfirmedPreferenceIds.Distinct(StringComparer.Ordinal))
            {
                var preference = await _preferenceRepository.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
                if (preference is null || preference.InvalidatedAtUtc is not null || !Owned(preference.OwnerId)) continue;
                if (await _preferenceRepository.MarkDeduplicatedAsync(id, Math.Min(1.0, preference.Confidence + _reinforceAlpha), cancellationToken).ConfigureAwait(false) is not null)
                    reinforced++;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Memory writer: reinforcing what the turn said again FAILED ({Error}); those memories stay as they were.", ex.Message);
        }
        if (reinforced > 0)
            _logger.LogInformation("Memory writer: {Count} stored memory(ies) said again, reinforced.", reinforced);
    }

}
