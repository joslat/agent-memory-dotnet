namespace Neo4j.AgentMemory.Neo4j.Infrastructure;

public interface IMigrationRunner
{
    Task RunMigrationsAsync(CancellationToken cancellationToken = default);
}
