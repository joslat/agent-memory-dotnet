using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Core.Services;

namespace Neo4j.AgentMemory.Core;

/// <summary>
/// Extension methods for registering Core memory services with the DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all Core memory services.
    /// Adapters (repositories, IEmbeddingProvider, etc.) must be registered separately.
    /// </summary>
    public static IServiceCollection AddAgentMemoryCore(
        this IServiceCollection services,
        Action<MemoryOptions> configure)
    {
        // Configure root options
        services.AddOptions<MemoryOptions>().Configure(configure);

        // Bridge sub-options from parent MemoryOptions so services that depend on
        // IOptions<ShortTermMemoryOptions> etc. receive the values configured on MemoryOptions.
        services.TryAddSingleton<IOptions<ShortTermMemoryOptions>>(sp =>
            Options.Create(sp.GetRequiredService<IOptions<MemoryOptions>>().Value.ShortTerm));

        services.TryAddSingleton<IOptions<LongTermMemoryOptions>>(sp =>
            Options.Create(sp.GetRequiredService<IOptions<MemoryOptions>>().Value.LongTerm));

        services.TryAddSingleton<IOptions<ReasoningMemoryOptions>>(sp =>
            Options.Create(sp.GetRequiredService<IOptions<MemoryOptions>>().Value.Reasoning));

        // Core services
        services.TryAddScoped<IShortTermMemoryService, ShortTermMemoryService>();
        services.TryAddScoped<ILongTermMemoryService, LongTermMemoryService>();
        services.TryAddScoped<IReasoningMemoryService, ReasoningMemoryService>();
        services.TryAddScoped<IMemoryContextAssembler, MemoryContextAssembler>();
        services.TryAddScoped<IMemoryService, MemoryService>();

        return services;
    }
}
