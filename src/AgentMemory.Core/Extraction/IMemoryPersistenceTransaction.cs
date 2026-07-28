namespace AgentMemory.Core.Extraction;

/// <summary>
/// Internal transaction boundary for one logical memory-persistence operation.
/// Storage providers that support transactions commit all repository work atomically;
/// providers without that capability execute the callback directly.
/// </summary>
internal interface IMemoryPersistenceTransaction
{
    Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default);
}
