using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using AgentMemory.AgentFramework.Recall;
using AgentMemory.AgentFramework.Security;
using AgentMemory.AgentFramework.Tools;
using NSubstitute;

namespace AgentMemory.Tests.Unit.AgentFramework;

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
        services.AddSingleton(Substitute.For<IMemoryIsolationPolicy>());

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

    // ── #88: task-aware automatic recall policy ────────────────────────────────

    [Fact]
    public void AddAgentMemoryFramework_RegistersConfiguredAutomaticRecallPolicy_AsScoped_ByDefault()
    {
        var services = BuildBaseServices();
        services.AddAgentMemoryFramework();

        services.Should().ContainSingle(d =>
            d.ServiceType == typeof(IAutomaticRecallPolicy) &&
            d.ImplementationType == typeof(ConfiguredAutomaticRecallPolicy) &&
            d.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddAgentMemoryFramework_ResolvesIAutomaticRecallPolicy_WithDependencies()
    {
        var provider = BuildBaseServices()
            .AddAgentMemoryFramework()
            .BuildServiceProvider();

        using var scope = provider.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<IAutomaticRecallPolicy>();

        sut.Should().BeOfType<ConfiguredAutomaticRecallPolicy>();
    }

    [Fact]
    public void AddAgentMemoryFramework_HostRegisteredPolicyBeforeCall_IsNotOverridden()
    {
        // TryAdd: a host that registers its own IAutomaticRecallPolicy before AddAgentMemoryFramework
        // must keep that registration -- the built-in Configured policy must not clobber it.
        var services = BuildBaseServices();
        services.AddScoped<IAutomaticRecallPolicy, HeuristicAutomaticRecallPolicy>();
        services.AddAgentMemoryFramework();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<IAutomaticRecallPolicy>();

        sut.Should().BeOfType<HeuristicAutomaticRecallPolicy>();
    }

    [Fact]
    public void AddAgentMemoryFramework_HostRegisteredPolicyAfterCall_WinsOverDefault()
    {
        // A host that registers its own IAutomaticRecallPolicy after AddAgentMemoryFramework must win --
        // plain AddScoped appends, and the last registration is what resolves for a non-keyed service.
        var services = BuildBaseServices();
        services.AddAgentMemoryFramework();
        services.AddScoped<IAutomaticRecallPolicy, HeuristicAutomaticRecallPolicy>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<IAutomaticRecallPolicy>();

        sut.Should().BeOfType<HeuristicAutomaticRecallPolicy>();
    }

    // ── #92 Phase 2: memory-context admission policy ────────────────────────────

    [Fact]
    public void AddAgentMemoryFramework_RegistersDefaultMemoryContextAdmissionPolicy_AsScoped_ByDefault()
    {
        var services = BuildBaseServices();
        services.AddAgentMemoryFramework();

        services.Should().ContainSingle(d =>
            d.ServiceType == typeof(IMemoryContextAdmissionPolicy) &&
            d.ImplementationType == typeof(DefaultMemoryContextAdmissionPolicy) &&
            d.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddAgentMemoryFramework_ResolvesIMemoryContextAdmissionPolicy_WithDependencies()
    {
        var provider = BuildBaseServices()
            .AddAgentMemoryFramework()
            .BuildServiceProvider();

        using var scope = provider.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<IMemoryContextAdmissionPolicy>();

        sut.Should().BeOfType<DefaultMemoryContextAdmissionPolicy>();
    }

    [Fact]
    public void AddAgentMemoryFramework_HostRegisteredAdmissionPolicyAfterCall_WinsOverDefault()
    {
        var services = BuildBaseServices();
        services.AddAgentMemoryFramework();
        var custom = Substitute.For<IMemoryContextAdmissionPolicy>();
        services.AddScoped(_ => custom);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<IMemoryContextAdmissionPolicy>();

        sut.Should().BeSameAs(custom);
    }

    [Fact]
    public void AddAgentMemoryFramework_WithConfigure_MapsSecurityModeIntoContextFormatOptions()
    {
        var provider = BuildBaseServices()
            .AddAgentMemoryFramework(opts => opts.ContextFormat.SecurityMode = MemoryContextSecurityMode.Strict)
            .BuildServiceProvider();

        var contextFormat = provider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<ContextFormatOptions>>().Value;

        contextFormat.SecurityMode.Should().Be(MemoryContextSecurityMode.Strict);
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

    [Fact]
    public void AddAgentMemoryFramework_WithConfigure_MapsMaxChatHistoryMessagesIntoContextFormatOptions()
    {
        // #91: MaxChatHistoryMessages set on AgentFrameworkOptions.ContextFormat must reach the
        // standalone ContextFormatOptions instance MafTypeMapper/Neo4jChatHistoryProvider consume.
        var provider = BuildBaseServices()
            .AddAgentMemoryFramework(opts => opts.ContextFormat.MaxChatHistoryMessages = 3)
            .BuildServiceProvider();

        var contextFormat = provider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<ContextFormatOptions>>().Value;

        contextFormat.MaxChatHistoryMessages.Should().Be(3);
    }

    [Fact]
    public void AddAgentMemoryFramework_NegativeMaxChatHistoryMessages_FailsValidationOnStart()
    {
        var provider = BuildBaseServices()
            .AddAgentMemoryFramework(opts => opts.ContextFormat.MaxChatHistoryMessages = -1)
            .BuildServiceProvider();

        var act = () => provider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<ContextFormatOptions>>().Value;

        act.Should().Throw<Microsoft.Extensions.Options.OptionsValidationException>();
    }
}
