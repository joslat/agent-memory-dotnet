namespace AgentMemory.Neo4j.Infrastructure;

public interface ISchemaBootstrapper
{
    Task BootstrapAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// J-8a. A one-off repair, run on demand (never at bootstrap): facts stored before predicate-echo trimming
    /// ("Daniel | is a chef | chef", which renders "is a chef chef") are rewritten as they read once trimmed
    /// ("Daniel | is | a chef"), or, when that trimmed fact already exists, superseded by it. With
    /// <paramref name="apply"/> false nothing is written and the count says what would change. Returns the number of
    /// facts rewritten or superseded (or that would be).
    /// </summary>
    Task<int> RetrimEchoedPredicatesAsync(bool apply, CancellationToken cancellationToken = default) =>
        Task.FromResult(0);
}
