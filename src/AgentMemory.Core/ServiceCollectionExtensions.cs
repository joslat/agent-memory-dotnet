using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Memory;
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
    /// Registers all Core memory services from a fully-constructed <see cref="MemoryOptions"/>.
    /// </summary>
    /// <remarks>
    /// The <c>Action&lt;MemoryOptions&gt;</c> overload cannot configure anything.
    /// <see cref="MemoryOptions"/> is a record whose properties are all <c>init</c>-only, so a
    /// configure lambda cannot assign them — <c>options.EnableGraphRag = true</c> is a compile error,
    /// and the <c>options = options with { ... }</c> form that does compile rebinds the parameter
    /// local and is discarded the moment the lambda returns. Code written that way builds, runs, and
    /// silently keeps every default.
    /// <para>
    /// This overload takes the instance directly, which is how every working call site in this
    /// repository already builds options, and applies the same validators the other overload
    /// registers — a supplied instance is checked, not trusted.
    /// </para>
    /// <para>
    /// Added rather than changing the properties to <c>set</c>: that would alter the setter signature
    /// of a public type on a SemVer-locked surface and break binary compatibility for anyone already
    /// compiled against it. This is purely additive.
    /// </para>
    /// <para>
    /// <b>Limitation, stated rather than left to be discovered.</b> This replaces the registration of
    /// <see cref="IOptions{TOptions}"/> only. A host resolving <c>IOptionsMonitor&lt;MemoryOptions&gt;</c>
    /// or <c>IOptionsSnapshot&lt;MemoryOptions&gt;</c> would still go through the options factory and
    /// receive defaults. Nothing in this product resolves either — every consumer takes
    /// <c>IOptions&lt;MemoryOptions&gt;</c> — but a host that does would see two different values for
    /// the same options type, which is worth knowing before it happens rather than after.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddAgentMemoryCore(
        this IServiceCollection services,
        MemoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        // Register everything the lambda overload does, including the validator chain, then take over
        // the resolution of IOptions<MemoryOptions>. An exact closed-generic registration wins over
        // the open IOptions<> one, so the supplied instance is what every consumer resolves.
        services.AddAgentMemoryCore(_ => { });
        services.AddSingleton<IOptions<MemoryOptions>>(serviceProvider =>
        {
            // The validators are the whole reason this is a factory rather than Options.Create: an
            // instance that skipped them would fail closed nowhere and misconfigure silently at the
            // first affected call, which is the exact failure the validator chain exists to prevent.
            var failures = serviceProvider
                .GetServices<IValidateOptions<MemoryOptions>>()
                .Select(validator => validator.Validate(Microsoft.Extensions.Options.Options.DefaultName, options))
                .Where(result => result.Failed)
                // Failures is only guaranteed non-null once Failed is true, which the Where above
                // establishes but the compiler cannot see.
                .SelectMany(result => result.Failures ?? [])
                .ToArray();
            if (failures.Length > 0)
            {
                throw new OptionsValidationException(
                    Microsoft.Extensions.Options.Options.DefaultName, typeof(MemoryOptions), failures);
            }

            return Microsoft.Extensions.Options.Options.Create(options);
        });

        return services;
    }

    /// <summary>
    /// Registers all Core memory services.
    /// Adapters (repositories, IEmbeddingGenerator, etc.) must be registered separately.
    /// </summary>
    public static IServiceCollection AddAgentMemoryCore(
        this IServiceCollection services,
        Action<MemoryOptions> configure)
    {
        // Configure root options
        // Stabilization fix: this tree of options previously had zero DI-time validation. A malformed
        // Isolation.Mode (e.g. an out-of-range int bound from JSON config) fell through
        // DefaultMemoryIsolationPolicy's switch to the most permissive (SingleTenant) behavior instead of
        // failing closed; numeric thresholds meant to be [0,1] or positive could silently misconfigure
        // deduplication/feedback/query-limit behavior with no error until the first affected call.
        services.AddOptions<MemoryOptions>()
            .Configure(configure)
            .Validate(
                o => Enum.IsDefined(typeof(MemoryIsolationMode), o.Isolation.Mode),
                "MemoryOptions.Isolation.Mode must be a defined MemoryIsolationMode value.")
            .Validate(
                o => o.LongTerm.MinConfidenceThreshold is >= 0 and <= 1,
                "MemoryOptions.LongTerm.MinConfidenceThreshold must be between 0 and 1.")
            .Validate(
                o => o.LongTerm.DeduplicationSimilarityThreshold is >= 0 and <= 1,
                "MemoryOptions.LongTerm.DeduplicationSimilarityThreshold must be between 0 and 1.")
            .Validate(
                o => o.LongTerm.DeduplicationConfidenceBump is >= 0 and <= 1,
                "MemoryOptions.LongTerm.DeduplicationConfidenceBump must be between 0 and 1.")
            .Validate(
                o => o.LongTerm.FeedbackConfidenceDelta is >= 0 and <= 1,
                "MemoryOptions.LongTerm.FeedbackConfidenceDelta must be between 0 and 1.")
            .Validate(
                o => o.ShortTerm.DefaultRecentMessageLimit > 0,
                "MemoryOptions.ShortTerm.DefaultRecentMessageLimit must be positive.")
            .Validate(
                o => o.ShortTerm.MaxMessagesPerQuery > 0,
                "MemoryOptions.ShortTerm.MaxMessagesPerQuery must be positive.")
            .Validate(
                o => o.ShortTerm.DefaultRecentMessageLimit <= o.ShortTerm.MaxMessagesPerQuery,
                "MemoryOptions.ShortTerm.DefaultRecentMessageLimit must not exceed MaxMessagesPerQuery.")
            .Validate(
                o => o.Reasoning.MaxTracesPerSession is null or > 0,
                "MemoryOptions.Reasoning.MaxTracesPerSession must be positive when set.")
            .Validate(
                o => o.Extraction.AutoMergeThreshold is >= 0 and <= 1,
                "MemoryOptions.Extraction.AutoMergeThreshold must be between 0 and 1.")
            .Validate(
                o => o.Extraction.SameAsThreshold is >= 0 and <= 1,
                "MemoryOptions.Extraction.SameAsThreshold must be between 0 and 1.")
            .Validate(
                o => o.Extraction.SameAsThreshold <= o.Extraction.AutoMergeThreshold,
                "MemoryOptions.Extraction.SameAsThreshold must not exceed AutoMergeThreshold.")
            .ValidateOnStart();

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

        services.TryAddSingleton<IOptions<MemoryIsolationOptions>>(sp =>
            Options.Create(sp.GetRequiredService<IOptions<MemoryOptions>>().Value.Isolation));

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

        // Centralized isolation-mode enforcement (#100). Stateless decision logic driven purely by
        // MemoryIsolationOptions -- no scoped dependencies, so it's a singleton like the ambient contexts above.
        services.TryAddSingleton<IMemoryIsolationPolicy, DefaultMemoryIsolationPolicy>();

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
            sp.GetRequiredService<IMemoryIsolationPolicy>(),
            // Hand the assembler the SAME AsyncLocal ranking context the long-term repositories read
            // (registered above as both IMemoryRankingContext and IWritableMemoryRankingContext). Without
            // this the assembler can never publish the D3 per-request query intent, so RecallOptions.Intent
            // (Latest/Analog) is silently inert in every DI-wired deployment.
            rankingContext: sp.GetService<IWritableMemoryRankingContext>(),
            truncationStrategies: sp.GetServices<ITruncationStrategy>(),
            rerankers: sp.GetServices<IMemoryReranker>(),
            // 30.2. Enumerable and unconditional, the reranker pattern: every feature is registered,
            // every feature reads its own flag, and every flag is off by default. Gating registration
            // instead would mean a host that enables projection through IOptions reconfiguration still
            // gets nothing -- silently, which is how both rerankers shipped registered by nobody.
            projectionFeatures: sp.GetServices<Services.Projection.IProjectionFeature>()));
        services.TryAddScoped<IMemoryService, MemoryService>();

        // The five projection features (30.2), registered unconditionally and enumerably.
        //
        // The three that read go through GetService, not GetRequiredService, and take a NULLABLE
        // repository. This is not defensive style, it is a resolvability requirement: a consumer may
        // register Core with their OWN ILongTermMemoryService and no repositories at all -- a shape that
        // exists in this repository's own tests and worked before this feature -- and a hard dependency
        // inside an enumerable registration makes the WHOLE IEnumerable<IProjectionFeature>
        // unresolvable, taking the assembler down with it. That is the same break an unconditional
        // binding with an unsatisfiable dependency caused during the 1.0 lockdown. Each feature reports
        // itself off when its repository is absent, which is more honest than accepting the flag and
        // contributing nothing.
        services.TryAddEnumerable(ServiceDescriptor.Scoped<
            Services.Projection.IProjectionFeature, Services.Projection.MatchQualityProjectionFeature>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<
            Services.Projection.IProjectionFeature, Services.Projection.ConflictProjectionFeature>());
        // The two-type-parameter factory overload, not the one-type one: TryAddEnumerable de-duplicates
        // by IMPLEMENTATION type, and a bare factory records the service type as its implementation,
        // which makes every entry "indistinguishable" and throws.
        services.TryAddEnumerable(ServiceDescriptor
            .Scoped<Services.Projection.IProjectionFeature, Services.Projection.SupersessionProjectionFeature>(
                sp => new Services.Projection.SupersessionProjectionFeature(
                    sp.GetService<Abstractions.Repositories.IFactRepository>())));
        services.TryAddEnumerable(ServiceDescriptor
            .Scoped<Services.Projection.IProjectionFeature, Services.Projection.SourceQuoteProjectionFeature>(
                sp => new Services.Projection.SourceQuoteProjectionFeature(
                    sp.GetService<Abstractions.Repositories.IMessageRepository>())));
        services.TryAddEnumerable(ServiceDescriptor
            .Scoped<Services.Projection.IProjectionFeature, Services.Projection.DateGroundingProjectionFeature>(
                sp => new Services.Projection.DateGroundingProjectionFeature(
                    sp.GetService<Abstractions.Repositories.IMessageRepository>())));

        // Context compressor (reflection/observation summarization). It uses an IChatClient when one is
        // registered and degrades to a verbatim passthrough when not, so this binding is always safe to
        // resolve. Without it, IContextCompressor consumers — e.g. the MCP memory-observations tool — fail
        // to resolve at runtime (it was previously injected but registered by no AddX).
        services.TryAddScoped<IContextCompressor, ContextCompressor>();

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

        // S1. The deterministic synthesizer is the default because a summary is regenerated every
        // time any of its sources moves; a completion per entity per change would scale cost with
        // exactly how much the conversation talks about its subjects. TryAdd, so a host wanting
        // prose registers an LLM synthesizer before calling this and keeps it.
        services.TryAddScoped<IEntitySummarySynthesizer, DeterministicEntitySummarySynthesizer>();
        services.TryAddScoped<IEntitySummaryService, EntitySummaryService>();

        // Keep StubEntityResolver available for explicit fallback use.
        services.TryAddScoped<StubEntityResolver>();

        // Extraction pipeline stages.
        // IExtractionStage receives IEnumerable<IExtractor> — all registered extractor implementations.
        services.TryAddSingleton<IMemoryPersistenceTransaction, PassThroughMemoryPersistenceTransaction>();
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
            sp.GetRequiredService<ILogger<MemoryExtractionPipeline>>(),
            sp.GetRequiredService<IMemoryIsolationPolicy>(),
            sp.GetService<IOptions<ExtractionOptions>>(),
            sp.GetServices<IMultiSessionUnifiedMemoryExtractor>()));

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

