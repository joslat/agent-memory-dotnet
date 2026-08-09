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

    Task<Entity> ResolveForPersistenceAsync(
        ExtractedEntity extractedEntity,
        IReadOnlyList<string> sourceMessageIds,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);
}
