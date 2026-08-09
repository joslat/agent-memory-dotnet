namespace AgentMemory.Core.Extraction;

/// <summary>
/// Internal transaction boundary for one logical memory-persistence operation.
/// Storage providers that support transactions commit all repository work atomically;
/// providers without that capability execute the callback directly.
/// </summary>
internal interface IMemoryPersistenceTransaction
{
    /// <summary>
    /// Whether a failed callback is guaranteed not to commit and rollback failure is surfaced as a
    /// different exception. The default keeps portable/pass-through providers on the legacy path.
    /// </summary>
    bool SupportsAtomicRollback => false;

    Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default);
}
