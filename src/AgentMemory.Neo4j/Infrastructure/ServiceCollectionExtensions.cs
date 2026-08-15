using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Neo4j.Driver;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Core.Extraction;
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
            .Validate(
                o => o.TransactionTimeout is null || o.TransactionTimeout > TimeSpan.Zero,
                "Neo4j TransactionTimeout must be positive when set; use null for no deadline. "
                + "Zero or negative is rejected rather than silently treated as 'no timeout', because "
                + "an operator who configured a bound would then be running without one.")
            .Validate(o => o.EmbeddingDimensions > 0, "Neo4j EmbeddingDimensions must be positive.")
            .Validate(o => o.ConnectionAcquisitionTimeout > TimeSpan.Zero, "Neo4j ConnectionAcquisitionTimeout must be positive.")
            .ValidateOnStart();

        // Infrastructure
        services.TryAddSingleton<INeo4jDriverFactory, Neo4jDriverFactory>();
        // Raw IDriver, for components (e.g. Neo4jGraphRagContextSource) that need direct driver access
        // rather than going through INeo4jDriverFactory. Same singleton instance/lifetime either way --
        // INeo4jDriverFactory still owns creation and disposal.
        services.TryAddSingleton<IDriver>(sp => sp.GetRequiredService<INeo4jDriverFactory>().GetDriver());
        services.TryAddSingleton<INeo4jSessionFactory, Neo4jSessionFactory>();
        // Singleton so repository instances in the same async flow can join the transaction opened
        // for one logical extraction-persistence operation.
        services.TryAddSingleton<INeo4jTransactionRunner, Neo4jTransactionRunner>();
        services.Replace(ServiceDescriptor.Singleton<IMemoryPersistenceTransaction, Neo4jMemoryPersistenceTransaction>());
        services.TryAddTransient<ISchemaBootstrapper, SchemaBootstrapper>();
        services.TryAddTransient<IMigrationRunner, MigrationRunner>();

        // Application / memory-store isolation tier (R1b). Additive: the SharedDatabase default with a
        // null ApplicationId routes to Neo4jOptions.Database, exactly reproducing single-store behavior.
        // IMemoryStoreContext is a singleton whose ApplicationId is AsyncLocal-backed, so it is safe to
        // set per request/agent-run flow (IC6) — concurrent requests cannot corrupt each other's routing.
        services.AddOptions<MemoryStoreOptions>()
            .Configure(o => configureStore?.Invoke(o))
            .Validate(
                o => o.Strategy != MemoryStorageStrategy.DatabasePerApplication || !string.IsNullOrWhiteSpace(o.DatabasePrefix),
                "MemoryStore DatabasePrefix must be non-blank when Strategy is DatabasePerApplication.")
            .ValidateOnStart();
        services.TryAddSingleton<DefaultMemoryStoreContext>();
        services.TryAddSingleton<IMemoryStoreContext>(sp => sp.GetRequiredService<DefaultMemoryStoreContext>());
        services.TryAddSingleton<IWritableMemoryStoreContext>(sp => sp.GetRequiredService<DefaultMemoryStoreContext>());
        services.TryAddSingleton<IMemoryStoreProvisioner, Neo4jMemoryStoreProvisioner>();

        // Short-term memory repositories
        services.TryAddTransient<IConversationRepository, Neo4jConversationRepository>();
        services.TryAddTransient<IMessageRepository, Neo4jMessageRepository>();

        // Long-term memory repositories
        services.TryAddTransient<IEntityRepository, Neo4jEntityRepository>();
        services.TryAddTransient<IFactRepository, Neo4jFactRepository>();
        services.TryAddTransient<IPreferenceRepository, Neo4jPreferenceRepository>();
        // S1. Registered beside the other repositories rather than behind a feature flag: an
        // unregistered store would make EntitySummaryService unresolvable, so the option that
        // enables summaries would fail at host start rather than simply doing nothing.
        services.TryAddTransient<IEntitySummaryRepository, Neo4jEntitySummaryRepository>();
        services.TryAddTransient<IRelationshipRepository, Neo4jRelationshipRepository>();

        // Provenance
        services.TryAddTransient<IExtractorRepository, Neo4jExtractorRepository>();

        // Custom entity-schema persistence (G4) — versioned :Schema nodes, global (not owner-scoped).
        services.TryAddTransient<ISchemaManager, Neo4jSchemaManager>();

        // Reasoning memory repositories
        services.TryAddTransient<IReasoningTraceRepository, Neo4jReasoningTraceRepository>();
        services.TryAddTransient<IReasoningStepRepository, Neo4jReasoningStepRepository>();
        services.TryAddTransient<IToolCallRepository, Neo4jToolCallRepository>();

        // 17.4b. Rerankers (R6 node-distance, R7 mention-frequency). Both shipped with full unit
        // suites and BOTH WERE REGISTERED BY NOTHING -- zero DI registrations and zero RerankAsync
        // call sites across the whole repository -- while `MemoryOptions.NodeDistanceReranking` and
        // `MentionFrequencyReranking` sat in the public surface documenting behaviour no consumer
        // could obtain, and Phase 10 was recorded COMPLETE.
        //
        // Registered unconditionally and enumerable, because each one already owns its own IsEnabled
        // gate reading those options. Gating the REGISTRATION on the flags instead would mean a host
        // using IOptions reconfiguration (or IOptionsSnapshot) could turn a flag on and still get
        // nothing -- the same class of defect one layer up. Both flags default false, so the default
        // recall path is unchanged.
        services.AddScoped<IMemoryReranker, NodeDistanceReranker>();
        services.AddScoped<IMemoryReranker, MentionFrequencyReranker>();

        // 30.14. Schema extensions, registered on exactly the reranker principle above: always
        // present, never active unless Neo4jOptions.Extensions names them. Registration is what makes
        // an extension *knowable* -- the owners report and the parity validator inspect every
        // registered extension, active or not, so a declaration that collides with base is caught on a
        // machine that has never enabled it. Empty Extensions set = base schema, byte-identical.
        // Registered from the SAME list the host-less callers read (the schema-parity CLI verb builds
        // no container), so DI and the CLI can never disagree about which extensions exist.
        foreach (var extension in Schema.Extensions.SchemaExtensionRegistry.CreateShipped())
            services.AddSingleton(extension);
        services.TryAddSingleton<Schema.Extensions.SchemaExtensionRegistry>();

        // 30.4. Registered unconditionally and self-gated on WorkingMemoryOptions.Enabled -- the
        // reranker pattern, so IOptions reconfiguration works.
        services.TryAddScoped<IWorkingMemoryService, Services.Neo4jWorkingMemoryService>();

        // Graph query service
        services.TryAddTransient<IGraphQueryService, Neo4jGraphQueryService>();

        // Long-term memory history / audit read model.
        services.TryAddTransient<IMemoryHistoryService, Neo4jMemoryHistoryService>();

        // Memory-hygiene / consolidation (PR #113) — dry-run by default.
        services.TryAddTransient<IConsolidationService, Neo4jConsolidationService>();

        // Conflict / contradiction detection (detect-only).
        services.TryAddTransient<IConflictDetectionService, Neo4jConflictDetectionService>();

        // Memory decay / pruning — replace the Core portable no-op with the server-side Cypher
        // implementation. Replace (not TryAdd) because Core unconditionally registers its placeholder.
        services.Replace(ServiceDescriptor.Scoped<IMemoryDecayService, Neo4jMemoryDecayService>());

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
