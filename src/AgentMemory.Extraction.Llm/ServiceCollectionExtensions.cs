using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Extraction.Llm;

/// <summary>
/// DI registration helpers for the LLM extraction services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers LLM-backed extractors and their options.
    /// </summary>
    public static IServiceCollection AddLlmExtraction(
        this IServiceCollection services,
        Action<LlmExtractionOptions>? configure = null)
    {
        var llmOptions = configure is not null
            ? services.AddOptions<LlmExtractionOptions>().Configure(configure)
            : services.AddOptions<LlmExtractionOptions>();
        llmOptions
            .Validate(o => o.Temperature >= 0.0f, "LlmExtraction Temperature must be non-negative.")
            .Validate(o => o.MaxRetries >= 0, "LlmExtraction MaxRetries must be non-negative.")
            .Validate(o => o.MaxConcurrentBatchesPerExtraction > 0,
                "LlmExtraction MaxConcurrentBatchesPerExtraction must be positive.")
            .Validate(o => o.MaxConcurrentExtractionBatches >= 0,
                "LlmExtraction MaxConcurrentExtractionBatches must be non-negative.")
            .ValidateOnStart();

        // Replace (not TryAdd) so the real extractors authoritatively override the Core no-op stub
        // extractors — AddAgentMemoryCore registers StubEntityExtractor et al. via TryAddScoped FIRST
        // (the meta package calls AddAgentMemoryCore before AddLlmExtraction), so a TryAdd here would be a
        // silent no-op and leave LLM extraction inert. This mirrors how the Neo4j package Replaces the
        // Core portable IMemoryDecayService no-op. Replace also works standalone (adds when none present).
        services.Replace(ServiceDescriptor.Scoped<IEntityExtractor, LlmEntityExtractor>());
        services.Replace(ServiceDescriptor.Scoped<IFactExtractor, LlmFactExtractor>());
        services.Replace(ServiceDescriptor.Scoped<IPreferenceExtractor, LlmPreferenceExtractor>());
        services.Replace(ServiceDescriptor.Scoped<IRelationshipExtractor, LlmRelationshipExtractor>());
        services.TryAddSingleton<LlmExtractionBatchDiagnostics>();
        services.TryAddScoped<IUnifiedMemoryExtractor, LlmUnifiedMemoryExtractor>();
        services.TryAddSingleton<LlmExtractionBatchConcurrencyLimiter>();
        services.TryAddScoped<IMultiSessionUnifiedMemoryExtractor, LlmMultiSessionUnifiedMemoryExtractor>();

        return services;
    }
}
