namespace AgentMemory.Neo4j.Infrastructure;

/// <summary>Capability interface for joining repository calls into one logical write transaction.</summary>
public interface INeo4jAtomicTransactionRunner
{
    /// <summary>
    /// Executes a logical persistence unit in one write transaction. Repository calls made from
    /// <paramref name="work"/> through the paired <see cref="INeo4jTransactionRunner"/> join it.
    /// </summary>
    Task<T> ExecuteAtomicWriteAsync<T>(
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default);
}
