using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Neo4j.AgentMemory.Abstractions.Repositories;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Neo4j.Repositories;
using Neo4j.AgentMemory.Neo4j.Services;

namespace Neo4j.AgentMemory.Neo4j.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Neo4j infrastructure services and all repository implementations.
    /// </summary>
    public static IServiceCollection AddNeo4jAgentMemory(
        this IServiceCollection services,
        Action<Neo4jOptions> configure)
    {
        services.Configure(configure);

        // Infrastructure
        services.TryAddSingleton<INeo4jDriverFactory, Neo4jDriverFactory>();
        services.TryAddSingleton<INeo4jSessionFactory, Neo4jSessionFactory>();
        services.TryAddTransient<INeo4jTransactionRunner, Neo4jTransactionRunner>();
        services.TryAddTransient<ISchemaBootstrapper, SchemaBootstrapper>();
        services.TryAddTransient<IMigrationRunner, MigrationRunner>();

        // Short-term memory repositories
        services.TryAddTransient<IConversationRepository, Neo4jConversationRepository>();
        services.TryAddTransient<IMessageRepository, Neo4jMessageRepository>();

        // Long-term memory repositories
        services.TryAddTransient<IEntityRepository, Neo4jEntityRepository>();
        services.TryAddTransient<IFactRepository, Neo4jFactRepository>();
        services.TryAddTransient<IPreferenceRepository, Neo4jPreferenceRepository>();
        services.TryAddTransient<IRelationshipRepository, Neo4jRelationshipRepository>();

        // Reasoning memory repositories
        services.TryAddTransient<IReasoningTraceRepository, Neo4jReasoningTraceRepository>();
        services.TryAddTransient<IReasoningStepRepository, Neo4jReasoningStepRepository>();
        services.TryAddTransient<IToolCallRepository, Neo4jToolCallRepository>();

        // Graph query service
        services.TryAddTransient<IGraphQueryService, Neo4jGraphQueryService>();

        return services;
    }
}

