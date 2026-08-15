using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework.Recall;
using AgentMemory.AgentFramework.Security;
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
                // Omitted until 2026-08-13, which made the documented procedural-memory recipe inert:
                // a host that set IncludeTraceOutcomes got a recalled procedure rendering its task and
                // dropping its outcome -- "you have done this before" and nothing about how. The
                // property existed, the option bound, and the bridge silently discarded it.
                ctx.IncludeTraceOutcomes = src.IncludeTraceOutcomes;
                ctx.IncludeWorkingMemory = src.IncludeWorkingMemory;
                ctx.ContextPrefix = src.ContextPrefix;
                // 25.3. Same lesson, one line down: EverySettablePropertyCrossesTheBridge caught this
                // omission the moment the property was added, which is precisely what that guard is for.
                ctx.ProcedureTrustClause = src.ProcedureTrustClause;
                ctx.MaxChatHistoryMessages = src.MaxChatHistoryMessages;
                ctx.SecurityMode = src.SecurityMode;
                ctx.MinimumTrustForAdmissionBypass = src.MinimumTrustForAdmissionBypass;
                ctx.DefaultMemoryRole = src.DefaultMemoryRole;
                ctx.MinimumTrustForSystemRole = src.MinimumTrustForSystemRole;
            })
            .Validate(o => o.MaxChatHistoryMessages >= 0, "ContextFormatOptions.MaxChatHistoryMessages must be non-negative.")
            // Stabilization fix: none of these four #92 enum knobs were previously range-checked.
            // IConfiguration's enum binder accepts any integer for a numeric enum, not just defined
            // members, so e.g. "MinimumTrustForAdmissionBypass": 99 would silently bind to an undefined
            // MemoryTrustLevel and change bypass comparisons instead of failing at startup.
            .Validate(o => Enum.IsDefined(typeof(MemoryContextSecurityMode), o.SecurityMode),
                "ContextFormatOptions.SecurityMode must be a defined MemoryContextSecurityMode value.")
            .Validate(o => Enum.IsDefined(typeof(MemoryTrustLevel), o.MinimumTrustForAdmissionBypass),
                "ContextFormatOptions.MinimumTrustForAdmissionBypass must be a defined MemoryTrustLevel value.")
            .Validate(o => Enum.IsDefined(typeof(RecalledMemoryMessageRole), o.DefaultMemoryRole),
                "ContextFormatOptions.DefaultMemoryRole must be a defined RecalledMemoryMessageRole value.")
            .Validate(o => Enum.IsDefined(typeof(MemoryTrustLevel), o.MinimumTrustForSystemRole),
                "ContextFormatOptions.MinimumTrustForSystemRole must be a defined MemoryTrustLevel value.")
            .ValidateOnStart();

        // Task-aware automatic recall policy (#88). TryAdd: a host registering its own
        // IAutomaticRecallPolicy either before or after this call always wins over this default.
        //
        // Default changed to TrivialTurnRecallPolicy: identical to ConfiguredAutomaticRecallPolicy on
        // every real turn, and recent-messages-only on a greeting/acknowledgement-only one. Measured on
        // PERF-R-01, a greeting turn previously cost 13 Cypher queries, 12 read transactions and an
        // embedding round trip to retrieve 11 items, 10 of which need no vector at all.
        // A host wanting the previous behaviour registers it explicitly:
        //   services.AddScoped<IAutomaticRecallPolicy, ConfiguredAutomaticRecallPolicy>();
        services.TryAddScoped<IAutomaticRecallPolicy, TrivialTurnRecallPolicy>();

        // Memory-context admission policy (#92 Phase 2). TryAdd: same override semantics as above.
        services.TryAddScoped<IMemoryContextAdmissionPolicy, DefaultMemoryContextAdmissionPolicy>();

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
