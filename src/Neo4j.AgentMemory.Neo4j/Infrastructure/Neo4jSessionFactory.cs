using Microsoft.Extensions.Options;
using Neo4j.Driver;

namespace Neo4j.AgentMemory.Neo4j.Infrastructure;

public sealed class Neo4jSessionFactory : INeo4jSessionFactory
{
    private readonly INeo4jDriverFactory _driverFactory;
    private readonly string _database;

    public Neo4jSessionFactory(INeo4jDriverFactory driverFactory, IOptions<Neo4jOptions> options)
    {
        _driverFactory = driverFactory;
        _database = options.Value.Database;
    }

    public IAsyncSession OpenSession(AccessMode accessMode = AccessMode.Write)
    {
        return _driverFactory.GetDriver().AsyncSession(c => c
            .WithDatabase(_database)
            .WithDefaultAccessMode(accessMode));
    }
}
