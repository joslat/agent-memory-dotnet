using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Core.Extraction;

/// <summary>Records persistence outcomes with the same fail-fast contract for every item kind.</summary>
internal static class PersistenceOutcomes
{
    /// <summary>Appends a <see cref="IngestionItemStatus.Succeeded"/> outcome (#101).</summary>
    internal static void RecordSuccess(
        List<IngestionItemOutcome> outcomes, MemoryItemKind kind, string? sourceKey, string? persistedId,
        MemoryWriteEffect effect = MemoryWriteEffect.Unreported) =>
        outcomes.Add(new IngestionItemOutcome
        {
            Kind = kind,
            Stage = IngestionStage.Persistence,
            Status = IngestionItemStatus.Succeeded,
            SourceKey = sourceKey,
            PersistedId = persistedId,
            Effect = effect,
        });

    /// <summary>
    /// Appends a <see cref="IngestionItemStatus.Failed"/> outcome and, under
    /// <see cref="IngestionFailureMode.FailFast"/>, throws <see cref="MemoryIngestionException"/>
    /// carrying every outcome recorded so far (#101). Centralizing this in one helper — rather than
    /// repeating "add outcome, then maybe throw" at each of the ~10 call sites across four item kinds —
    /// is what guarantees every one of them gets the same fail-fast behavior; hand-copying it previously
    /// let the relationship block silently miss the failure-mode check entirely (caught in review).
    /// </summary>
    internal static void RecordFailureAndMaybeThrow(
        List<IngestionItemOutcome> outcomes,
        bool failFast,
        MemoryItemKind kind,
        IngestionStage stage,
        string errorCode,
        string? sourceKey,
        string? persistedId,
        Exception ex,
        string failFastMessage)
    {
        outcomes.Add(new IngestionItemOutcome
        {
            Kind = kind,
            Stage = stage,
            Status = IngestionItemStatus.Failed,
            SourceKey = sourceKey,
            PersistedId = persistedId,
            ErrorCode = errorCode,
            ErrorMessage = ex.Message,
            Retryable = true,
        });

        if (failFast)
            throw new MemoryIngestionException(failFastMessage, outcomes, ex);
    }
}
