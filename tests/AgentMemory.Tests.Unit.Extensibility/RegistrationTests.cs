using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility;
using AgentMemory.Extensibility.Capabilities;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Contributors;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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
