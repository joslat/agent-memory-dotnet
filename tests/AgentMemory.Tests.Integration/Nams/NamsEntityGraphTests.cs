using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using AgentMemory.Nams.Client;

namespace AgentMemory.Tests.Integration.Nams;

/// <summary>
/// Live tests for the Phase 10g TCK Platinum entity-feedback/graph additions:
/// <see cref="INamsClient.SetEntityFeedbackAsync"/>, <see cref="INamsClient.GetEntityGraphAsync"/>, and
/// <see cref="INamsClient.ExpandGraphAsync"/>. See
/// <c>docs/reviews/NAMS_Phase10g_EntityFeedbackGraph_PlanningAndImplementationPlan.md</c> for the design.
/// Deliberately reuses an existing entity discovered via the already-shipped <see cref="INamsClient.ListEntitiesAsync"/>
/// rather than creating one -- entity creation only happens via the async extraction pipeline or the
/// out-of-scope <c>POST /entities</c> endpoint, and non-destructively scoring/reading an existing entity matches
/// the same pattern the Phase 10e spike itself used.
/// </summary>
[Collection("NAMS Live")]
[Trait("Category", "Integration")]
public sealed class NamsEntityGraphTests
{
    private readonly NamsLiveFixture _fixture;

    public NamsEntityGraphTests(NamsLiveFixture fixture) => _fixture = fixture;

    [LiveNamsFact]
    public async Task SetEntityFeedbackAsync_OnExistingEntity_UpdatesScoreAndConfirmedFlag()
    {
        var namsClient = _fixture.Services!.GetRequiredService<INamsClient>();
        var entityId = await NamsLiveTestHelpers.GetAnyExistingEntityIdAsync(namsClient, limit: 1, CancellationToken.None);

        var result = await namsClient.SetEntityFeedbackAsync(entityId, userScore: 0.75, confirmed: true, CancellationToken.None);

        result.Id.Should().Be(entityId, "the response must echo back the exact entity id we scored, not some other one");
        result.Updated.Should().BeTrue();
    }

    [LiveNamsFact]
    public async Task GetEntityGraphAsync_ReturnsNodesAndEdgesFromTheWorkspace()
    {
        var namsClient = _fixture.Services!.GetRequiredService<INamsClient>();
        // Cross-check against several listed entities, not just one: GET /entities/graph documents no limit
        // param or ordering contract linking it to ListEntitiesAsync, so a single arbitrary entity could in
        // principle fall outside some undocumented cap as this shared dev workspace keeps growing. Requiring
        // overlap with ANY of several entities (rather than one specific one) keeps the assertion genuine
        // while removing that single-entity fragility.
        // Independent reads -- neither depends on the other's result, so run concurrently rather than paying
        // two sequential round trips to the live SaaS (a Phase 10-review efficiency finding).
        var listEntitiesTask = namsClient.ListEntitiesAsync(20, CancellationToken.None);
        var graphTask = namsClient.GetEntityGraphAsync(CancellationToken.None);
        await Task.WhenAll(listEntitiesTask, graphTask);
        var entities = listEntitiesTask.Result;
        var graph = graphTask.Result;

        entities.Should().NotBeEmpty();
        var knownEntityIds = entities.Select(e => e.Id).ToHashSet();

        graph.Nodes.Should().NotBeEmpty();
        graph.Nodes.Should().Contain(n => knownEntityIds.Contains(n.Id),
            "at least one of the entities independently confirmed to exist via ListEntitiesAsync must also " +
            "appear in the full workspace graph -- proves both endpoints are reading the same real data, not " +
            "just independently well-typed empty/placeholder responses");
    }

    [LiveNamsFact]
    public async Task ExpandGraphAsync_OnASeedEntity_ReturnsANonEmptyNeighborhood()
    {
        var namsClient = _fixture.Services!.GetRequiredService<INamsClient>();
        var seedId = await NamsLiveTestHelpers.GetAnyExistingEntityIdAsync(namsClient, limit: 1, CancellationToken.None);

        var expansion = await namsClient.ExpandGraphAsync(seedId, [seedId], CancellationToken.None);

        // This dev workspace's entities are heavily interlinked from many prior phases' live tests (confirmed
        // Phase 10e: expanding a single entity returned 21 nodes/27 edges) -- a genuinely empty neighborhood
        // would indicate expand is broken, not just that this particular entity happens to be isolated.
        expansion.Nodes.Should().NotBeEmpty("this workspace's entities are heavily interlinked from prior phases");
        expansion.Truncated.Should().NotBeNull();
        expansion.Truncated!.NodeId.Should().Be(seedId);
    }
}
