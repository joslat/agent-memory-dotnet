using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Core.Extraction;
using Neo4j.AgentMemory.Core.Resolution;
using Neo4j.AgentMemory.Core.Services;
using Neo4j.AgentMemory.Core.Stubs;

namespace Neo4j.AgentMemory.Core;

/// <summary>
/// Extension methods for registering Core memory services with the DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all Core memory services.
    /// Adapters (repositories, IEmbeddingGenerator, etc.) must be registered separately.
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

        services.TryAddSingleton<IOptions<ExtractionOptions>>(sp =>
            Options.Create(sp.GetRequiredService<IOptions<MemoryOptions>>().Value.Extraction));

        // Core services
        services.TryAddSingleton<ISessionIdGenerator, SessionIdGenerator>();
        services.TryAddScoped<IShortTermMemoryService, ShortTermMemoryService>();
        services.TryAddScoped<ILongTermMemoryService, LongTermMemoryService>();
        services.TryAddScoped<IReasoningMemoryService, ReasoningMemoryService>();
        services.TryAddScoped<IMemoryContextAssembler, MemoryContextAssembler>();
        services.TryAddScoped<IMemoryService, MemoryService>();

        // Entity resolution — CompositeEntityResolver replaces StubEntityResolver.
        // Callers may override by registering their own IEntityResolver before calling this method.
        services.TryAddScoped<IEntityResolver, CompositeEntityResolver>();

        // Keep StubEntityResolver available for explicit fallback use.
        services.TryAddScoped<StubEntityResolver>();

        // Extraction pipeline stages.
        // IExtractionStage receives IEnumerable<IExtractor> — all registered extractor implementations.
        services.TryAddScoped<IExtractionStage, ExtractionStage>();
        services.TryAddScoped<IPersistenceStage, PersistenceStage>();

        // Unified extraction pipeline — composes the two stages.
        services.TryAddScoped<IMemoryExtractionPipeline, MemoryExtractionPipeline>();

        // Embedding orchestrator — centralizes embedding generation logic.
        services.TryAddScoped<IEmbeddingOrchestrator, EmbeddingOrchestrator>();

        // Memory decay service — scoring and pruning of stale memories.
        services.TryAddSingleton<IOptions<MemoryDecayOptions>>(sp =>
            Options.Create(sp.GetRequiredService<IOptions<MemoryOptions>>().Value.MemoryDecay));
        services.TryAddScoped<IMemoryDecayService, MemoryDecayService>();

        // Stub extractors as no-op defaults; replaced when AddLlmExtraction() is called.
        services.TryAddScoped<IEntityExtractor, StubEntityExtractor>();
        services.TryAddScoped<IFactExtractor, StubFactExtractor>();
        services.TryAddScoped<IPreferenceExtractor, StubPreferenceExtractor>();
        services.TryAddScoped<IRelationshipExtractor, StubRelationshipExtractor>();

        return services;
    }
}

