using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Core.Services;

internal sealed partial class LongTermMemoryService
{
    public Task<IReadOnlyList<Fact>> GetEventDetailsAsync(IReadOnlyList<string> seedFactIds,
        IReadOnlyCollection<string> excludeFactIds, int limit, MemoryScope? scope,
        CancellationToken cancellationToken = default)
    {
        var resolved = _isolationPolicy.ResolveReadScope(
            scope, ownerId: null, nameof(GetEventDetailsAsync), MemoryOperationAccess.Tenant);
        return _factRepo.GetEventDetailsAsync(seedFactIds, excludeFactIds, limit, resolved, cancellationToken);
    }
}
