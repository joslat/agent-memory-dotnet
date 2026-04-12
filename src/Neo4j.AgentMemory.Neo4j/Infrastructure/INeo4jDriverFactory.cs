using Neo4j.Driver;

namespace Neo4j.AgentMemory.Neo4j.Infrastructure;

public interface INeo4jDriverFactory : IAsyncDisposable
{
    IDriver GetDriver();
}
