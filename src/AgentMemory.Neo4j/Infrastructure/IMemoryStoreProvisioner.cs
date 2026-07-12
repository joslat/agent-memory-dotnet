namespace AgentMemory.Neo4j.Infrastructure;

/// <summary>
/// Ensures the physical store (Neo4j database) for an application exists and is schema-bootstrapped
/// (R1b). A no-op under the default <see cref="MemoryStorageStrategy.SharedDatabase"/> strategy.
/// </summary>
internal interface IMemoryStoreProvisioner
{
    /// <summary>
    /// Ensures the store for <paramref name="applicationId"/> is ready.
    /// <list type="bullet">
    /// <item>SharedDatabase, or a null/empty application id ⇒ no-op (the default database is assumed to exist).</item>
    /// <item>DatabasePerApplication with <c>AutoProvision</c> ⇒ <c>CREATE DATABASE … IF NOT EXISTS</c> and
    /// bootstraps the schema in the new database. Idempotent and cached per database name.</item>
    /// </list>
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// DatabasePerApplication is requested but the Neo4j edition does not support multiple databases
    /// (Community Edition). Use SharedDatabase, or run a separate Neo4j instance per application.
    /// </exception>
    Task EnsureStoreAsync(string? applicationId, CancellationToken cancellationToken = default);
}
