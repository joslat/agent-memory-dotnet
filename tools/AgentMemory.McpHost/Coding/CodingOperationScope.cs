using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentMemory.McpHost.Coding;

/// <summary>Owns a fresh service scope for the whole awaited operation, including background jobs.</summary>
internal sealed class CodingOperationScope(IServiceScopeFactory scopes)
{
    public async Task<T> RunAsync<T>(CodingIdentity identity,
        Func<IServiceProvider, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.OwnerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.ProjectId);
        await using var scope = scopes.CreateAsyncScope();
        var storage = scope.ServiceProvider.GetRequiredService<IOptions<MemoryStoreOptions>>().Value;
        if (storage.Strategy != MemoryStorageStrategy.DatabasePerApplication ||
            storage.DatabasePrefix != "coding-" || storage.AutoProvision)
            throw new InvalidOperationException("Coding operations require pre-provisioned, separate coding-<project> databases.");
        using var owner = scope.ServiceProvider.GetRequiredService<IWritableMemoryOwnerContext>()
            .BeginOwnerScope(identity.OwnerId);
        using var store = scope.ServiceProvider.GetRequiredService<IWritableMemoryStoreContext>()
            .BeginStoreScope(identity.ProjectId);
        return await operation(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
    }
}
