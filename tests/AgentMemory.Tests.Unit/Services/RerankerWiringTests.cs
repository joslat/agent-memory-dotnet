using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// Rerankers must be reachable, and must change nothing when off (17.4b).
/// </summary>
/// <remarks>
/// <para>
/// <c>NodeDistanceReranker</c> (R6) and <c>MentionFrequencyReranker</c> (R7) shipped with full unit
/// suites, and had <b>zero DI registrations and zero <c>RerankAsync</c> call sites</b> across the
/// repository — while <c>MemoryOptions.NodeDistanceReranking</c> and <c>MentionFrequencyReranking</c>
/// sat in the public surface documenting behaviour no consumer could obtain, and Phase 10 was
/// recorded COMPLETE.
/// </para>
/// <para>
/// Two properties are held here because each failed independently: the implementations must be
/// resolvable, and turning the flags on must actually reach them. A test that only checked
/// registration would have passed on a build where nothing called them — which is the state that
/// shipped.
/// </para>
/// </remarks>
public sealed class RerankerWiringTests
{
    // The container holds IAsyncDisposable services (the Neo4j driver factory), so it must be
    // disposed asynchronously; a synchronous `using` throws on teardown and turns a passing assertion
    // into a failing test.
    private static ServiceProvider Resolve()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNeo4jAgentMemory(
            new MemoryOptions(),
            neo4j =>
            {
                neo4j.Uri = "bolt://localhost:7687";
                neo4j.Username = "neo4j";
                neo4j.Password = "unused-in-this-test";
                neo4j.EmbeddingDimensions = 1536;
            });
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task BothShippedRerankersAreResolvable()
    {
        // Red before 17.4b: this returned an empty sequence, so the two public options gated
        // implementations the container had never heard of.
        await using var provider = Resolve();

        var rerankers = provider.CreateScope().ServiceProvider
            .GetServices<IMemoryReranker>()
            .Select(reranker => reranker.GetType().Name)
            .ToList();

        rerankers.Should().Contain("NodeDistanceReranker");
        rerankers.Should().Contain("MentionFrequencyReranker");
    }

    [Fact]
    public async Task EveryRerankerImplementationIsRegistered()
    {
        // THE drift guard. A third reranker added later and left unregistered is the same defect
        // wearing a new name, and it would be invisible: the option would bind, validate, and do
        // nothing. Reflected rather than listed, so the list cannot go stale.
        await using var provider = Resolve();
        var registered = provider.CreateScope().ServiceProvider
            .GetServices<IMemoryReranker>()
            .Select(reranker => reranker.GetType())
            .ToHashSet();

        var implementations = typeof(AgentMemory.Neo4j.Infrastructure.Neo4jOptions).Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract && type.IsAssignableTo(typeof(IMemoryReranker)))
            .ToList();

        implementations.Should().NotBeEmpty();
        registered.Should().BeEquivalentTo(implementations,
            "an IMemoryReranker the container cannot produce is an option that silently does nothing");
    }

    [Fact]
    public async Task BothRerankersAreOffByDefault()
    {
        // The default recall path must be byte-identical to before this wiring, or every sealed
        // measurement becomes incomparable with anything taken after it.
        var options = new MemoryOptions();

        options.NodeDistanceReranking.Should().BeFalse();
        options.MentionFrequencyReranking.Should().BeFalse();

        await using var provider = Resolve();
        provider.CreateScope().ServiceProvider
            .GetServices<IMemoryReranker>()
            .Should().OnlyContain(reranker => !reranker.IsEnabled,
                "registration must not by itself turn reranking on");
    }
}
