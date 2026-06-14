using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Resolution;
using AgentMemory.Core.Services;
using AgentMemory.Core.Services.Budgeting;
using AgentMemory.Core.Stubs;

namespace AgentMemory.Core;

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
        // Sensible defaults for the two ambient primitives that many services depend on (assembler,
        // reasoning, consolidation, dedup). TryAdd so a consumer can still register their own first.
        // Without these the meta package wasn't self-sufficient — every sample had to register them by hand.
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IIdGenerator, GuidIdGenerator>();
        services.TryAddSingleton<ISessionIdGenerator, SessionIdGenerator>();

        // Ambient owner context (IC8) — AsyncLocal-backed, safe as a singleton; set per request/agent
        // flow by adapters so the LLM-invokable facade tools scope by owner without trusting the model.
        services.TryAddSingleton<DefaultMemoryOwnerContext>();
        services.TryAddSingleton<IMemoryOwnerContext>(sp => sp.GetRequiredService<DefaultMemoryOwnerContext>());
        services.TryAddSingleton<IWritableMemoryOwnerContext>(sp => sp.GetRequiredService<DefaultMemoryOwnerContext>());

        // Per-request ranking override (D3 query-intent presets). AsyncLocal-backed singleton, set by the
        // context assembler from RecallOptions.Intent and read by the long-term repositories' vector search.
        services.TryAddSingleton<DefaultMemoryRankingContext>();
        services.TryAddSingleton<IMemoryRankingContext>(sp => sp.GetRequiredService<DefaultMemoryRankingContext>());
        services.TryAddSingleton<IWritableMemoryRankingContext>(sp => sp.GetRequiredService<DefaultMemoryRankingContext>());
        services.TryAddScoped<IShortTermMemoryService, ShortTermMemoryService>();
        services.TryAddScoped<ILongTermMemoryService, LongTermMemoryService>();
        services.TryAddScoped<IReasoningMemoryService, ReasoningMemoryService>();

        // Context-budget truncation strategies (S9). Registered as an enumerable so a consumer can add or
        // replace a strategy for a given TruncationStrategy value; the assembler always falls back to its
        // four built-ins, so these registrations are an override surface rather than a requirement.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITruncationStrategy, OldestFirstTruncationStrategy>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITruncationStrategy, LowestScoreFirstTruncationStrategy>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITruncationStrategy, ProportionalTruncationStrategy>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITruncationStrategy, FailTruncationStrategy>());

        // GraphRAG is an optional source: resolve it with GetService so memory-only consumers
        // (who never call AddGraphRagAdapter) don't fail to construct the assembler. When the
        // GraphRAG adapter IS registered, that instance (incl. any observability decorator) is used.
        services.TryAddScoped<IMemoryContextAssembler>(sp => new MemoryContextAssembler(
            sp.GetRequiredService<IShortTermMemoryService>(),
            sp.GetRequiredService<ILongTermMemoryService>(),
            sp.GetRequiredService<IReasoningMemoryService>(),
            sp.GetService<IGraphRagContextSource>(),
            sp.GetRequiredService<IEmbeddingOrchestrator>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<IOptions<MemoryOptions>>(),
            sp.GetRequiredService<ILogger<MemoryContextAssembler>>(),
            rankingContext: null,
            truncationStrategies: sp.GetServices<ITruncationStrategy>()));
        services.TryAddScoped<IMemoryService, MemoryService>();

        // Role interfaces (ISP): bind each to the same scoped IMemoryService instance so consumers
        // can depend on a narrow contract (recall / ingestion / maintenance) without a second object.
        services.TryAddScoped<IMemoryRecall>(sp => sp.GetRequiredService<IMemoryService>());
        services.TryAddScoped<IMemoryIngestion>(sp => sp.GetRequiredService<IMemoryService>());
        services.TryAddScoped<IMemoryMaintenance>(sp => sp.GetRequiredService<IMemoryService>());

        // Render-ready query/command facade shared by framework adapters (MAF tools, SK plugin).
        services.TryAddScoped<IMemoryQueryFacade, MemoryQueryFacade>();

        // Entity resolution — CompositeEntityResolver replaces StubEntityResolver.
        // Callers may override by registering their own IEntityResolver before calling this method.
        services.TryAddScoped<IEntityResolver, CompositeEntityResolver>();

        // Keep StubEntityResolver available for explicit fallback use.
        services.TryAddScoped<StubEntityResolver>();

        // Extraction pipeline stages.
        // IExtractionStage receives IEnumerable<IExtractor> — all registered extractor implementations.
        services.TryAddScoped<IExtractionStage, ExtractionStage>();
        services.TryAddScoped<IPersistenceStage, PersistenceStage>();

        // Streaming (chunked) extraction (R4). The extractor is a pure text→chunks→entities helper; it
        // does NOT persist, so it carries no owner context itself — owner stamping (R1) happens when its
        // output is persisted via PersistenceStage with ExtractionRequest.UserId. Registered now that the
        // isolation surface has landed; was intentionally held back until then.
        services.TryAddScoped<IStreamingExtractor, Extraction.Streaming.StreamingExtractor>();

        // Unified extraction pipeline — composes the two stages. Registered via a factory because
        // MemoryExtractionPipeline's constructor is internal (its stage parameters are internal types),
        // which the default DI activator (public ctors only) cannot select.
        services.TryAddScoped<IMemoryExtractionPipeline>(sp => new MemoryExtractionPipeline(
            sp.GetRequiredService<IExtractionStage>(),
            sp.GetRequiredService<IPersistenceStage>(),
            sp.GetRequiredService<ILogger<MemoryExtractionPipeline>>()));

        // Embedding orchestrator — centralizes embedding generation logic.
        services.TryAddScoped<IEmbeddingOrchestrator, EmbeddingOrchestrator>();

        // Memory decay service — scoring and pruning of stale memories.
        services.TryAddSingleton<IOptions<MemoryDecayOptions>>(sp =>
            Options.Create(sp.GetRequiredService<IOptions<MemoryOptions>>().Value.MemoryDecay));
        services.TryAddScoped<IMemoryDecayService, MemoryDecayService>();

        // Retrieval-ranking options (recency re-ranker / structural decay; opt-in, schema-neutral).
        // Consumed by the long-term repositories' SearchByVector and the GraphRAG traversal.
        services.TryAddSingleton<IOptions<MemoryRankingOptions>>(sp =>
            Options.Create(sp.GetRequiredService<IOptions<MemoryOptions>>().Value.Ranking));

        // Stub extractors as no-op defaults; replaced when AddLlmExtraction() is called.
        services.TryAddScoped<IEntityExtractor, StubEntityExtractor>();
        services.TryAddScoped<IFactExtractor, StubFactExtractor>();
        services.TryAddScoped<IPreferenceExtractor, StubPreferenceExtractor>();
        services.TryAddScoped<IRelationshipExtractor, StubRelationshipExtractor>();

        return services;
    }
}

