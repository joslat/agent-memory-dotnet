using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extraction.AzureLanguage;
using AgentMemory.Extraction.Llm;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Observability;

namespace AgentMemory.Tests.Unit.MetaPackage;

public sealed class MetaPackageDiRegistrationTests
{
    private static IServiceCollection BuildServices(
        Action<MemoryOptions>? configureMemory = null,
        Action<Neo4jOptions>? configureNeo4j = null,
        Action<LlmExtractionOptions>? configureLlm = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddNeo4jAgentMemory(
            configureMemory ?? (_ => { }),
            configureNeo4j  ?? (_ => { }),
            configureLlm);

        return services;
    }

    [Fact]
    public void AddNeo4jAgentMemory_RegistersCoreServices()
    {
        var services = BuildServices();
        services.Should().Contain(d => d.ServiceType == typeof(IMemoryService));
    }

    [Fact]
    public void AddNeo4jAgentMemory_RegistersMemoryRoleInterfaces()
    {
        var services = BuildServices();

        // The ISP role interfaces (3.10) are registered alongside the composed IMemoryService.
        services.Should().Contain(d => d.ServiceType == typeof(IMemoryRecall));
        services.Should().Contain(d => d.ServiceType == typeof(IMemoryIngestion));
        services.Should().Contain(d => d.ServiceType == typeof(IMemoryMaintenance));
    }

    [Fact]
    public void IMemoryService_ComposesAllThreeRoleInterfaces()
    {
        // The facade transition-shim must expose every role so existing consumers stay source-compatible.
        typeof(IMemoryRecall).IsAssignableFrom(typeof(IMemoryService)).Should().BeTrue();
        typeof(IMemoryIngestion).IsAssignableFrom(typeof(IMemoryService)).Should().BeTrue();
        typeof(IMemoryMaintenance).IsAssignableFrom(typeof(IMemoryService)).Should().BeTrue();
    }

    [Fact]
    public void AddNeo4jAgentMemory_RegistersShortTermMemoryService()
    {
        var services = BuildServices();
        services.Should().Contain(d => d.ServiceType == typeof(IShortTermMemoryService));
    }

    [Fact]
    public void AddNeo4jAgentMemory_RegistersLongTermMemoryService()
    {
        var services = BuildServices();
        services.Should().Contain(d => d.ServiceType == typeof(ILongTermMemoryService));
    }

    [Fact]
    public void AddNeo4jAgentMemory_RegistersNeo4jOptions()
    {
        var services = BuildServices(configureNeo4j: o => o.Uri = "bolt://test:7687");
        var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<Neo4jOptions>>();
        options.Value.Uri.Should().Be("bolt://test:7687");
    }

    [Fact]
    public void AddNeo4jAgentMemory_RegistersMemoryOptions()
    {
        var services = BuildServices();
        var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<MemoryOptions>>();
        options.Value.Should().NotBeNull();
        options.Value.ShortTerm.Should().NotBeNull();
        options.Value.LongTerm.Should().NotBeNull();
    }

    [Fact]
    public void AddNeo4jAgentMemory_RegistersStoreIsolationServices()
    {
        var services = BuildServices();

        // R1b: application/memory-store isolation tier.
        services.Should().Contain(d => d.ServiceType == typeof(IMemoryStoreContext));
        services.Should().Contain(d => d.ServiceType == typeof(IMemoryStoreProvisioner));
    }

    [Fact]
    public void AddNeo4jAgentMemory_DefaultStoreOptions_AreSharedDatabaseAndInheritDefaultDb()
    {
        var services = BuildServices();
        var provider = services.BuildServiceProvider();

        var opts = provider.GetRequiredService<IOptions<MemoryStoreOptions>>();
        opts.Value.Strategy.Should().Be(MemoryStorageStrategy.SharedDatabase);
        opts.Value.DefaultDatabase.Should().BeEmpty(); // empty ⇒ inherit Neo4jOptions.Database
    }

    [Fact]
    public void AddNeo4jAgentMemory_SessionFactoryResolvesWithStoreDependencies()
    {
        var services = BuildServices(configureNeo4j: o => o.Uri = "bolt://test:7687");
        var provider = services.BuildServiceProvider();

        // Verifies the store-aware ctor (IOptions<MemoryStoreOptions> + IMemoryStoreContext) is satisfiable.
        provider.GetRequiredService<INeo4jSessionFactory>().Should().BeOfType<Neo4jSessionFactory>();
    }

    [Fact]
    public void AddNeo4jAgentMemory_RegistersStreamingExtractor()
    {
        var services = BuildServices();
        services.Should().Contain(d => d.ServiceType == typeof(IStreamingExtractor));
    }

    [Fact]
    public void AddNeo4jAgentMemory_RegistersConsolidationService()
    {
        var services = BuildServices();
        services.Should().Contain(d => d.ServiceType == typeof(IConsolidationService));
    }

    [Fact]
    public void AddNeo4jAgentMemory_RegistersLlmExtractors()
    {
        var services = BuildServices();
        services.Should().Contain(d => d.ServiceType == typeof(IEntityExtractor));
        services.Should().Contain(d => d.ServiceType == typeof(IFactExtractor));
        services.Should().Contain(d => d.ServiceType == typeof(IPreferenceExtractor));
        services.Should().Contain(d => d.ServiceType == typeof(IRelationshipExtractor));
    }

    [Fact]
    public void AddNeo4jAgentMemory_WithLlmConfigure_AppliesLlmOptions()
    {
        var services = BuildServices(configureLlm: o => o.ModelId = "gpt-4o");
        var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<LlmExtractionOptions>>();
        options.Value.ModelId.Should().Be("gpt-4o");
    }

    [Fact]
    public void AddNeo4jAgentMemory_WithoutLlmConfigure_UsesDefaultLlmOptions()
    {
        var services = BuildServices();
        var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<LlmExtractionOptions>>();
        options.Value.Temperature.Should().Be(0.0f);
        options.Value.MaxRetries.Should().Be(2);
    }

    [Fact]
    public void AddNeo4jAgentMemory_NullServices_ThrowsArgumentNull()
    {
        IServiceCollection nullServices = null!;
        var act = () => nullServices.AddNeo4jAgentMemory(_ => { }, _ => { });
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddNeo4jAgentMemory_NullConfigureMemory_ThrowsArgumentNull()
    {
        var services = new ServiceCollection();
        var act = () => services.AddNeo4jAgentMemory(null!, _ => { });
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddNeo4jAgentMemory_NullConfigureNeo4j_ThrowsArgumentNull()
    {
        var services = new ServiceCollection();
        var act = () => services.AddNeo4jAgentMemory(_ => { }, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── 3.13: opt-in capability methods ────────────────────────────────────────

    [Fact]
    public void WithObservability_RegistersMetricsAndIsChainable()
    {
        var services = BuildServices();

        var returned = services.WithObservability();

        returned.Should().BeSameAs(services);
        services.Should().Contain(d => d.ServiceType == typeof(MemoryMetrics));
    }

    [Fact]
    public void WithEnrichment_RegistersEnrichmentServices()
    {
        var services = BuildServices().WithEnrichment();

        services.Should().Contain(d => d.ServiceType == typeof(IGeocodingService));
        services.Should().Contain(d => d.ServiceType == typeof(IEnrichmentService));
    }

    [Fact]
    public void WithAzureLanguageExtraction_RegistersAzureOptions()
    {
        var services = BuildServices()
            .WithAzureLanguageExtraction(o => { o.Endpoint = "https://example.cognitiveservices.azure.com"; o.ApiKey = "k"; });
        var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<AzureLanguageOptions>>();
        options.Value.Endpoint.Should().Be("https://example.cognitiveservices.azure.com");
    }

    [Fact]
    public void WithAzureLanguageExtraction_NullConfigure_ThrowsArgumentNull()
    {
        var services = BuildServices();
        var act = () => services.WithAzureLanguageExtraction(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
