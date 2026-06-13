using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Analytics;
using AgentMemory.Neo4j.Infrastructure;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Analytics;

public sealed class GdsAnalyticsTests
{
    // ── Projection Cypher shape (owner isolation) ────────────────────────────

    [Fact]
    public void Projection_Scoped_FiltersLiveAndOwnerOnBothEndpoints()
    {
        var (node, rel) = GdsQueries.Projection(hasOwnerFilter: true, includeShared: true);

        node.Should().Contain("e.invalidated_at IS NULL")
            .And.Contain("(e.owner_id = $ownerId OR e.owner_id IS NULL)");
        // A relationship is projected only when BOTH endpoints are in scope (no cross-owner edges).
        rel.Should().Contain("(a.owner_id = $ownerId OR a.owner_id IS NULL)")
            .And.Contain("(b.owner_id = $ownerId OR b.owner_id IS NULL)");
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
}
