using AgentMemory.Core.Extraction;

namespace AgentMemory.Core.Services;

/// <summary>Portable fallback for stores that do not expose a transaction coordinator.</summary>
internal sealed class PassThroughMemoryPersistenceTransaction : IMemoryPersistenceTransaction
{
    public bool SupportsAtomicRollback => false;

    public Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return work(cancellationToken);
    }
}
