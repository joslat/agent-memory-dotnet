namespace Neo4j.AgentMemory.Neo4j.Infrastructure;

public interface ISchemaBootstrapper
{
    Task BootstrapAsync(CancellationToken cancellationToken = default);
}
