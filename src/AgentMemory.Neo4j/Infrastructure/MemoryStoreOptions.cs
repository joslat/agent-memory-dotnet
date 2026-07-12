using AgentMemory.Abstractions.Services;

namespace AgentMemory.Neo4j.Infrastructure;

/// <summary>
/// How memory stores (applications) are physically isolated. See
/// <c>docs/archive/Memory_Review_and_Implementation_Plan.md</c> (R1b).
/// </summary>
public enum MemoryStorageStrategy
{
    /// <summary>
    /// One Neo4j database; isolate users logically via <c>owner_id</c> (R1). Community-compatible.
    /// This is the default and reproduces the library's original single-store behavior.
    /// </summary>
    SharedDatabase,

    /// <summary>
    /// Each <c>ApplicationId</c> routes to its own Neo4j database. Requires Neo4j Enterprise or AuraDB
    /// (Community Edition supports only a single user database).
    /// </summary>
    DatabasePerApplication,
}

/// <summary>
/// Options for the application / memory-store isolation tier (R1b). Additive: the defaults
/// (<see cref="MemoryStorageStrategy.SharedDatabase"/> + <see cref="DefaultDatabase"/>) reproduce the
/// pre-existing single-store behavior, so existing deployments are unaffected.
/// </summary>
public sealed class MemoryStoreOptions
{
    /// <summary>Storage isolation strategy. Defaults to <see cref="MemoryStorageStrategy.SharedDatabase"/>.</summary>
    public MemoryStorageStrategy Strategy { get; set; } = MemoryStorageStrategy.SharedDatabase;

    /// <summary>
    /// The Neo4j database used when <c>ApplicationId</c> is null (the default store). Blank (the
    /// default) means "inherit <see cref="Neo4jOptions.Database"/>", so the SharedDatabase path is
    /// byte-for-byte identical to the pre-R1b single-store behavior whatever database is configured.
    /// </summary>
    public string DefaultDatabase { get; set; } = "";

    /// <summary>
    /// Database-name prefix for <see cref="MemoryStorageStrategy.DatabasePerApplication"/>:
    /// <c>db = DatabasePrefix + sanitize(ApplicationId)</c>. Uses a dash (not underscore) because
    /// Neo4j database names disallow underscores.
    /// </summary>
    public string DatabasePrefix { get; set; } = "mem-";

    /// <summary>
    /// When true (and strategy is <see cref="MemoryStorageStrategy.DatabasePerApplication"/>), the
    /// store's database is created and its schema bootstrapped on first use.
    /// </summary>
    public bool AutoProvision { get; set; } = true;
}

/// <summary>
/// Default mutable <see cref="IMemoryStoreContext"/>. <see cref="ApplicationId"/> is backed by an
/// <see cref="AsyncLocal{T}"/> so this can be safely registered as a process-wide singleton: each
/// async flow (request / agent run) sees its own value, set e.g. by the MAF provider for that scope.
/// Concurrent requests therefore cannot corrupt each other's store routing (R1b). Setting it flows to
/// every awaited call downstream on the same logical async context — including the session factory.
/// </summary>
internal sealed class DefaultMemoryStoreContext : IWritableMemoryStoreContext
{
    private readonly AsyncLocal<string?> _applicationId = new();

    /// <inheritdoc />
    public string? ApplicationId
    {
        get => _applicationId.Value;
        set => _applicationId.Value = value;
    }
}
