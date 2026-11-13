using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Core.Services;

internal sealed partial class LongTermMemoryService
{
    public Task<IReadOnlyList<PersonIdentity>> GetPersonIdentitiesAsync(int limit, MemoryScope? scope,
        CancellationToken cancellationToken = default)
    {
        var resolved = _isolationPolicy.ResolveReadScope(
            scope, ownerId: null, nameof(GetPersonIdentitiesAsync), MemoryOperationAccess.Tenant);
        return _factRepo.GetPersonIdentitiesAsync(limit, resolved, cancellationToken);
    }

    public Task<IReadOnlyList<Fact>> GetPersonDetailsAsync(IReadOnlyList<string> personEntityIds,
        IReadOnlyCollection<string> excludeFactIds, int limit, MemoryScope? scope,
        CancellationToken cancellationToken = default)
    {
        var resolved = _isolationPolicy.ResolveReadScope(
            scope, ownerId: null, nameof(GetPersonDetailsAsync), MemoryOperationAccess.Tenant);
        return _factRepo.GetPersonDetailsAsync(personEntityIds, excludeFactIds, limit, resolved, cancellationToken);
    }
}
