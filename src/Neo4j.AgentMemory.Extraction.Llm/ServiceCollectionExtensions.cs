using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Extraction.Llm;

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
        if (configure is not null)
            services.AddOptions<LlmExtractionOptions>().Configure(configure);
        else
            services.AddOptions<LlmExtractionOptions>();

        services.TryAddScoped<IEntityExtractor, LlmEntityExtractor>();
        services.TryAddScoped<IFactExtractor, LlmFactExtractor>();
        services.TryAddScoped<IPreferenceExtractor, LlmPreferenceExtractor>();
        services.TryAddScoped<IRelationshipExtractor, LlmRelationshipExtractor>();

        return services;
    }
}
