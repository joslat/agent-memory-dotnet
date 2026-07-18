using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using AgentMemory.Nams.Client;
using AgentMemory.Nams.Domain;

namespace AgentMemory.Tests.Integration.Nams;

/// <summary>
/// Live tests for the Phase 10i TCK Platinum Cypher query console: <see cref="INamsClient.ExecuteCypherQueryAsync"/>.
/// See <c>docs/reviews/NAMS_Phase10i_CypherQueryConsole_PlanningAndImplementationPlan.md</c> for the design and
/// for why this phase specifically required the user's explicit go-ahead before implementation (unlike Phase
/// 10e-10h). <see cref="ExecuteCypherQueryAsync_WriteAttempt_IsRejectedByTheServer"/> is the single most
/// important test in this phase: it re-confirms, as part of the merged suite (not just a one-off research
/// probe), that NAMS's read-only enforcement is a real server-side guarantee -- the property this whole
/// capability's approval was conditioned on.
/// </summary>
[Collection("NAMS Live")]
[Trait("Category", "Integration")]
public sealed class NamsCypherQueryTests
{
    private readonly NamsLiveFixture _fixture;

    public NamsCypherQueryTests(NamsLiveFixture fixture) => _fixture = fixture;

    [LiveNamsFact]
    public async Task ExecuteCypherQueryAsync_ReadQuery_ReturnsRealResults()
    {
        var namsClient = _fixture.Services!.GetRequiredService<INamsClient>();

        var result = await namsClient.ExecuteCypherQueryAsync(
            "MATCH (n) RETURN count(n) AS cnt", parameters: null, CancellationToken.None);

        result.Columns.Should().Equal("cnt");
        result.Rows.Should().ContainSingle();
        result.Rows[0]["cnt"].GetInt32().Should().BeGreaterThan(0,
            "this live dev workspace has accumulated real graph data from every prior phase's live tests");
    }

    [LiveNamsFact]
    public async Task ExecuteCypherQueryAsync_WriteAttempt_IsRejectedByTheServer()
    {
        var namsClient = _fixture.Services!.GetRequiredService<INamsClient>();

        var act = () => namsClient.ExecuteCypherQueryAsync(
            "CREATE (n:Phase10iShouldNeverExist {marker: 1}) RETURN n", parameters: null, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NamsOperationException>(
            "NAMS must enforce read-only server-side -- this is the empirical guarantee this whole " +
            "capability's approval was conditioned on, not just a documentation claim");
        exception.Which.FailureKind.Should().Be(NamsFailureKind.Validation);
    }

    [LiveNamsFact]
    public async Task ExecuteCypherQueryAsync_WithParameters_SubstitutesThemCorrectly()
    {
        var namsClient = _fixture.Services!.GetRequiredService<INamsClient>();
        var knownEntityId = await NamsLiveTestHelpers.GetAnyExistingEntityIdAsync(namsClient, limit: 1, CancellationToken.None);

        var result = await namsClient.ExecuteCypherQueryAsync(
            "MATCH (n) WHERE n.id = $id RETURN n.id AS id",
            new Dictionary<string, object?> { ["id"] = knownEntityId },
            CancellationToken.None);

        result.Rows.Should().ContainSingle(
            "the parameter must genuinely substitute into the query -- a silently-ignored or malformed " +
            "$id parameter would either match everything or nothing, not exactly the one entity queried");
        result.Rows[0]["id"].GetString().Should().Be(knownEntityId);
    }
}
