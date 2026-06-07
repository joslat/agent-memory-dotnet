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
            .ValidateOnStart();

        services.TryAddScoped<IEntityExtractor, LlmEntityExtractor>();
        services.TryAddScoped<IFactExtractor, LlmFactExtractor>();
        services.TryAddScoped<IPreferenceExtractor, LlmPreferenceExtractor>();
        services.TryAddScoped<IRelationshipExtractor, LlmRelationshipExtractor>();

        return services;
    }
}
