using Neo4j.Driver;

namespace Neo4j.AgentMemory.Neo4j.Infrastructure;

public interface INeo4jSessionFactory
{
    IAsyncSession OpenSession(AccessMode accessMode = AccessMode.Write);
}
