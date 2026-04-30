using Neo4j.Driver;

namespace AgentMemory.Neo4j.Infrastructure;

public interface INeo4jDriverFactory : IAsyncDisposable
{
    IDriver GetDriver();
}
