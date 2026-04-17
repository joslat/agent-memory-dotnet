using Neo4j.AgentMemory.Abstractions.Domain;

namespace Neo4j.AgentMemory.Core.Extraction;

/// <summary>
/// Runs extractors, merges multi-extractor results, filters, validates, and resolves entities.
/// Does NOT embed or persist — that is delegated to <see cref="IPersistenceStage"/>.
/// </summary>
internal interface IExtractionStage
{
    /// <summary>
    /// Extracts, merges, filters, validates, and resolves items from the given messages.
    /// </summary>
    Task<ExtractionStageResult> ExtractAsync(
        IReadOnlyList<Message> messages,
        ExtractionTypes typesToExtract,
        CancellationToken cancellationToken = default);
}
