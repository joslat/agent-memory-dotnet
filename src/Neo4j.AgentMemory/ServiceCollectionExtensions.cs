using Microsoft.Extensions.DependencyInjection;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Core;
using Neo4j.AgentMemory.Extraction.Llm;
using NeoInfra = Neo4j.AgentMemory.Neo4j.Infrastructure;

namespace Neo4j.AgentMemory;

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
    public static IServiceCollection AddNeo4jAgentMemory(
        this IServiceCollection services,
        Action<MemoryOptions> configureMemory,
        Action<NeoInfra.Neo4jOptions> configureNeo4j,
        Action<LlmExtractionOptions>? configureLlm = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureMemory);
        ArgumentNullException.ThrowIfNull(configureNeo4j);

        services.AddAgentMemoryCore(configureMemory);
        NeoInfra.ServiceCollectionExtensions.AddNeo4jAgentMemory(services, configureNeo4j);
        services.AddLlmExtraction(configureLlm);

        return services;
    }
}
