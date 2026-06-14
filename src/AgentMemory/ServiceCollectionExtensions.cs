using Microsoft.Extensions.DependencyInjection;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core;
using AgentMemory.Enrichment;
using AgentMemory.Extraction.AzureLanguage;
using AgentMemory.Extraction.Llm;
using AgentMemory.Observability;
using NeoInfra = AgentMemory.Neo4j.Infrastructure;

namespace AgentMemory;

/// <summary>
/// Convenience DI registration for the full Neo4j Agent Memory stack.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all core, Neo4j infrastructure, and LLM extraction services in one call.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configureMemory">Configures core memory options.</param>
    /// <param name="configureNeo4j">Configures Neo4j connection options.</param>
    /// <param name="configureLlm">Optional: configures LLM extraction options.</param>
    /// <param name="configureStore">
    /// Optional: configures the application / memory-store isolation tier (R1b) — e.g.
    /// <c>MemoryStorageStrategy.DatabasePerApplication</c> to route each <c>ApplicationId</c> to its own
    /// Neo4j database (requires Enterprise/AuraDB). Defaults to <c>SharedDatabase</c> (single database,
    /// owner-scoped), which reproduces the original single-store behavior.
    /// </param>
    public static IServiceCollection AddNeo4jAgentMemory(
        this IServiceCollection services,
        Action<MemoryOptions> configureMemory,
        Action<NeoInfra.Neo4jOptions> configureNeo4j,
        Action<LlmExtractionOptions>? configureLlm = null,
        Action<NeoInfra.MemoryStoreOptions>? configureStore = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureMemory);
        ArgumentNullException.ThrowIfNull(configureNeo4j);

        services.AddAgentMemoryCore(configureMemory);
        NeoInfra.ServiceCollectionExtensions.AddNeo4jAgentMemory(services, configureNeo4j, configureStore);
        services.AddLlmExtraction(configureLlm);

        return services;
    }

    /// <summary>
    /// Opt-in: adds OpenTelemetry-based metrics and instruments the memory service. Chain after
    /// <see cref="AddNeo4jAgentMemory"/>.
    /// </summary>
    public static IServiceCollection WithObservability(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddAgentMemoryObservability();
    }

    /// <summary>
    /// Opt-in: adds geocoding + entity enrichment services (Nominatim geocoding, Wikimedia/Diffbot
    /// enrichment) with rate-limiting and caching decorators. Chain after <see cref="AddNeo4jAgentMemory"/>.
    /// </summary>
    public static IServiceCollection WithEnrichment(
        this IServiceCollection services,
        Action<GeocodingOptions>? configureGeocoding = null,
        Action<EnrichmentOptions>? configureEnrichment = null,
        Action<EnrichmentCacheOptions>? configureCaching = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddEnrichmentServices(configureGeocoding, configureEnrichment, configureCaching);
    }

    /// <summary>
    /// Opt-in: adds Azure AI Language-backed extractors as an alternative/supplement to the LLM
    /// extractors. Chain after <see cref="AddNeo4jAgentMemory"/>.
    /// </summary>
    public static IServiceCollection WithAzureLanguageExtraction(
        this IServiceCollection services,
        Action<AzureLanguageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        return services.AddAzureLanguageExtraction(configure);
    }
}
