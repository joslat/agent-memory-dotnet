using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Core.Services.Projection;
using FluentAssertions;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// 30.2 steps 7-8. Supersession notes and conflict blocks.
/// </summary>
public sealed class SupersessionAndConflictProjectionTests
{
    private static readonly DateTimeOffset Stamp = new(2023, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Fact MakeFact(
        string id, string @object = "Acme", string subject = "Bob", string predicate = "works_at",
        string? owner = null, double confidence = 0.9, DateTimeOffset? invalidatedAt = null,
        DateTimeOffset? validFrom = null) => new()
    {
        FactId = id, Subject = subject, Predicate = predicate, Object = @object,
        Confidence = confidence, CreatedAtUtc = Stamp, OwnerId = owner,
        InvalidatedAtUtc = invalidatedAt, ValidFrom = validFrom,
    };

    private static ProjectionState State(
        MemoryProjectionOptions options, params Fact[] facts) => new()
    {
        Options = options,
        Scope = null,
        Entities = [],
        Facts = facts,
        Preferences = [],
        Traces = [],
        RecentMessages = [],
        RelevantMessages = [],
        EntityScores = [],
        FactScores = [],
        PreferenceScores = [],
        TraceScores = [],
    };

    // ── supersession ──────────────────────────────────────────────────

    private static MemoryProjectionOptions SupersessionOn =>
        MemoryProjectionOptions.Default with { ResolveSupersessions = true };

    private static IFactRepository RepoReturning(
        Dictionary<string, IReadOnlyList<SupersededFact>> chains)
    {
        var repo = Substitute.For<IFactRepository>();
        repo.GetSupersessionPredecessorsAsync(
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<SupersededFact>>>(chains));
        return repo;
    }

    [Fact]
    public async Task ASupersededPredecessorBecomesANoteOnTheCurrentFact()
    {
        var state = State(SupersessionOn, MakeFact("f1"));
        var repo = RepoReturning(new()
        {
            ["f1"] = [new SupersededFact("Globex", Stamp, new DateTimeOffset(2023, 5, 12, 0, 0, 0, TimeSpan.Zero))],
        });

        await new SupersessionProjectionFeature(repo).ApplyAsync(state, CancellationToken.None);

        state.Build().Annotations["f1"].SupersessionNote
            .Should().Be("(since 2023-05-12; previously Globex)");
    }

    [Fact]
    public async Task ThePreferredDateIsValidTimeNotTransactionTime()
    {
        // "Since when?" is a question about the world, not about when the database noticed.
        var state = State(SupersessionOn, MakeFact("f1"));
        var repo = RepoReturning(new()
        {
            ["f1"] =
            [
                new SupersededFact(
                    "Globex",
                    InvalidatedAtUtc: new DateTimeOffset(2024, 9, 9, 0, 0, 0, TimeSpan.Zero),
                    ValidUntilUtc: new DateTimeOffset(2023, 5, 12, 0, 0, 0, TimeSpan.Zero)),
            ],
        });

        await new SupersessionProjectionFeature(repo).ApplyAsync(state, CancellationToken.None);

        state.Build().Annotations["f1"].SupersessionNote.Should().Contain("2023-05-12")
            .And.NotContain("2024-09-09");
    }

    [Fact]
    public async Task AnUnstampedPredecessorOmitsTheDateRatherThanInventingOne()
    {
        // A fabricated date inside a temporal cue is worse than no cue.
        var state = State(SupersessionOn, MakeFact("f1"));
        var repo = RepoReturning(new()
        {
            ["f1"] = [new SupersededFact("Globex", null, null)],
        });

        await new SupersessionProjectionFeature(repo).ApplyAsync(state, CancellationToken.None);

        state.Build().Annotations["f1"].SupersessionNote.Should().Be("(previously Globex)");
    }

    [Fact]
    public async Task ALongerChainRendersEarlierValues()
    {
        var state = State(SupersessionOn, MakeFact("f1"));
        var repo = RepoReturning(new()
        {
            ["f1"] =
            [
                new SupersededFact("Globex", null, new DateTimeOffset(2023, 5, 12, 0, 0, 0, TimeSpan.Zero)),
                new SupersededFact("Initech", null, new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            ],
        });

        await new SupersessionProjectionFeature(repo).ApplyAsync(state, CancellationToken.None);

        state.Build().Annotations["f1"].SupersessionNote
            .Should().Be("(since 2023-05-12; previously Globex; earlier Initech)");
    }

    [Fact]
    public async Task TheChainCapIsPassedToTheRepository()
    {
        var state = State(SupersessionOn with { MaxSupersessionChain = 2 }, MakeFact("f1"));
        var repo = RepoReturning([]);

        await new SupersessionProjectionFeature(repo).ApplyAsync(state, CancellationToken.None);

        await repo.Received(1).GetSupersessionPredecessorsAsync(
            Arg.Any<IReadOnlyList<string>>(), 2, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExactlyOneReadIsIssuedForTheWholeFactSection()
    {
        // The one-extra-read-per-recall budget, enforced rather than intended.
        var state = State(SupersessionOn, MakeFact("f1"), MakeFact("f2"), MakeFact("f3"));
        var repo = RepoReturning([]);

        await new SupersessionProjectionFeature(repo).ApplyAsync(state, CancellationToken.None);

        await repo.Received(1).GetSupersessionPredecessorsAsync(
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoFactsMeansNoReadAtAll()
    {
        var repo = RepoReturning([]);

        await new SupersessionProjectionFeature(repo).ApplyAsync(State(SupersessionOn), CancellationToken.None);

        await repo.DidNotReceive().GetSupersessionPredecessorsAsync(
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void TheFeatureIsOffUnlessItsFlagIsSet()
    {
        // Off must mean the repository is never touched -- a read nobody opted into is a latency cost.
        var feature = new SupersessionProjectionFeature(Substitute.For<IFactRepository>());

        feature.IsEnabled(MemoryProjectionOptions.Default).Should().BeFalse();
        feature.IsEnabled(SupersessionOn).Should().BeTrue();
    }

    [Fact]
    public async Task AFactWithNoHistoryGetsNoNote()
    {
        var state = State(SupersessionOn, MakeFact("f1"));
        var repo = RepoReturning([]);

        await new SupersessionProjectionFeature(repo).ApplyAsync(state, CancellationToken.None);

        state.IsEmpty.Should().BeTrue();
    }

    // ── conflicts ─────────────────────────────────────────────────────

    private static MemoryProjectionOptions ConflictsOn =>
        MemoryProjectionOptions.Default with { RenderConflicts = true };

    private static async Task<ProjectionState> RunConflictsAsync(params Fact[] facts)
    {
        var state = State(ConflictsOn, facts);
        await new ConflictProjectionFeature().ApplyAsync(state, CancellationToken.None);
        return state;
    }

    [Fact]
    public async Task TwoLiveFactsWithDifferentObjectsBecomeOneConflictBlock()
    {
        var state = await RunConflictsAsync(
            MakeFact("f1", "Acme"), MakeFact("f2", "Globex"));

        var block = state.Build().Blocks.Should().ContainSingle().Subject;
        block.Kind.Should().Be(ProjectedBlockKind.ConflictingMemory);
        block.Text.Should().Contain("CONFLICTING MEMORY").And.Contain("Acme").And.Contain("Globex");
    }

    [Fact]
    public async Task AgreeingFactsAreNotAConflict()
    {
        var state = await RunConflictsAsync(MakeFact("f1", "Acme"), MakeFact("f2", "Acme"));

        state.Build().Blocks.Should().BeEmpty();
    }

    [Fact]
    public async Task TwoSpellingsOfOneAnswerAreNotAConflict()
    {
        // The most annoying possible false positive: it would teach the model to hedge about something
        // nobody disagrees on. Grouped the way the write path canonicalises a triple.
        var state = await RunConflictsAsync(MakeFact("f1", "Acme"), MakeFact("f2", " acme "));

        state.Build().Blocks.Should().BeEmpty();
    }

    [Fact]
    public async Task ASupersededFactIsHistoryNotACompetingClaim()
    {
        // It would also contradict the supersession note the sibling feature attaches to the same pair.
        var state = await RunConflictsAsync(
            MakeFact("f1", "Acme"),
            MakeFact("f2", "Globex", invalidatedAt: Stamp));

        state.Build().Blocks.Should().BeEmpty();
    }

    [Fact]
    public async Task DifferentOwnersAreTwoTenantsNotAContradiction()
    {
        // Rendering this as a conflict would also leak the existence of another owner's data.
        var state = await RunConflictsAsync(
            MakeFact("f1", "Acme", owner: "alice"),
            MakeFact("f2", "Globex", owner: "bob"));

        state.Build().Blocks.Should().BeEmpty();
    }

    [Fact]
    public async Task DifferentPredicatesAreNotAConflict()
    {
        var state = await RunConflictsAsync(
            MakeFact("f1", "Acme", predicate: "works_at"),
            MakeFact("f2", "Zurich", predicate: "lives_in"));

        state.Build().Blocks.Should().BeEmpty();
    }

    [Fact]
    public async Task TheHigherConfidenceClaimIsRenderedFirst()
    {
        // The block is a cue, not a verdict -- but ordering by confidence gives the reader the better
        // claim first rather than whichever the index happened to return first.
        var state = await RunConflictsAsync(
            MakeFact("f1", "Acme", confidence: 0.4),
            MakeFact("f2", "Globex", confidence: 0.95));

        var text = state.Build().Blocks[0].Text;
        text.IndexOf("Globex", StringComparison.Ordinal)
            .Should().BeLessThan(text.IndexOf("Acme", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EachClaimCarriesADateSoTheReaderCanPreferTheNewer()
    {
        var state = await RunConflictsAsync(
            MakeFact("f1", "Acme", validFrom: new DateTimeOffset(2024, 2, 3, 0, 0, 0, TimeSpan.Zero)),
            MakeFact("f2", "Globex"));

        state.Build().Blocks[0].Text.Should().Contain("2024-02-03");
    }

    [Fact]
    public void TheConflictFeatureIsOffUnlessItsFlagIsSet()
    {
        var feature = new ConflictProjectionFeature();

        feature.IsEnabled(MemoryProjectionOptions.Default).Should().BeFalse();
        feature.IsEnabled(ConflictsOn).Should().BeTrue();
        feature.IsEnabled(MemoryProjectionOptions.Default with { ResolveSupersessions = true })
            .Should().BeFalse();
    }
}
