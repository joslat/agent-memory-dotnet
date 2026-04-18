using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.AgentFramework;
using Neo4j.AgentMemory.AgentFramework.Tools;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.AgentFramework;

/// <summary>
/// Tests that <see cref="ServiceCollectionExtensions.AddAgentMemoryFramework"/> registers the expected
/// services with the correct lifetimes and that they resolve without error when dependencies are present.
/// </summary>
public sealed class ServiceCollectionExtensionsTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static IServiceCollection BuildBaseServices()
    {
        var services = new ServiceCollection();

        // Core dependencies that AgentFramework services require.
        services.AddSingleton(Substitute.For<IMemoryService>());
        services.AddSingleton(Substitute.For<ILongTermMemoryService>());
        services.AddSingleton(Substitute.For<IReasoningMemoryService>());
        services.AddSingleton(Substitute.For<IEmbeddingOrchestrator>());
        services.AddSingleton(Substitute.For<IClock>());
        services.AddSingleton(Substitute.For<IIdGenerator>());

        // Provide ILogger<T> for all types via NullLoggerFactory.
        services.AddSingleton<ILoggerFactory>(_ => NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        return services;
    }

    // ── lifetime tests ────────────────────────────────────────────────────────

    [Fact]
    public void AddAgentMemoryFramework_RegistersNeo4jMemoryContextProvider_AsScoped()
    {
        var services = BuildBaseServices();
        services.AddAgentMemoryFramework();

        services.Should().ContainSingle(d =>
            d.ServiceType == typeof(Neo4jMemoryContextProvider) &&
            d.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddAgentMemoryFramework_RegistersNeo4jChatMessageStore_AsScoped()
    {
        var services = BuildBaseServices();
        services.AddAgentMemoryFramework();

        services.Should().ContainSingle(d =>
            d.ServiceType == typeof(Neo4jChatMessageStore) &&
            d.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddAgentMemoryFramework_RegistersNeo4jMicrosoftMemoryFacade_AsScoped()
    {
        var services = BuildBaseServices();
        services.AddAgentMemoryFramework();

        services.Should().ContainSingle(d =>
            d.ServiceType == typeof(Neo4jMicrosoftMemoryFacade) &&
            d.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddAgentMemoryFramework_RegistersAgentTraceRecorder_AsScoped()
    {
        var services = BuildBaseServices();
        services.AddAgentMemoryFramework();

        services.Should().ContainSingle(d =>
            d.ServiceType == typeof(AgentTraceRecorder) &&
            d.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddAgentMemoryFramework_RegistersMemoryToolFactory_AsScoped()
    {
        var services = BuildBaseServices();
        services.AddAgentMemoryFramework();

        services.Should().ContainSingle(d =>
            d.ServiceType == typeof(MemoryToolFactory) &&
            d.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddAgentMemoryFramework_RegistersNeo4jChatHistoryProvider_AsScoped()
    {
        var services = BuildBaseServices();
        services.AddAgentMemoryFramework();

        services.Should().ContainSingle(d =>
            d.ServiceType == typeof(Neo4jChatHistoryProvider) &&
            d.Lifetime == ServiceLifetime.Scoped);
    }

    // ── resolution tests ──────────────────────────────────────────────────────

    [Fact]
    public void AddAgentMemoryFramework_ResolvesNeo4jMemoryContextProvider_WithDependencies()
    {
        var provider = BuildBaseServices()
            .AddAgentMemoryFramework()
            .BuildServiceProvider();

        using var scope = provider.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<Neo4jMemoryContextProvider>();

        sut.Should().NotBeNull();
    }

    [Fact]
    public void AddAgentMemoryFramework_ResolvesAgentTraceRecorder_WithDependencies()
    {
        var provider = BuildBaseServices()
            .AddAgentMemoryFramework()
            .BuildServiceProvider();

        using var scope = provider.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<AgentTraceRecorder>();

        sut.Should().NotBeNull();
    }

    [Fact]
    public void AddAgentMemoryFramework_ResolvesMemoryToolFactory_WithDependencies()
    {
        var provider = BuildBaseServices()
            .AddAgentMemoryFramework()
            .BuildServiceProvider();

        using var scope = provider.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<MemoryToolFactory>();

        sut.Should().NotBeNull();
    }

    [Fact]
    public void AddAgentMemoryFramework_ResolvesNeo4jChatHistoryProvider_WithDependencies()
    {
        var provider = BuildBaseServices()
            .AddAgentMemoryFramework()
            .BuildServiceProvider();

        using var scope = provider.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<Neo4jChatHistoryProvider>();

        sut.Should().NotBeNull();
    }

    // ── idempotency ───────────────────────────────────────────────────────────

    [Fact]
    public void AddAgentMemoryFramework_CalledTwice_DoesNotDuplicateRegistrations()
    {
        var services = BuildBaseServices();
        services.AddAgentMemoryFramework();
        services.AddAgentMemoryFramework();

        var contextProviderCount = services.Count(d => d.ServiceType == typeof(Neo4jMemoryContextProvider));
        contextProviderCount.Should().Be(1, "TryAddScoped should not register a second instance");
    }

    // ── options ───────────────────────────────────────────────────────────────

    [Fact]
    public void AddAgentMemoryFramework_WithConfigure_AppliesOptions()
    {
        var provider = BuildBaseServices()
            .AddAgentMemoryFramework(opts =>
            {
                opts.AutoExtractOnPersist = false;
                opts.DefaultSessionIdKey = "my_session";
            })
            .BuildServiceProvider();

        var opts = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentFrameworkOptions>>().Value;

        opts.AutoExtractOnPersist.Should().BeFalse();
        opts.DefaultSessionIdKey.Should().Be("my_session");
    }
}
