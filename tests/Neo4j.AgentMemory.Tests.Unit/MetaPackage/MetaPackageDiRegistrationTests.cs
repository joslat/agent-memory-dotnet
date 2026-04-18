using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Extraction.Llm;
using Neo4j.AgentMemory.Neo4j.Infrastructure;

namespace Neo4j.AgentMemory.Tests.Unit.MetaPackage;

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
}
