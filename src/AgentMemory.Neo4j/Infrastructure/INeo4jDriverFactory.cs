using Neo4j.Driver;

namespace AgentMemory.Neo4j.Infrastructure;

internal interface INeo4jDriverFactory : IAsyncDisposable
{
    IDriver GetDriver();
}
