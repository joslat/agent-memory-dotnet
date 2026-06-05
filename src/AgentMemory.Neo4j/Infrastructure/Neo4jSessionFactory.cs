using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Services;
using Neo4j.Driver;

namespace AgentMemory.Neo4j.Infrastructure;

/// <summary>
/// Opens Neo4j sessions, routing to the correct store database (R1b). The database is resolved per
/// call from the ambient <see cref="IMemoryStoreContext"/> and <see cref="MemoryStoreOptions"/>, so a
/// single driver can serve multiple application stores. With the default SharedDatabase strategy the
/// resolution collapses to the configured <see cref="Neo4jOptions.Database"/> — identical to the
/// original single-store behavior.
/// </summary>
public sealed class Neo4jSessionFactory : INeo4jSessionFactory
{
    private readonly INeo4jDriverFactory _driverFactory;
    private readonly MemoryStoreOptions _storeOptions;
    private readonly IMemoryStoreContext _storeContext;
    private readonly string _defaultDatabase;

    public Neo4jSessionFactory(
        INeo4jDriverFactory driverFactory,
        IOptions<Neo4jOptions> options,
        IOptions<MemoryStoreOptions> storeOptions,
        IMemoryStoreContext storeContext)
    {
        _driverFactory = driverFactory;
        _storeOptions = storeOptions.Value;
        _storeContext = storeContext;
        _defaultDatabase = options.Value.Database;
    }

    public IAsyncSession OpenSession(AccessMode accessMode = AccessMode.Write)
    {
        var database = StoreDatabaseNaming.Resolve(_storeOptions, _defaultDatabase, _storeContext.ApplicationId);
        return OpenSession(database, accessMode);
    }

    public IAsyncSession OpenSession(string database, AccessMode accessMode = AccessMode.Write)
    {
        return _driverFactory.GetDriver().AsyncSession(c => c
            .WithDatabase(database)
            .WithDefaultAccessMode(accessMode));
    }
}
