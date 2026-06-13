using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentMemory.Analytics;

/// <summary>DI registration for the optional GDS analytics services.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the optional Neo4j GDS analytics services — <see cref="IMemoryPageRankService"/>,
    /// <see cref="IMemoryCommunityService"/>, and <see cref="IGdsAvailability"/>. Requires the Neo4j
    /// services (call <c>AddNeo4jAgentMemory()</c> first) for the transaction runner. Safe to register even
    /// without the GDS plugin installed — the services degrade to a graceful no-op (empty results).
    /// </summary>
    public static IServiceCollection AddGdsMemoryAnalytics(
        this IServiceCollection services, Action<GdsAnalyticsOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);
        else
            services.AddOptions<GdsAnalyticsOptions>();

        services.TryAddScoped<IGdsAvailability, GdsAvailability>();
        services.TryAddScoped<IMemoryPageRankService, MemoryPageRankService>();
        services.TryAddScoped<IMemoryCommunityService, MemoryCommunityService>();
        return services;
    }
}
