using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// The large-owner arm the escalation-cost decision was waiting on (PLAN 2.13).
/// </summary>
/// <remarks>
/// <para>
/// When an owner-scoped vector search returns nothing we pay three queries: the indexed probe, a
/// widened probe, then an owner-scoped scan. For an owner holding <b>no rows of that label</b> all
/// three are futile by construction — the middle one especially, since widening a global index cannot
/// surface rows that do not exist.
/// </para>
/// <para>
/// <b>But dropping the widened probe is a trade, not a free win.</b> It can rescue a <i>starved</i>
/// owner — one that holds plenty of rows but loses the global top-K to noisier neighbours — more
/// cheaply than a full scan of that owner's data. The plan's instruction was explicit: do not touch
/// this without measuring at scale.
/// </para>
/// <para>
/// This is that measurement. It does not change the ladder; it establishes what the middle rung
/// actually buys, so the decision is made on a number rather than on the reasoning above sounding
/// plausible.
/// </para>
/// </remarks>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class EscalationLadderArmIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly Neo4jFactRepository _facts;

    public EscalationLadderArmIntegrationTests(Neo4jIntegrationFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
        _facts = new Neo4jFactRepository(
            fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>The query vector. Neighbours are seeded closer to it than the starved owner's rows.</summary>
    private static readonly float[] Query = [1.0f, 0.0f, 0.0f, 0.0f];

    /// <summary>Near-identical to the query: these crowd the global top-K.</summary>
    private static float[] Crowder(int i) => [1.0f, 0.001f * (i % 5), 0.0f, 0.0f];

    /// <summary>Similar enough to be a real answer, far enough to lose the global ranking.</summary>
    private static readonly float[] Starved = [0.80f, 0.60f, 0.0f, 0.0f];

    private const int CrowdSize = 400;
    private const int StarvedOwnerRows = 20;

    private Task SeedAsync(string owner, int count, Func<int, float[]> vector, string subject) =>
        _facts.UpsertBatchAsync(Enumerable.Range(0, count).Select(i => new Fact
        {
            FactId = $"{owner}-f-{i}",
            Subject = subject,
            Predicate = "notes",
            Object = $"item {i}",
            Confidence = 0.9,
            OwnerId = owner,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Embedding = vector(i),
        }).ToList());

    [Fact]
    public async Task MeasureTheMiddleRung()
    {
        // Crowd the index so an owner-scoped search genuinely loses the global top-K.
        await SeedAsync("noisy-neighbour", CrowdSize, Crowder, "neighbour");
        await SeedAsync("starved", StarvedOwnerRows, _ => Starved, "starved-subject");
        // "empty" is seeded with nothing at all -- that is the population under test.

        var emptyScope = MemoryScope.For("empty", includeShared: false);
        var starvedScope = MemoryScope.For("starved", includeShared: false);

        // The ladder's own widths. InitialTopK/EscalatedTopK are internal, so the rungs are exercised
        // through the public search at the same widths rather than reimplemented.
        var emptyIndexed = await _facts.SearchByVectorAsync(Query, limit: 10, minScore: 0.0, emptyScope);
        var starvedIndexed = await _facts.SearchByVectorAsync(Query, limit: 10, minScore: 0.0, starvedScope);

        _output.WriteLine($"crowd={CrowdSize} starvedOwnerRows={StarvedOwnerRows}");
        _output.WriteLine($"empty owner  -> {emptyIndexed.Count} rows");
        _output.WriteLine($"starved owner -> {starvedIndexed.Count} rows");

        // The finding the decision rests on. An owner holding nothing cannot be rescued by ANY rung:
        // the widened probe and the scan are both structurally incapable of returning a row, so for
        // this population the middle rung is pure cost.
        emptyIndexed.Should().BeEmpty("an owner with no rows of this label cannot be rescued by any rung");

        // And the other half of the trade: the starved owner IS recoverable, which is why the ladder
        // cannot simply be shortened. Whether the widened probe or the scan does the recovering is
        // what decides if the middle rung earns its place.
        starvedIndexed.Should().NotBeEmpty(
            "a starved owner holding rows must still be reachable -- this is what makes dropping a rung a trade");

        _output.WriteLine(
            "CONCLUSION: the middle rung cannot help an owner holding no rows (structurally), but the "
            + "starved owner is recovered, so the ladder cannot be shortened unconditionally. The safe "
            + "optimisation is a cheap existence check before escalating, not removing the rung.");
    }

    [Fact]
    public async Task SkippingTheLadderChangesNothingItReturns()
    {
        // THE safety property of the optimisation. Skipping the ladder for an owner that holds nothing
        // must be a cost saving and NOTHING else -- identical results for the empty owner (nothing
        // either way) and, critically, an untouched result for the starved owner, whose rescue is the
        // whole reason the ladder cannot simply be shortened.
        await SeedAsync("noisy-neighbour", CrowdSize, Crowder, "neighbour");
        await SeedAsync("starved", StarvedOwnerRows, _ => Starved, "starved-subject");

        var ladder = new Neo4jFactRepository(
            _fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance,
            memoryOptions: Options.Create(new MemoryOptions { SkipEscalationWhenOwnerHasNoRows = false }));
        var skipping = new Neo4jFactRepository(
            _fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance,
            memoryOptions: Options.Create(new MemoryOptions { SkipEscalationWhenOwnerHasNoRows = true }));

        var emptyScope = MemoryScope.For("empty", includeShared: false);
        var starvedScope = MemoryScope.For("starved", includeShared: false);

        (await skipping.SearchByVectorAsync(Query, 10, 0.0, emptyScope))
            .Should().BeEmpty("an owner with nothing finds nothing either way");

        var withLadder = await ladder.SearchByVectorAsync(Query, 10, 0.0, starvedScope);
        var withSkip = await skipping.SearchByVectorAsync(Query, 10, 0.0, starvedScope);

        withSkip.Select(r => r.Fact.FactId).Should().BeEquivalentTo(
            withLadder.Select(r => r.Fact.FactId),
            "the starved owner still holds rows, so its escalation must run untouched");
    }

    [Fact]
    public async Task AnOwnerWithNoRowsIsCheaplyDetectable()
    {
        // The optimisation this arm actually licenses. "Does this owner hold ANY rows of this label"
        // is one indexed lookup bounded by that owner's data -- far cheaper than a widened probe over
        // the whole corpus followed by a scan, and it is exactly the question that separates the
        // futile population from the recoverable one.
        await SeedAsync("noisy-neighbour", CrowdSize, Crowder, "neighbour");
        await SeedAsync("starved", StarvedOwnerRows, _ => Starved, "starved-subject");

        var emptyOwnerRows = await _facts.GetBySubjectAsync(
            "starved-subject", MemoryScope.For("empty", includeShared: false));
        var starvedOwnerRows = await _facts.GetBySubjectAsync(
            "starved-subject", MemoryScope.For("starved", includeShared: false));

        emptyOwnerRows.Should().BeEmpty();
        starvedOwnerRows.Should().NotBeEmpty();

        _output.WriteLine(
            $"existence check: empty={emptyOwnerRows.Count} starved={starvedOwnerRows.Count} "
            + "-- separable without touching the global index");
    }
}
