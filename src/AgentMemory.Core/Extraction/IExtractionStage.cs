using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Core.Extraction;

/// <summary>
/// Runs extractors, merges multi-extractor results, filters, validates, and resolves entities.
/// Does NOT embed or persist — that is delegated to <see cref="IPersistenceStage"/>.
/// </summary>
internal interface IExtractionStage
{
    /// <summary>
    /// Extracts, merges, filters, validates, and resolves items from the given messages. When
    /// <paramref name="scope"/> is supplied (R1) entity resolution is confined to the owner's own and
    /// (optionally) shared entities, so resolution cannot reach across the owner isolation boundary.
    /// </summary>
    Task<ExtractionStageResult> ExtractAsync(
        IReadOnlyList<Message> messages,
        ExtractionTypes typesToExtract,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the normal validation, owner-scoped resolution, and filtering stages to a unified
    /// result that was already extracted by a validated multi-session batch.
    /// </summary>
    Task<ExtractionStageResult> ProcessUnifiedAsync(
        IReadOnlyList<Message> messages,
        UnifiedExtractionResult extracted,
        ExtractionTypes typesToExtract,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);
}
