using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core;
using AgentMemory.Enrichment;
using AgentMemory.Extraction.AzureLanguage;
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

    // ── Stabilization fixes below: these registrations previously had no validation at all ──

    [Fact]
    public void Neo4jOptions_NonPositiveConnectionAcquisitionTimeout_FailsValidation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNeo4jAgentMemory(o => o.ConnectionAcquisitionTimeout = TimeSpan.Zero);

        using var provider = services.BuildServiceProvider();

        var act = () => _ = provider.GetRequiredService<IOptions<Neo4jOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void MemoryStoreOptions_BlankDatabasePrefix_DatabasePerApplication_FailsValidation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNeo4jAgentMemory(
            o => { o.Uri = "bolt://localhost:7687"; },
            configureStore: o =>
            {
                o.Strategy = MemoryStorageStrategy.DatabasePerApplication;
                o.DatabasePrefix = "  ";
            });

        using var provider = services.BuildServiceProvider();

        var act = () => _ = provider.GetRequiredService<IOptions<MemoryStoreOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void MemoryStoreOptions_BlankDatabasePrefix_SharedDatabase_PassesValidation()
    {
        // DatabasePrefix is only meaningful under DatabasePerApplication -- SharedDatabase (the default
        // strategy) never reads it, so a blank value must not be rejected in that mode.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNeo4jAgentMemory(
            o => { o.Uri = "bolt://localhost:7687"; },
            configureStore: o => o.DatabasePrefix = "");

        using var provider = services.BuildServiceProvider();

        var act = () => _ = provider.GetRequiredService<IOptions<MemoryStoreOptions>>().Value;

        act.Should().NotThrow();
    }

    [Fact]
    public void EnrichmentCacheOptions_NonPositiveGeocodingCacheDuration_FailsValidation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEnrichmentServices(configureCaching: o => o.GeocodingCacheDuration = TimeSpan.Zero);

        using var provider = services.BuildServiceProvider();

        var act = () => _ = provider.GetRequiredService<IOptions<EnrichmentCacheOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void EnrichmentCacheOptions_NonPositiveEnrichmentCacheDuration_FailsValidation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEnrichmentServices(configureCaching: o => o.EnrichmentCacheDuration = TimeSpan.FromSeconds(-1));

        using var provider = services.BuildServiceProvider();

        var act = () => _ = provider.GetRequiredService<IOptions<EnrichmentCacheOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void MemoryOptions_UndefinedIsolationMode_FailsValidation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentMemoryCore(o => o.Isolation.Mode = (MemoryIsolationMode)99);

        using var provider = services.BuildServiceProvider();

        var act = () => _ = provider.GetRequiredService<IOptions<MemoryOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    // Note: MemoryOptions.ShortTerm/LongTerm/Reasoning are init-only record properties, so their nested
    // fields (validated above in AddAgentMemoryCore, e.g. DefaultRecentMessageLimit <= MaxMessagesPerQuery)
    // cannot be set through the Action<MemoryOptions> configure delegate this test file otherwise uses --
    // only through configuration-binding (appsettings.json), which the .NET options binder supports for
    // init properties but which is out of scope to wire up here. ExtractionOptions/MemoryIsolationOptions
    // are mutable classes, so their validations (below and above) are directly testable this way.

    [Fact]
    public void MemoryOptions_SameAsThresholdExceedsAutoMergeThreshold_FailsValidation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentMemoryCore(o =>
        {
            o.Extraction.AutoMergeThreshold = 0.8;
            o.Extraction.SameAsThreshold = 0.9;
        });

        using var provider = services.BuildServiceProvider();

        var act = () => _ = provider.GetRequiredService<IOptions<MemoryOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void MemoryOptions_ValidConfig_PassesValidation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentMemoryCore(o => { });

        using var provider = services.BuildServiceProvider();

        var act = () => _ = provider.GetRequiredService<IOptions<MemoryOptions>>().Value;

        act.Should().NotThrow();
    }

    [Fact]
    public void AzureLanguageOptions_OutOfRangeKeyPhraseFactConfidence_FailsValidation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzureLanguageExtraction(o =>
        {
            o.Endpoint = "https://example.cognitiveservices.azure.com";
            o.ApiKey = "key";
            o.KeyPhraseFactConfidence = 1.5;
        });

        using var provider = services.BuildServiceProvider();

        var act = () => _ = provider.GetRequiredService<IOptions<AzureLanguageOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }
}
