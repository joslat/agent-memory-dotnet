using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Neo4j.AgentMemory.AgentFramework;

/// <summary>
/// Dependency injection extensions for the Agent Memory Framework adapter.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all Agent Memory Framework adapter services.
    /// </summary>
    public static IServiceCollection AddAgentMemoryFramework(
        this IServiceCollection services,
        Action<AgentFrameworkOptions>? configure = null)
    {
        if (configure is not null)
            services.AddOptions<AgentFrameworkOptions>().Configure(configure);
        else
            services.AddOptions<AgentFrameworkOptions>();

        services.AddOptions<ContextFormatOptions>()
            .Configure<IOptions<AgentFrameworkOptions>>((ctx, af) =>
            {
                var src = af.Value.ContextFormat;
                ctx.IncludeEntities = src.IncludeEntities;
                ctx.IncludeFacts = src.IncludeFacts;
                ctx.IncludePreferences = src.IncludePreferences;
                ctx.IncludeReasoningTraces = src.IncludeReasoningTraces;
                ctx.ContextPrefix = src.ContextPrefix;
                ctx.MaxContextMessages = src.MaxContextMessages;
            });

        services.TryAddScoped<Neo4jMemoryContextProvider>();
        services.TryAddScoped<Neo4jChatMessageStore>();
        services.TryAddScoped<Neo4jMicrosoftMemoryFacade>();

        return services;
    }
}
