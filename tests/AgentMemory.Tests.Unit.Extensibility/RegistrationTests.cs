using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility;
using AgentMemory.Extensibility.AgentFramework;
using AgentMemory.Extensibility.Capabilities;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Contributors;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// Registration: the seam that decides whether any of this is reachable from a host.
/// </summary>
/// <remarks>
/// Worth its own tests because an SDK that compiles and cannot be wired up is indistinguishable, from
/// the host's side, from one that does not exist — and the first cut of this package shipped with no
/// registration extension at all.
/// </remarks>
public sealed class RegistrationTests
{
    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IMemoryContextAssembler>());
        // The SDK REQUIRES an isolation policy and deliberately does not default one: silently
        // installing a policy that decides who can read what is not a memory package's call. A real
        // host gets it from AddNeo4jAgentMemory; these tests supply it explicitly for the same reason.
        var isolation = Substitute.For<AgentMemory.Abstractions.Services.IMemoryIsolationPolicy>();
        isolation.ResolveReadScope(
                Arg.Any<AgentMemory.Abstractions.Options.MemoryScope?>(), Arg.Any<string?>(),
                Arg.Any<string>(), Arg.Any<AgentMemory.Abstractions.Domain.MemoryOperationAccess>())
            .Returns(call => AgentMemory.Abstractions.Options.MemoryScope.For(call.ArgAt<string?>(1) ?? "anonymous"));
        services.AddSingleton(isolation);
        return services;
    }

    /// <summary>The compiler resolves after registration.</summary>
    [Fact]
    public void TheCompilerIsResolvable()
    {
        using var provider = Services().AddAgentMemoryExtensibility().BuildServiceProvider();

        provider.GetService<IContextCompiler>().Should().NotBeNull();
    }

    /// <summary>
    /// Core memory is NOT registered as a contributor by default.
    /// </summary>
    /// <remarks>
    /// On the Agent Framework path core memory is inherited from the shipped provider. Registering it
    /// here as well would render core twice — once inherited, once compiled — which is the single
    /// outcome the opaque core section exists to prevent.
    /// </remarks>
    [Fact]
    public void CoreMemoryIsNotAContributorUnlessAsked()
    {
        using var provider = Services().AddAgentMemoryExtensibility().BuildServiceProvider();

        provider.GetServices<IContextContributor>().Should().BeEmpty();
    }

    /// <summary>A host that compiles rather than inherits can opt in.</summary>
    [Fact]
    public void CoreMemoryCanBeAddedDeliberately()
    {
        using var provider = Services()
            .AddAgentMemoryExtensibility()
            .AddCoreMemoryContributor()
            .BuildServiceProvider();

        provider.GetServices<IContextContributor>().Should().ContainSingle()
            .Which.Descriptor.Id.Should().Be(CoreMemoryContextContributor.ContributorId);
    }

    /// <summary>
    /// Contributors are additive: two modules both contributing is the normal case.
    /// </summary>
    /// <remarks>
    /// A <c>TryAdd</c> here would silently drop the second module, and the host would see one
    /// module's sections missing with nothing to explain why.
    /// </remarks>
    [Fact]
    public void TwoContributorsBothRegister()
    {
        using var provider = Services()
            .AddAgentMemoryExtensibility()
            .AddContextContributor<FirstContributor>()
            .AddContextContributor<SecondContributor>()
            .BuildServiceProvider();

        provider.GetServices<IContextContributor>().Select(c => c.Descriptor.Id)
            .Should().BeEquivalentTo(["first", "second"]);
    }

    /// <summary>Registering twice does not add the compiler twice.</summary>
    [Fact]
    public void RegisteringTwiceIsIdempotentForTheCompiler()
    {
        using var provider = Services()
            .AddAgentMemoryExtensibility()
            .AddAgentMemoryExtensibility()
            .BuildServiceProvider();

        provider.GetServices<IContextCompiler>().Should().ContainSingle();
    }

    /// <summary>The resolved graph actually compiles an envelope end to end.</summary>
    [Fact]
    public async Task TheResolvedCompilerProducesAnEnvelope()
    {
        using var provider = Services()
            .AddAgentMemoryExtensibility()
            .AddContextContributor<FirstContributor>()
            .BuildServiceProvider();

        var envelope = await provider.GetRequiredService<IContextCompiler>()
            .CompileAsync(new ContextRequest { SessionId = "s-1" });

        envelope.Sections.Should().ContainSingle().Which.ContributorId.Should().Be("first");
    }

    private sealed class FirstContributor : StubContributor { public FirstContributor() : base("first") { } }

    private sealed class SecondContributor : StubContributor { public SecondContributor() : base("second") { } }

    private abstract class StubContributor(string id) : IContextContributor
    {
        public ContextContributorDescriptor Descriptor { get; } =
            new(id, new HashSet<string>(StringComparer.Ordinal) { $"{id}.section" });

        public ValueTask<bool> AppliesAsync(ContextRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);

        public Task<ContextSection?> ContributeAsync(ContextRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<ContextSection?>(
                new ContextSection($"{id}.section", id, 1, [new ContextItem(null, "x")]));
    }
}

/// <summary>
/// The Agent Framework registration: the seam without which this package does nothing.
/// </summary>
/// <remarks>
/// The first cut registered a compiler and contributors and left the agent using the SHIPPED
/// provider — every call succeeded and module sections never reached a prompt. That is the
/// reachable-but-never-fed defect in registration form, and it is the reason these exist.
/// </remarks>
public sealed class FrameworkRegistrationTests
{
    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<AgentMemory.Abstractions.Services.IMemoryService>());
        services.AddSingleton(Substitute.For<AgentMemory.Abstractions.Services.IEmbeddingOrchestrator>());
        services.AddSingleton(Substitute.For<AgentMemory.Abstractions.Services.IClock>());
        services.AddSingleton(Substitute.For<AgentMemory.Abstractions.Services.IIdGenerator>());
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(
            new AgentMemory.Abstractions.Options.MemoryOptions()));
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(
            new AgentMemory.AgentFramework.ContextFormatOptions()));
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(
            new AgentMemory.AgentFramework.AgentFrameworkOptions()));
        services.AddSingleton(Substitute.For<AgentMemory.Abstractions.Services.IMemoryContextAssembler>());
        // The SDK REQUIRES an isolation policy and deliberately does not default one: silently
        // installing a policy that decides who can read what is not a memory package's call. A real
        // host gets it from AddNeo4jAgentMemory; these tests supply it explicitly for the same reason.
        var isolation = Substitute.For<AgentMemory.Abstractions.Services.IMemoryIsolationPolicy>();
        isolation.ResolveReadScope(
                Arg.Any<AgentMemory.Abstractions.Options.MemoryScope?>(), Arg.Any<string?>(),
                Arg.Any<string>(), Arg.Any<AgentMemory.Abstractions.Domain.MemoryOperationAccess>())
            .Returns(call => AgentMemory.Abstractions.Options.MemoryScope.For(call.ArgAt<string?>(1) ?? "anonymous"));
        services.AddSingleton(isolation);
        return services;
    }

    /// <summary>A host resolving the shipped provider gets the extended one.</summary>
    [Fact]
    public void TheShippedProviderTypeResolvesToTheExtensibleOne()
    {
        var services = Services();
        services.AddScoped<AgentMemory.AgentFramework.Neo4jMemoryContextProvider>();
        services.AddAgentMemoryFrameworkExtensibility();

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<AgentMemory.AgentFramework.Neo4jMemoryContextProvider>()
            .Should().BeOfType<AgentMemory.Extensibility.AgentFramework.ExtensibleMemoryContextProvider>(
                "otherwise the agent keeps using the shipped provider and module sections never appear");
    }

    /// <summary>
    /// Exactly one provider, so core memory is never rendered twice.
    /// </summary>
    [Fact]
    public void ThereIsExactlyOneProviderRegistration()
    {
        var services = Services();
        services.AddScoped<AgentMemory.AgentFramework.Neo4jMemoryContextProvider>();
        services.AddAgentMemoryFrameworkExtensibility();

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        scope.ServiceProvider
            .GetServices<AgentMemory.AgentFramework.Neo4jMemoryContextProvider>()
            .Should().ContainSingle();
    }

    /// <summary>
    /// Registration order does not decide which provider the host gets.
    /// </summary>
    /// <remarks>
    /// The two registrations compose from either direction, and by different mechanisms: the core
    /// uses <c>TryAddScoped</c>, so it no-ops when this package went first, and this package uses
    /// <c>Replace</c>, so it wins when the core went first. Neither fact is obvious from reading one
    /// of them, and if either changed the failure would be silent -- a host would resolve the shipped
    /// provider, every call would succeed, and module sections would simply never appear.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RegistrationOrderDoesNotChangeWhichProviderWins(bool extensibilityFirst)
    {
        var services = Services();

        if (extensibilityFirst)
        {
            services.AddAgentMemoryFrameworkExtensibility();
            services.TryAddScoped<AgentMemory.AgentFramework.Neo4jMemoryContextProvider>();
        }
        else
        {
            services.TryAddScoped<AgentMemory.AgentFramework.Neo4jMemoryContextProvider>();
            services.AddAgentMemoryFrameworkExtensibility();
        }

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<AgentMemory.AgentFramework.Neo4jMemoryContextProvider>()
            .Should().BeOfType<AgentMemory.Extensibility.AgentFramework.ExtensibleMemoryContextProvider>();
    }

    /// <summary>
    /// The graph builds with scope validation on.
    /// </summary>
    /// <remarks>
    /// The first cut registered the compiler and contributors as singletons while Core registers the
    /// assembler scoped — a captive dependency that either refuses to build here or silently outlives
    /// its request. This repository has paid for one of those before.
    /// </remarks>
    [Fact]
    public void TheGraphSurvivesScopeValidation()
    {
        var services = Services();
        services.AddScoped<AgentMemory.AgentFramework.Neo4jMemoryContextProvider>();
        services.AddAgentMemoryFrameworkExtensibility();
        services.AddCoreMemoryContributor();

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IContextCompiler>().Should().NotBeNull();
    }
}
