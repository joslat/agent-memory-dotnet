using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Analytics;
using AgentMemory.Neo4j.Infrastructure;
using Neo4j.Driver;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Analytics;

public sealed class GdsAnalyticsTests
{
    // ── GdsAvailability: definitive vs transient probe caching ───────────────

    [Fact]
    public async Task GdsAvailability_TransientProbeFailure_IsNotCached_AndReprobes()
    {
        var tx = Substitute.For<INeo4jTransactionRunner>();
        var calls = 0;
        tx.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<string>>>(), Arg.Any<CancellationToken>())
          .Returns(_ => ++calls == 1
              ? throw new ServiceUnavailableException("connection blip")
              : Task.FromResult("2.5.0"));
        var sut = new GdsAvailability(tx, NullLogger<GdsAvailability>.Instance);

        (await sut.IsAvailableAsync()).Should().BeFalse("a transient probe failure degrades to unavailable");
        (await sut.IsAvailableAsync()).Should().BeTrue("the transient failure was not cached, so it re-probes and recovers");
        calls.Should().Be(2);
    }

    [Fact]
    public async Task GdsAvailability_NotInstalled_IsCached_AfterOneProbe()
    {
        var tx = Substitute.For<INeo4jTransactionRunner>();
        var calls = 0;
        tx.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<string>>>(), Arg.Any<CancellationToken>())
          .Returns<Task<string>>(_ => { calls++; throw new ClientException("Unknown function 'gds.version'"); });
        var sut = new GdsAvailability(tx, NullLogger<GdsAvailability>.Instance);

        (await sut.IsAvailableAsync()).Should().BeFalse();
        (await sut.IsAvailableAsync()).Should().BeFalse();
        calls.Should().Be(1, "a genuine not-installed result is a stable answer and is memoized");
    }

    [Fact]
    public async Task GdsAvailability_Available_IsCached_AndReturnsTrue()
    {
        var tx = Substitute.For<INeo4jTransactionRunner>();
        var calls = 0;
        tx.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<string>>>(), Arg.Any<CancellationToken>())
          .Returns(_ => { calls++; return Task.FromResult("2.5.0"); });
        var sut = new GdsAvailability(tx, NullLogger<GdsAvailability>.Instance);

        (await sut.IsAvailableAsync()).Should().BeTrue();
        (await sut.IsAvailableAsync()).Should().BeTrue();
        calls.Should().Be(1, "a present result is memoized for the process lifetime");
    }

    // ── Projection Cypher shape (owner isolation) ────────────────────────────

    [Fact]
    public void Projection_Scoped_FiltersLiveAndOwnerOnBothEndpoints()
    {
        var (node, rel) = GdsQueries.Projection(hasOwnerFilter: true, includeShared: true);

        node.Should().Contain("e.invalidated_at IS NULL")
            .And.Contain("(e.owner_id = $ownerId OR e.owner_id IS NULL)");
        // A relationship is projected only when BOTH endpoints are in scope (no cross-owner edges)...
        rel.Should().Contain("(a.owner_id = $ownerId OR a.owner_id IS NULL)")
            .And.Contain("(b.owner_id = $ownerId OR b.owner_id IS NULL)");
        // ...and the edge's OWN owner_id is scoped too, so a foreign edge between two shared nodes can't
        // perturb a scoped owner's analytics.
        rel.Should().Contain("(r.owner_id = $ownerId OR r.owner_id IS NULL)");
    }

    [Fact]
    public void Projection_ScopedExcludeShared_RestrictsToOwnerExactly()
    {
        var (node, rel) = GdsQueries.Projection(hasOwnerFilter: true, includeShared: false);

        node.Should().Contain("e.owner_id = $ownerId").And.NotContain("owner_id IS NULL");
        rel.Should().Contain("a.owner_id = $ownerId").And.Contain("b.owner_id = $ownerId").And.NotContain("IS NULL");
    }

    [Fact]
    public void Projection_Unscoped_HasNoOwnerFilter_ButStillLiveOnly()
    {
        var (node, rel) = GdsQueries.Projection(hasOwnerFilter: false, includeShared: true);

        node.Should().NotContain("owner_id").And.Contain("invalidated_at IS NULL");
        rel.Should().NotContain("owner_id");
    }

    [Fact]
    public void ProjectCypher_BindsOwnerParamWhenScoped_AndAlwaysSkipsDanglingRels()
    {
        GdsQueries.ProjectCypher(hasOwnerFilter: true)
            .Should().Contain("validateRelationships: false").And.Contain("parameters: {ownerId: $ownerId}");
        GdsQueries.ProjectCypher(hasOwnerFilter: false)
            .Should().Contain("validateRelationships: false").And.NotContain("ownerId");
    }

    // ── Graceful degradation when GDS is absent ──────────────────────────────

    [Fact]
    public async Task PageRank_WhenGdsUnavailable_ReturnsEmpty()
    {
        var gds = Substitute.For<IGdsAvailability>();
        gds.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(false);
        var sut = new MemoryPageRankService(
            Substitute.For<INeo4jTransactionRunner>(), gds,
            Options.Create(new GdsAnalyticsOptions()), NullLogger<MemoryPageRankService>.Instance);

        (await sut.RankEntitiesAsync(topN: 10)).Should().BeEmpty();
        await gds.Received(1).IsAvailableAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Community_WhenGdsUnavailable_ReturnsEmpty()
    {
        var gds = Substitute.For<IGdsAvailability>();
        gds.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(false);
        var sut = new MemoryCommunityService(
            Substitute.For<INeo4jTransactionRunner>(), gds, NullLogger<MemoryCommunityService>.Instance);

        (await sut.DetectCommunitiesAsync()).Should().BeEmpty();
        await gds.Received(1).IsAvailableAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void AddGdsMemoryAnalytics_RegistersAllServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<INeo4jTransactionRunner>()); // provided by AddNeo4jAgentMemory in real use
        services.AddGdsMemoryAnalytics(o => o.DefaultTopN = 5);

        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();

        scope.ServiceProvider.GetRequiredService<IGdsAvailability>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<IMemoryPageRankService>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<IMemoryCommunityService>().Should().NotBeNull();
    }

    // ── RankEntitiesAsync: topN input guard (runs before the GDS-availability probe) ──

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task RankEntitiesAsync_NonPositiveTopN_Throws(int topN)
    {
        var sut = new MemoryPageRankService(
            Substitute.For<INeo4jTransactionRunner>(),
            Substitute.For<IGdsAvailability>(),
            Options.Create(new GdsAnalyticsOptions()),
            NullLogger<MemoryPageRankService>.Instance);

        var act = async () => await sut.RankEntitiesAsync(topN: topN);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
