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
            services.GetRequiredService<IMemoryIsolationPolicy>(), services.GetService<TimeProvider>());
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
