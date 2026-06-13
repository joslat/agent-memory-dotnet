using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Options;
using AgentMemory.Analytics;
using AgentMemory.Tests.Integration.Fixtures;
using Xunit;

namespace AgentMemory.Tests.Integration.Analytics;

/// <summary>
/// Validates the optional GDS analytics services against a real GDS-enabled Neo4j: PageRank surfaces the
/// most-connected entity, community detection clusters connected entities, and both honour R1 owner scoping
/// (a scoped projection never includes another owner's entities).
/// </summary>
[Collection("Neo4j GDS Integration")]
[Trait("Category", "Integration")]
public class GdsAnalyticsIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jGdsIntegrationFixture _fixture;
    private readonly GdsAvailability _gds;
    private readonly MemoryPageRankService _pageRank;
    private readonly MemoryCommunityService _community;

    public GdsAnalyticsIntegrationTests(Neo4jGdsIntegrationFixture fixture)
    {
        _fixture = fixture;
        _gds = new GdsAvailability(fixture.TransactionRunner, NullLogger<GdsAvailability>.Instance);
        _pageRank = new MemoryPageRankService(
            fixture.TransactionRunner, _gds, Options.Create(new GdsAnalyticsOptions()), NullLogger<MemoryPageRankService>.Instance);
        _community = new MemoryCommunityService(
            fixture.TransactionRunner, _gds, NullLogger<MemoryCommunityService>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // alice: a hub `h` pointed to by 3 spokes (so `h` has the highest PageRank). bob: a separate pair.
    private async Task SeedAsync()
    {
        await using var session = _fixture.Driver.AsyncSession();
        await session.RunAsync(@"
            CREATE (h:Entity  {id:'h',  owner_id:'alice'}),
                   (s1:Entity {id:'s1', owner_id:'alice'}),
                   (s2:Entity {id:'s2', owner_id:'alice'}),
                   (s3:Entity {id:'s3', owner_id:'alice'}),
                   (b1:Entity {id:'b1', owner_id:'bob'}),
                   (b2:Entity {id:'b2', owner_id:'bob'})
            CREATE (s1)-[:RELATED_TO]->(h), (s2)-[:RELATED_TO]->(h), (s3)-[:RELATED_TO]->(h),
                   (b1)-[:RELATED_TO]->(b2)");
    }

    [Fact]
    public async Task GdsAvailability_OnGdsEnabledNeo4j_IsTrue()
    {
        (await _gds.IsAvailableAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task PageRank_Unscoped_RanksTheHubHighest_AndIncludesAllOwners()
    {
        await SeedAsync();

        var ranks = await _pageRank.RankEntitiesAsync(topN: 10);

        ranks.Should().NotBeEmpty();
        ranks[0].EntityId.Should().Be("h", "the hub is pointed to by the most nodes");
        ranks.Select(r => r.EntityId).Should().Contain(new[] { "b1", "b2" }, "unscoped ranking spans all owners");
    }

    [Fact]
    public async Task PageRank_ScopedToOwner_ExcludesOtherOwnersEntities()
    {
        await SeedAsync();

        var ranks = await _pageRank.RankEntitiesAsync(topN: 10, scope: MemoryScope.For("alice"));

        var ids = ranks.Select(r => r.EntityId).ToList();
        ids.Should().Contain("h").And.Contain("s1");
        ids.Should().NotContain("b1").And.NotContain("b2", "a scoped projection never includes another owner's entities");
        ranks[0].EntityId.Should().Be("h");
    }

    [Fact]
    public async Task Community_ScopedToOwner_ClustersOwnedEntities_WithoutOtherOwners()
    {
        await SeedAsync();

        var communities = await _community.DetectCommunitiesAsync(MemoryScope.For("alice"));

        var ids = communities.Select(c => c.EntityId).ToList();
        ids.Should().BeEquivalentTo(new[] { "h", "s1", "s2", "s3" });
        // alice's four connected entities form a single community.
        communities.Select(c => c.CommunityId).Distinct().Should().HaveCount(1);
    }

    [Fact]
    public async Task ScopedProjection_IgnoresForeignEdgeBetweenSharedNodes()
    {
        // Two SHARED nodes (x, y) connected only by a BOB-owned edge; an alice-owned edge connects az→x.
        // For alice's scoped projection the nodes are all visible, but bob's edge must be excluded — so y
        // stays disconnected from x. (Without the edge-owner filter, bob's x→y edge would join them.)
        await using (var session = _fixture.Driver.AsyncSession())
        {
            await session.RunAsync(@"
                CREATE (x:Entity  {id:'x',  owner_id:null}),
                       (y:Entity  {id:'y',  owner_id:null}),
                       (az:Entity {id:'az', owner_id:'alice'})
                CREATE (x)-[:RELATED_TO {owner_id:'bob'}]->(y),
                       (az)-[:RELATED_TO {owner_id:'alice'}]->(x)");
        }

        var communities = await _community.DetectCommunitiesAsync(MemoryScope.For("alice"));

        var byEntity = communities.ToDictionary(c => c.EntityId, c => c.CommunityId);
        byEntity.Keys.Should().Contain(new[] { "x", "y", "az" });
        byEntity["x"].Should().Be(byEntity["az"], "the alice-owned edge connects az and x");
        byEntity["y"].Should().NotBe(byEntity["x"], "bob's edge must not connect y to x in alice's scoped projection");
    }

    [Fact]
    public async Task Community_Unscoped_SeparatesDisconnectedOwners()
    {
        await SeedAsync();

        var communities = await _community.DetectCommunitiesAsync();

        var byEntity = communities.ToDictionary(c => c.EntityId, c => c.CommunityId);
        byEntity["h"].Should().Be(byEntity["s1"]);                 // alice's cluster
        byEntity["b1"].Should().Be(byEntity["b2"]);                 // bob's cluster
        byEntity["h"].Should().NotBe(byEntity["b1"], "disconnected subgraphs are distinct communities");
    }
}
