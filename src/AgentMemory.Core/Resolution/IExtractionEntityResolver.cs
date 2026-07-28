using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Core.Resolution;

internal interface IExtractionEntityResolver
{
    Task<Entity> ResolveForPersistenceAsync(
        ExtractedEntity extractedEntity,
        IReadOnlyList<string> sourceMessageIds,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);
}
