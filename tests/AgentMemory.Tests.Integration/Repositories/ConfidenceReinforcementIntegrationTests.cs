using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// Confidence that earns and loses (S2), verified where the arithmetic actually runs.
/// </summary>
/// <remarks>
/// <para>
/// Confidence was set once by extraction and never moved: a fact stated five times and one stated in
/// passing carried whatever number the extractor happened to report. With α &gt; 0 a re-asserted
/// triple gains α and a superseded one loses 2α.
/// </para>
/// <para>
/// <b>Asymmetric deliberately.</b> Being contradicted is stronger evidence against a fact than one
/// more restatement is for it — a repeated claim may just be a habit of phrasing, while a replaced
/// one is a claim the world stopped believing.
/// </para>
/// <para>
/// The clamping is the part that has to be checked in the database. Confidence is read by ranking,
/// dedup and decay; a value that escaped [0,1] would propagate into computations where it means
/// nothing, and Cypher arithmetic is where such an escape would happen.
/// </para>
/// </remarks>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class ConfidenceReinforcementIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;

    private const double Alpha = 0.05;

    public ConfidenceReinforcementIntegrationTests(Neo4jIntegrationFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private Neo4jFactRepository Repository(double alpha) =>
        new(_fixture.TransactionRunner,
            NullLogger<Neo4jFactRepository>.Instance,
            memoryOptions: Options.Create(new MemoryOptions { ConfidenceReinforcementAlpha = alpha }));

    private static Fact NewFact(string @object, double confidence) => new()
    {
        FactId = $"fact-{Guid.NewGuid():N}",
        Subject = "user",
        Predicate = "lives in",
        Object = @object,
        Confidence = confidence,
        OwnerId = "alice",
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task ReAssertionRaisesConfidence()
    {
        // The world said it again, so the claim earns rather than being overwritten by whatever the
        // latest extraction reported.
        var repo = Repository(Alpha);
        var stored = await repo.UpsertAsync(NewFact("Zurich", 0.50));
        await repo.UpsertAsync(NewFact("Zurich", 0.50));

        var reinforced = await repo.GetByIdAsync(stored.FactId);

        reinforced!.Confidence.Should().BeApproximately(0.55, 1e-9);
    }

    [Fact]
    public async Task RepeatedReAssertionAccumulatesButNeverExceedsOne()
    {
        // The upper clamp, exercised past the boundary rather than near it. Confidence is a [0,1]
        // quantity every ranking and dedup computation reads.
        var repo = Repository(0.2);
        var stored = await repo.UpsertAsync(NewFact("Zurich", 0.90));
        for (var i = 0; i < 5; i++)
            await repo.UpsertAsync(NewFact("Zurich", 0.90));

        var reinforced = await repo.GetByIdAsync(stored.FactId);

        reinforced!.Confidence.Should().Be(1.0);
    }

    [Fact]
    public async Task BeingSupersededCostsTwiceWhatCorroborationEarns()
    {
        // The asymmetry, measured: 0.60 - 2*0.05 = 0.50. Being contradicted is stronger evidence
        // against a fact than one more restatement is for it.
        var repo = Repository(Alpha);
        var loser = await repo.UpsertAsync(NewFact("Basel", 0.60));
        var winner = await repo.UpsertAsync(NewFact("Zurich", 0.90));

        await repo.SupersedeAsync(loser.FactId, winner.FactId, MemoryScope.For("alice", includeShared: false));

        var demoted = await repo.GetByIdAsync(loser.FactId);
        demoted!.Confidence.Should().BeApproximately(0.50, 1e-9);
    }

    [Fact]
    public async Task ConfidenceNeverGoesNegative()
    {
        // The lower clamp. A negative confidence would be read by ranking and decay as a number, not
        // as an error, and would propagate somewhere it means nothing.
        var repo = Repository(0.4);
        var loser = await repo.UpsertAsync(NewFact("Basel", 0.10));
        var winner = await repo.UpsertAsync(NewFact("Zurich", 0.90));

        await repo.SupersedeAsync(loser.FactId, winner.FactId, MemoryScope.For("alice", includeShared: false));

        (await repo.GetByIdAsync(loser.FactId))!.Confidence.Should().Be(0.0);
    }

    [Fact]
    public async Task TheWinnerOfASupersessionIsUntouched()
    {
        // Only the loser is demoted. Moving both would make every contradiction erode the graph.
        var repo = Repository(Alpha);
        var loser = await repo.UpsertAsync(NewFact("Basel", 0.60));
        var winner = await repo.UpsertAsync(NewFact("Zurich", 0.90));

        await repo.SupersedeAsync(loser.FactId, winner.FactId, MemoryScope.For("alice", includeShared: false));

        (await repo.GetByIdAsync(winner.FactId))!.Confidence.Should().BeApproximately(0.90, 1e-9);
    }

    [Fact]
    public async Task AtAlphaZeroTheBehaviourIsExactlyWhatItWas()
    {
        // The byte-identical guarantee, and the reason the gate lives in Cypher rather than in C#: at
        // alpha 0 the assignment is the original one, so every sealed measurement stands.
        var repo = Repository(0.0);
        var stored = await repo.UpsertAsync(NewFact("Zurich", 0.50));
        await repo.UpsertAsync(NewFact("Zurich", 0.70));

        var after = await repo.GetByIdAsync(stored.FactId);

        after!.Confidence.Should().BeApproximately(0.70, 1e-9,
            "with reinforcement off, the latest assertion's confidence replaces the stored one");
    }

    [Fact]
    public async Task ADifferentFactDoesNotReinforce()
    {
        // Corroboration is per triple. If any write about the same subject reinforced, one chatty
        // subject would drag every fact about it upward.
        var repo = Repository(Alpha);
        var lives = await repo.UpsertAsync(NewFact("Zurich", 0.50));
        await repo.UpsertAsync(new Fact
        {
            FactId = $"fact-{Guid.NewGuid():N}",
            Subject = "user",
            Predicate = "works at",
            Object = "Acme",
            Confidence = 0.5,
            OwnerId = "alice",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });

        (await repo.GetByIdAsync(lives.FactId))!.Confidence.Should().BeApproximately(0.50, 1e-9);
    }
}
