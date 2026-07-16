using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework.Recall;
using AgentMemory.Core.Services;

namespace AgentMemory.AgentFramework;

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
                ctx.MaxChatHistoryMessages = src.MaxChatHistoryMessages;
            })
            .Validate(o => o.MaxChatHistoryMessages >= 0, "ContextFormatOptions.MaxChatHistoryMessages must be non-negative.")
            .ValidateOnStart();

        // Task-aware automatic recall policy (#88). TryAdd: a host registering its own
        // IAutomaticRecallPolicy either before or after this call always wins over this default.
        services.TryAddScoped<IAutomaticRecallPolicy, ConfiguredAutomaticRecallPolicy>();

        services.TryAddScoped<Neo4jMemoryContextProvider>();
        services.TryAddScoped<Neo4jChatMessageStore>();
        services.TryAddScoped<Neo4jMicrosoftMemoryFacade>();

        // P2-6: Register AgentTraceRecorder and MemoryToolFactory so consumers don't need to add them manually.
        // Both are Scoped: they depend on scoped Core services and are not safe as singletons.
        services.TryAddScoped<AgentTraceRecorder>();
        // MemoryToolFactory delegates to the Core query facade; register it here so the factory resolves
        // even when only AddAgentMemoryFramework was called (TryAdd respects an existing Core registration).
        services.TryAddScoped<IMemoryQueryFacade, MemoryQueryFacade>();
        services.TryAddScoped<Tools.MemoryToolFactory>();

        // MAF 1.1.0 ChatHistoryProvider for plugging into ChatClientAgentOptions.ChatHistoryProvider.
        // Registered as a scoped concrete type; consumers wire it into their agent options explicitly.
        services.TryAddScoped<Neo4jChatHistoryProvider>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<AgentFrameworkOptions>>().Value;
            return ActivatorUtilities.CreateInstance<Neo4jChatHistoryProvider>(sp, opts);
        });

        return services;
    }
}
