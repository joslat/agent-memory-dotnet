using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using AgentMemory.Nams.Client;

namespace AgentMemory.Tests.Integration.Nams;

/// <summary>
/// Live tests for the Phase 10g TCK Platinum entity-feedback/graph additions:
/// <see cref="INamsClient.SetEntityFeedbackAsync"/>, <see cref="INamsClient.GetEntityGraphAsync"/>, and
/// <see cref="INamsClient.ExpandGraphAsync"/>. See
/// <c>docs/reviews/NAMS_Phase10g_EntityFeedbackGraph_PlanningAndImplementationPlan.md</c> for the design.
/// Most tests deliberately reuse an existing entity discovered via the already-shipped
/// <see cref="INamsClient.ListEntitiesAsync"/> rather than creating one -- non-destructively scoring/reading an
/// existing entity matches the same pattern the Phase 10e spike itself used.
/// <see cref="INamsClient.CreateEntityAsync"/> (Phase 10j) is tested separately below, added specifically to
/// support the NAMS TCK bridge's <c>add_entity</c> route.
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

        // Deliberately NOT "any entity via ListEntitiesAsync(1)" -- that returns newest-first (confirmed live),
        // and a just-created entity (e.g. from CreateEntityAsync's own live test, or any other test run in the
        // same session) genuinely has zero edges yet, since relationship extraction lags entity creation. This
        // real flakiness was caught while adding Phase 10j's CreateEntityAsync test. Instead, pull the full
        // workspace graph and pick a seed provably referenced by at least one edge -- guaranteed connected,
        // not gambling on freshness.
        var graph = await namsClient.GetEntityGraphAsync(CancellationToken.None);
        var connectedIds = (graph.Edges ?? []).SelectMany(e => new[] { e.SourceId, e.TargetId }).ToHashSet();
        connectedIds.Should().NotBeEmpty("this workspace's entities are heavily interlinked from prior phases");
        var seedId = connectedIds.First();

        var expansion = await namsClient.ExpandGraphAsync(seedId, [seedId], CancellationToken.None);

        expansion.Nodes.Should().NotBeEmpty("the seed was chosen specifically because it has at least one edge");
        expansion.Truncated.Should().NotBeNull();
        expansion.Truncated!.NodeId.Should().Be(seedId);
    }

    [LiveNamsFact]
    public async Task CreateEntityAsync_ReturnsAWellFormedResultForWhicheverResolutionOccurs()
    {
        // Deliberately does NOT assume "created" -- this test ITSELF originally did, using a name sharing the
        // "TckBridgeProbeEntity" prefix already reused across many earlier probes in this same shared dev
        // workspace, and NAMS's own fuzzy entity-resolution auto-merged it ("resolution":"merged") instead of
        // creating a new one, returning a genuinely different, minimal response shape (no name/type at all).
        // See NamsCreateEntityResult's own doc comment for the full three-shape finding this caused. A
        // resolution outcome is inherently probabilistic (semantic/name-similarity-based, not something a
        // caller fully controls), so this test asserts on whichever real outcome occurs rather than gambling
        // that name uniqueness alone guarantees "created".
        var namsClient = _fixture.Services!.GetRequiredService<INamsClient>();
        var marker = Guid.NewGuid().ToString("N");
        var name = $"zzTckBridgeProbe{marker}";

        var result = await namsClient.CreateEntityAsync(
            name, "TestType", "created by CreateEntityAsync_ReturnsAWellFormedResultForWhicheverResolutionOccurs",
            CancellationToken.None);

        result.Id.Should().NotBeNullOrWhiteSpace();
        result.Resolution.Should().NotBeNullOrWhiteSpace();
        if (result.Resolution is "created" or "review_pending")
        {
            result.Name.Should().Be(name,
                "the response must echo back the exact name this test just created, not some other entity");
            result.Type.Should().Be("TestType");
        }
        else
        {
            result.Resolution.Should().Be("merged");
            result.MergedInto.Should().NotBeNullOrWhiteSpace();
            result.Confidence.Should().NotBeNull();
        }
    }
}
