using AgentMemory.Core.Extraction;

namespace AgentMemory.Neo4j.Infrastructure;

/// <summary>
/// Runs memory persistence inside one Neo4j write transaction when the configured runner can
/// provide one, and passes work straight through when it cannot.
/// </summary>
/// <remarks>
/// <see cref="INeo4jTransactionRunner"/> is a <b>public</b> extension point, registered with
/// <c>TryAddSingleton</c> precisely so a host can substitute its own implementation. This type is
/// then registered unconditionally with <c>Replace</c>, so it receives whatever the host supplied.
/// <para>
/// It previously hard-cast that runner to <see cref="INeo4jAtomicTransactionRunner"/> — an interface
/// this library added later — and threw when the cast failed. That turned a documented, deliberately
/// overridable seam into a startup crash for any host that had already exercised it: the substitution
/// was legal when they wrote it and became fatal on upgrade, with no compile-time signal.
/// </para>
/// <para>
/// Atomicity is optional by design, which is why <see cref="IMemoryPersistenceTransaction"/> carries
/// <see cref="SupportsAtomicRollback"/> at all and why <c>PersistenceStage</c> already branches on
/// it. So a runner without atomic support degrades to pass-through and reports that honestly, rather
/// than claiming a rollback guarantee it cannot keep or refusing to start.
/// </para>
/// </remarks>
internal sealed class Neo4jMemoryPersistenceTransaction : IMemoryPersistenceTransaction
{
    private readonly INeo4jAtomicTransactionRunner? _atomicRunner;

    public Neo4jMemoryPersistenceTransaction(INeo4jTransactionRunner transactionRunner)
    {
        ArgumentNullException.ThrowIfNull(transactionRunner);
        _atomicRunner = transactionRunner as INeo4jAtomicTransactionRunner;
    }

    /// <summary>True only when the configured runner can actually roll back.</summary>
    public bool SupportsAtomicRollback => _atomicRunner is not null;

    public Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (_atomicRunner is not null)
            return _atomicRunner.ExecuteAtomicWriteAsync(work, cancellationToken);

        // No coordinator: run the work as-is. Callers that need all-or-nothing check
        // SupportsAtomicRollback first, so this cannot silently downgrade a guarantee.
        cancellationToken.ThrowIfCancellationRequested();
        return work(cancellationToken);
    }
}
