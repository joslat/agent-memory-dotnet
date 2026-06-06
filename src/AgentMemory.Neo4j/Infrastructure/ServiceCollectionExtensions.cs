using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Neo4j.Services;

namespace AgentMemory.Neo4j.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Neo4j infrastructure services and all repository implementations.
    /// </summary>
    public static IServiceCollection AddNeo4jAgentMemory(
        this IServiceCollection services,
        Action<Neo4jOptions> configure,
        Action<MemoryStoreOptions>? configureStore = null)
    {
        services.AddOptions<Neo4jOptions>()
            .Configure(configure)
            .Validate(o => !string.IsNullOrWhiteSpace(o.Uri), "Neo4j Uri must be provided.")
            .Validate(o => Uri.TryCreate(o.Uri, UriKind.Absolute, out _), "Neo4j Uri must be a valid absolute URI.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Username), "Neo4j Username must be provided.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Database), "Neo4j Database must be provided.")
            .Validate(o => o.MaxConnectionPoolSize > 0, "Neo4j MaxConnectionPoolSize must be positive.")
            .Validate(o => o.EmbeddingDimensions > 0, "Neo4j EmbeddingDimensions must be positive.")
            .ValidateOnStart();

        // Infrastructure
        services.TryAddSingleton<INeo4jDriverFactory, Neo4jDriverFactory>();
        services.TryAddSingleton<INeo4jSessionFactory, Neo4jSessionFactory>();
        services.TryAddTransient<INeo4jTransactionRunner, Neo4jTransactionRunner>();
        services.TryAddTransient<ISchemaBootstrapper, SchemaBootstrapper>();
        services.TryAddTransient<IMigrationRunner, MigrationRunner>();

        // Application / memory-store isolation tier (R1b). Additive: the SharedDatabase default with a
        // null ApplicationId routes to Neo4jOptions.Database, exactly reproducing single-store behavior.
        // IMemoryStoreContext is a singleton whose ApplicationId is AsyncLocal-backed, so it is safe to
        // set per request/agent-run flow (IC6) — concurrent requests cannot corrupt each other's routing.
        services.AddOptions<MemoryStoreOptions>().Configure(o => configureStore?.Invoke(o));
        services.TryAddSingleton<DefaultMemoryStoreContext>();
        services.TryAddSingleton<IMemoryStoreContext>(sp => sp.GetRequiredService<DefaultMemoryStoreContext>());
        services.TryAddSingleton<IMemoryStoreProvisioner, Neo4jMemoryStoreProvisioner>();

        // Short-term memory repositories
        services.TryAddTransient<IConversationRepository, Neo4jConversationRepository>();
        services.TryAddTransient<IMessageRepository, Neo4jMessageRepository>();

        // Long-term memory repositories
        services.TryAddTransient<IEntityRepository, Neo4jEntityRepository>();
        services.TryAddTransient<IFactRepository, Neo4jFactRepository>();
        services.TryAddTransient<IPreferenceRepository, Neo4jPreferenceRepository>();
        services.TryAddTransient<IRelationshipRepository, Neo4jRelationshipRepository>();

        // Provenance
        services.TryAddTransient<IExtractorRepository, Neo4jExtractorRepository>();

        // Reasoning memory repositories
        services.TryAddTransient<IReasoningTraceRepository, Neo4jReasoningTraceRepository>();
        services.TryAddTransient<IReasoningStepRepository, Neo4jReasoningStepRepository>();
        services.TryAddTransient<IToolCallRepository, Neo4jToolCallRepository>();

        // Graph query service
        services.TryAddTransient<IGraphQueryService, Neo4jGraphQueryService>();

        // Memory-hygiene / consolidation (PR #113) — dry-run by default.
        services.TryAddTransient<IConsolidationService, Neo4jConsolidationService>();

        // Conflict / contradiction detection (detect-only).
        services.TryAddTransient<IConflictDetectionService, Neo4jConflictDetectionService>();

        return services;
    }

    /// <summary>
    /// Registers <see cref="Neo4jGraphRagContextSource"/> as the <see cref="IGraphRagContextSource"/>
    /// implementation and configures <see cref="GraphRagOptions"/>.
    /// </summary>
    public static IServiceCollection AddGraphRagAdapter(
        this IServiceCollection services,
        Action<GraphRagOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<GraphRagOptions>()
            .Configure(configure)
            .Validate(o => !string.IsNullOrWhiteSpace(o.IndexName), "GraphRag IndexName must be provided.")
            .Validate(o => o.TopK > 0, "GraphRag TopK must be positive.")
            .Validate(
                o => o.SearchMode != AgentMemory.Abstractions.Domain.GraphRagSearchMode.Hybrid
                     || !string.IsNullOrWhiteSpace(o.FulltextIndexName),
                "GraphRag FulltextIndexName is required for Hybrid search mode.")
            .Validate(
                o => o.SearchMode != AgentMemory.Abstractions.Domain.GraphRagSearchMode.Graph
                     || o.MaxTraversalHops is >= 1 and <= 5,
                "GraphRag MaxTraversalHops must be between 1 and 5 for Graph search mode.")
            .ValidateOnStart();
        services.TryAddScoped<IGraphRagContextSource, Neo4jGraphRagContextSource>();
        return services;
    }
}

