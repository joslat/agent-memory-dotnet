using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Neo4j.AgentMemory.Neo4j.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Neo4j infrastructure services: driver factory, session factory,
    /// transaction runner, schema bootstrapper, and migration runner.
    /// </summary>
    public static IServiceCollection AddNeo4jAgentMemory(
        this IServiceCollection services,
        Action<Neo4jOptions> configure)
    {
        services.Configure(configure);

        services.TryAddSingleton<INeo4jDriverFactory, Neo4jDriverFactory>();
        services.TryAddSingleton<INeo4jSessionFactory, Neo4jSessionFactory>();
        services.TryAddTransient<INeo4jTransactionRunner, Neo4jTransactionRunner>();
        services.TryAddTransient<ISchemaBootstrapper, SchemaBootstrapper>();
        services.TryAddTransient<IMigrationRunner, MigrationRunner>();

        return services;
    }
}
