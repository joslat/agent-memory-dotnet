using FluentAssertions;
using AgentMemory.Neo4j.Queries;
using AgentMemory.Tests.Integration.Fixtures;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// RVW-REST-IDENTITY (the review's identity-4): the exact identity lookup runs for every mention, at planning, at
/// revalidation and on replay. As one WHERE (name OR key OR EXISTS) no index could serve it, and with shared memory
/// included (the default) the planner scanned every Entity in the store. Each way of naming now has its own index-backed
/// branch; this pins the plan, on the indexes the identity extension creates.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class IdentityExactLookupPlanIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;

    public IdentityExactLookupPlanIntegrationTests(Neo4jIntegrationFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.CleanDatabaseAsync();
        foreach (var index in new[]
        {
            "CREATE INDEX entity_name_idx IF NOT EXISTS FOR (e:Entity) ON (e.name)",
            "CREATE INDEX identity_calendar_key IF NOT EXISTS FOR (e:Entity) ON (e.identity_calendar_key)",
            "CREATE INDEX identity_evidence_lookup IF NOT EXISTS FOR (a:IdentityEvidenceAssertion) ON (a.lookup_key)",
            "CREATE INDEX identity_evidence_surface IF NOT EXISTS FOR (a:IdentityEvidenceAssertion) ON (a.namespace, a.surface)",
            "CREATE CONSTRAINT identity_decision_observation IF NOT EXISTS FOR (d:IdentityDecision) REQUIRE (d.owner_scope, d.identity_observation_key) IS UNIQUE",
        })
        {
            await using var session = _fixture.Driver.AsyncSession();
            await (await session.RunAsync(index)).ConsumeAsync();
        }
        await using (var session = _fixture.Driver.AsyncSession())
            await (await session.RunAsync("CALL db.awaitIndexes(60)")).ConsumeAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Every_way_of_naming_is_an_index_seek_and_no_entity_is_scanned(bool includeShared)
    {
        await using var session = _fixture.Driver.AsyncSession();
        var summary = await (await session.RunAsync("EXPLAIN " + IdentityEvidenceQueries.ExactIds, new Dictionary<string, object?>
        {
            ["ownerId"] = "owner-a", ["includeShared"] = includeShared, ["types"] = new[] { "PERSON" }, ["name"] = "Dana Whitfield",
            ["calendarKey"] = "2026-12-12", ["evidenceScopes"] = new[] { "owned:owner-a", "shared" }, ["keyLookups"] = new[] { "k1" },
            ["namespace"] = "person", ["sentinelLimit"] = 65,
        })).ConsumeAsync();

        var operators = Operators(summary.Plan).ToList();
        operators.Should().NotContain(op => op.StartsWith("NodeByLabelScan", StringComparison.Ordinal),
            "a mention's exact lookup must not read every entity in the store");
        operators.Count(op => op.StartsWith("NodeIndexSeek", StringComparison.Ordinal)).Should().BeGreaterThanOrEqualTo(4,
            "name, calendar key, key assertion and alias assertion each seek by their index");
    }

    private static IEnumerable<string> Operators(IPlan plan)
    {
        yield return plan.OperatorType;
        foreach (var child in plan.Children)
            foreach (var op in Operators(child))
                yield return op;
    }
}
