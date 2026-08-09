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
    /// Registers the full Neo4j-backed memory stack from a fully-constructed
    /// <see cref="MemoryOptions"/>.
    /// </summary>
    /// <remarks>
    /// Prefer this over the <c>Action&lt;MemoryOptions&gt;</c> overload, which cannot configure
    /// anything: <see cref="MemoryOptions"/> is a record with <c>init</c>-only properties, so a
    /// configure lambda can neither assign them nor keep a <c>with</c> expression's result. See
    /// <see cref="AgentMemory.Core.ServiceCollectionExtensions.AddAgentMemoryCore(IServiceCollection, MemoryOptions)"/>.
    /// <para>
    /// Binary compatibility is unaffected. There is one narrow source-level consequence: an untyped
    /// <c>null</c> as the second argument now converts to both this overload and the lambda one, so
    /// <c>AddNeo4jAgentMemory(null!, ...)</c> becomes ambiguous and needs an explicit
    /// <c>(Action&lt;MemoryOptions&gt;)</c> cast. That shape appears once in this repository, in a
    /// null-guard test; it is not something production code writes.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddNeo4jAgentMemory(
        this IServiceCollection services,
        MemoryOptions memoryOptions,
        Action<NeoInfra.Neo4jOptions> configureNeo4j,
        Action<LlmExtractionOptions>? configureLlm = null,
        Action<NeoInfra.MemoryStoreOptions>? configureStore = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(memoryOptions);
        ArgumentNullException.ThrowIfNull(configureNeo4j);

        services.AddAgentMemoryCore(memoryOptions);
        NeoInfra.ServiceCollectionExtensions.AddNeo4jAgentMemory(services, configureNeo4j, configureStore);
        if (configureLlm is not null)
            services.AddLlmExtraction(configureLlm);

        return services;
    }

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

        // Only wire LLM-backed extraction when the caller OPTS IN (configureLlm provided). Otherwise the
        // Core no-op stub extractors remain, so a memory-only consumer does not need to register an
        // IChatClient just to resolve the extraction pipeline. When opted in, AddLlmExtraction
        // authoritatively Replaces the stubs (a TryAdd would be a no-op since the stubs are registered first).
        if (configureLlm is not null)
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
        Action<WikimediaEnrichmentOptions>? configureEnrichment = null,
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
