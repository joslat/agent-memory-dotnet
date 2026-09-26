using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Core.Resolution;

internal interface IExtractionEntityResolver
{
    IDisposable BeginBatch();

    Task PrepareCandidatesAsync(
        IReadOnlyCollection<string> entityTypes,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);

    void InvalidateBatch();

    /// <summary>
    /// Embeds, in ONE request, the names of the batch's entities that no string matcher can resolve, so
    /// the semantic matcher finds their vectors ready instead of making one round trip per name.
    /// </summary>
    Task PrepareNameEmbeddingsAsync(
        IReadOnlyList<ExtractedEntity> entities,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);

    Task<Entity> ResolveForPersistenceAsync(
        ExtractedEntity extractedEntity,
        IReadOnlyList<string> sourceMessageIds,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);
}
