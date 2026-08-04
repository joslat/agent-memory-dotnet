using AgentMemory.Core.Extraction;

namespace AgentMemory.Neo4j.Infrastructure;

internal sealed class Neo4jMemoryPersistenceTransaction : IMemoryPersistenceTransaction
{
    private readonly INeo4jAtomicTransactionRunner _transactionRunner;

    public Neo4jMemoryPersistenceTransaction(INeo4jTransactionRunner transactionRunner)
    {
        _transactionRunner = transactionRunner as INeo4jAtomicTransactionRunner
            ?? throw new InvalidOperationException(
                $"The configured {nameof(INeo4jTransactionRunner)} must also implement " +
                $"{nameof(INeo4jAtomicTransactionRunner)} for atomic memory persistence.");
    }

    public bool SupportsAtomicRollback => true;

    public Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default) =>
        _transactionRunner.ExecuteAtomicWriteAsync(work, cancellationToken);
}
