using System.Diagnostics.CodeAnalysis;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Extensibility.Reading;
using AgentMemory.Neo4j.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace AgentMemory.Extensibility.Neo4j;

/// <summary>Binds statically composed modules to the host's Neo4j database (provider <c>neo4j-direct</c>).</summary>
/// <remarks>Uses the host's registered <see cref="INeo4jTransactionRunner"/>; a NAMS-shaped host has none and is refused at activation.</remarks>
[Experimental("AMEXT001")]
public sealed class Neo4jModuleStoreAdapter : IModuleStoreAdapter
{
    private readonly Neo4jModuleSchemaCapabilityProbe _probe = new();

    /// <inheritdoc/>
    public string Provider => _probe.Provider;

    /// <inheritdoc/>
    public IReadOnlyList<string> GetProblems(IServiceProvider services, string? serviceKey = null) => _probe.GetProblems(services, serviceKey);

    /// <inheritdoc/>
    public IModuleStore CreateStore(IServiceProvider services, ModuleResolution resolution) =>
        new Neo4jModuleStore(services.GetRequiredService<INeo4jTransactionRunner>(), resolution);

    /// <inheritdoc/>
    public IModuleReader CreateReader(IServiceProvider services, ModuleResolution resolution, string moduleId) =>
        new Neo4jModuleReader(services.GetRequiredService<INeo4jTransactionRunner>(), resolution, moduleId,
            services.GetRequiredService<IMemoryIsolationPolicy>(), services.GetService<TimeProvider>(), meaning: services.GetService<Storage.PackMeaning>());

    /// <inheritdoc/>
    public IModuleTraversalReader? CreateTraversalReader(IServiceProvider services, ModuleResolution resolution) =>
        new Neo4jModuleTraversalReader(services.GetRequiredService<INeo4jTransactionRunner>(), resolution,
            services.GetRequiredService<IMemoryIsolationPolicy>());
}

/// <summary>Knowledge packs in the host's Neo4j database (Extensibility 0.13 §K3).</summary>
[Experimental("AMEXT001")]
public static class Neo4jKnowledgePackServiceCollectionExtensions
{
    /// <summary>Registers <see cref="Packs.IKnowledgePackStore"/> on the host's Neo4j database and the <see cref="Packs.KnowledgePackImporter"/>.</summary>
    public static IServiceCollection AddNeo4jKnowledgePacks(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<Packs.IKnowledgePackStore>(sp =>
            new Neo4jKnowledgePackStore(sp.GetRequiredService<INeo4jTransactionRunner>(), sp.GetService<TimeProvider>(), sp.GetService<Storage.PackMeaning>()));
        services.AddSingleton(sp => new Packs.KnowledgePackImporter(sp.GetRequiredService<Packs.IKnowledgePackStore>(), sp.GetService<TimeProvider>()));
        // CMP-2a: host-generated snapshots, such as the code map of a repository at a commit.
        services.AddSingleton<Packs.IModuleSnapshotStore>(sp =>
            new Neo4jModuleSnapshotStore(sp.GetRequiredService<INeo4jTransactionRunner>(), sp.GetService<TimeProvider>()));
        return services;
    }

    /// <summary>
    /// Searches packs by meaning too (feature 19), with the host's registered embedder: imports embed the records a search view
    /// reads, and search views merge the nearest records with their full-text hits. Opt-in; without it a pack is found by words.
    /// </summary>
    public static IServiceCollection AddNeo4jPackMeaning(this IServiceCollection services, Storage.PackMeaningOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(sp => new Storage.PackMeaning(
            sp.GetRequiredService<Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>>>(), options));
        return services;
    }
}

/// <summary>Neo4j binding for <see cref="ModuleSetBuilder"/>.</summary>
[Experimental("AMEXT001")]
public static class Neo4jModuleSetBuilderExtensions
{
    /// <summary>Stores and reads module records in the host's Neo4j database.</summary>
    public static ModuleSetBuilder UseNeo4jStore(this ModuleSetBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseStore(new Neo4jModuleStoreAdapter());
    }
}
