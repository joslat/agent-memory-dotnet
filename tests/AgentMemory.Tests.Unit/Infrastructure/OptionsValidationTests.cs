using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using AgentMemory.Enrichment;
using AgentMemory.Neo4j.Infrastructure;

namespace AgentMemory.Tests.Unit.Infrastructure;

/// <summary>
/// Verifies that invalid configuration is rejected (fail-fast) via the options validation
/// wired up in the DI registration extensions (Task 2.8).
/// </summary>
public sealed class OptionsValidationTests
{
    [Fact]
    public void Neo4jOptions_EmptyUri_FailsValidation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNeo4jAgentMemory(o => o.Uri = "");

        using var provider = services.BuildServiceProvider();

        var act = () => _ = provider.GetRequiredService<IOptions<Neo4jOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void Neo4jOptions_NonPositiveEmbeddingDimensions_FailsValidation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNeo4jAgentMemory(o => o.EmbeddingDimensions = 0);

        using var provider = services.BuildServiceProvider();

        var act = () => _ = provider.GetRequiredService<IOptions<Neo4jOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void GeocodingOptions_EmptyUserAgent_FailsValidation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEnrichmentServices(configureGeocoding: o => o.UserAgent = "");

        using var provider = services.BuildServiceProvider();

        var act = () => _ = provider.GetRequiredService<IOptions<GeocodingOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void Neo4jOptions_ValidConfig_PassesValidation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNeo4jAgentMemory(o =>
        {
            o.Uri = "bolt://localhost:7687";
            o.EmbeddingDimensions = 1536;
        });

        using var provider = services.BuildServiceProvider();

        var act = () => _ = provider.GetRequiredService<IOptions<Neo4jOptions>>().Value;

        act.Should().NotThrow();
    }
}
