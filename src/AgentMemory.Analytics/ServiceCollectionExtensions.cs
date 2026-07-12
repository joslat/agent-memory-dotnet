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
        var optionsBuilder = services.AddOptions<GdsAnalyticsOptions>();
        if (configure is not null)
            optionsBuilder.Configure(configure);
        // A non-positive DefaultTopN flows straight into a Neo4j LIMIT and errors at query time; fail fast at
        // startup instead (per-call topN is guarded separately in MemoryPageRankService.RankEntitiesAsync).
        optionsBuilder
            .Validate(o => o.DefaultTopN > 0, "GdsAnalyticsOptions.DefaultTopN must be greater than 0.")
            .ValidateOnStart();

        services.TryAddScoped<IGdsAvailability, GdsAvailability>();
        services.TryAddScoped<IMemoryPageRankService, MemoryPageRankService>();
        services.TryAddScoped<IMemoryCommunityService, MemoryCommunityService>();
        return services;
    }
}
