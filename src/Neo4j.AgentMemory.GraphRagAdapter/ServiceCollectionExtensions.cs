using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.GraphRagAdapter;

/// <summary>
/// DI registration extensions for the GraphRAG adapter.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="Neo4jGraphRagContextSource"/> as the <see cref="IGraphRagContextSource"/>
    /// implementation and configures <see cref="GraphRagAdapterOptions"/>.
    /// </summary>
    public static IServiceCollection AddGraphRagAdapter(
        this IServiceCollection services,
        Action<GraphRagAdapterOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<GraphRagAdapterOptions>().Configure(configure);
        services.TryAddScoped<IGraphRagContextSource, Neo4jGraphRagContextSource>();
        return services;
    }
}
