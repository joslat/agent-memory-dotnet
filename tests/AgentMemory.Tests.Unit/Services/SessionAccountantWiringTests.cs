using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Domain.Extraction;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Extraction.Derivation;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// 30.6 steps 2 and 7. The flag reaches the accountant, and with it off <b>nothing happens at all</b>.
/// </summary>
/// <remarks>
/// <para>
/// The off-state assertion is stricter than "the graph is unchanged": it asserts the repository saw
/// <b>zero</b> calls. A feature that reads a group, computes nothing, and writes nothing would leave an
/// identical graph while adding a query per touched group to every ingestion — off has to mean off in
/// cost as well as in effect, or every latency measurement taken afterwards is measuring a different
/// system than the one the archive describes.
/// </para>
/// <para>
/// The on-state assertions are about <b>reachability</b>: an option that binds, validates, and reaches
/// no code is the defect this project has now found fifteen times, and an arithmetic feature would wear
/// it especially well — the symptom is simply that certain aggregates never appear.
/// </para>
/// </remarks>
public sealed class SessionAccountantWiringTests
{
    private readonly IFactRepository _facts = Substitute.For<IFactRepository>();
    private readonly IEmbeddingOrchestrator _embeddings = Substitute.For<IEmbeddingOrchestrator>();
    private readonly IIdGenerator _ids = Substitute.For<IIdGenerator>();
    private readonly IClock _clock = Substitute.For<IClock>();

    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    public SessionAccountantWiringTests()
    {
        _clock.UtcNow.Returns(Now);
        _ids.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));
        _embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f });
        _facts.UpsertDerivedAsync(
                Arg.Any<Fact>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Fact>());
    }

    private SessionAccountant Create(Action<DerivedMemoryOptions>? configure = null)
    {
        var options = new ExtractionOptions();
        options.DerivedMemory.Enabled = true;
        configure?.Invoke(options.DerivedMemory);
        return new SessionAccountant(
            _facts, _embeddings, _ids, _clock,
            Options.Create(options), NullLogger<SessionAccountant>.Instance);
    }

    private static ExtractionStageResult Staged(params (string Subject, string Predicate)[] facts) => new()
    {
        FilteredFacts =
        [
            .. facts.Select(f => new ExtractedFact
            {
                Subject = f.Subject, Predicate = f.Predicate, Object = "irrelevant", Confidence = 0.9,
            }),
        ],
    };

    private void GroupReturns(params Fact[] facts) =>
        _facts.GetGroupFactsAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MemoryScope?>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(facts);

    private static Fact Live(string id, string @object, int order) => new()
    {
        FactId = id, Subject = "user", Predicate = "savings_balance", Object = @object,
        Confidence = 0.9, CreatedAtUtc = Now.AddDays(order),
    };

    // ── off ───────────────────────────────────────────────────────────

    [Fact]
    public async Task WithTheFlagOffTheRepositoryIsNeverTouched()
    {
        var options = new ExtractionOptions();  // Enabled defaults false
        var accountant = new SessionAccountant(
            _facts, _embeddings, _ids, _clock,
            Options.Create(options), NullLogger<SessionAccountant>.Instance);

        var written = await accountant.AccountAsync(
            Staged(("user", "savings_balance")), "alice");

        written.Should().Be(0);
        await _facts.DidNotReceive().GetGroupFactsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MemoryScope?>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _facts.DidNotReceive().UpsertDerivedAsync(
            Arg.Any<Fact>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        await _embeddings.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnEmptyOperatorSetIsAlsoOff()
    {
        // Enabled=true with Operators=None is a configuration that reads as "on" and can produce
        // nothing. Reading the group anyway would spend a query per touched group for a guaranteed
        // empty result.
        var accountant = Create(o => o.Operators = DerivationOperators.None);

        await accountant.AccountAsync(Staged(("user", "savings_balance")), "alice");

        await _facts.DidNotReceive().GetGroupFactsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MemoryScope?>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── on ────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithTheFlagOnTheTouchedGroupIsReadAndAggregatesAreWritten()
    {
        GroupReturns(Live("a", "800", 0), Live("b", "50", 1));
        var accountant = Create();

        var written = await accountant.AccountAsync(Staged(("user", "savings_balance")), "alice");

        written.Should().BeGreaterThan(0);
        await _facts.Received(1).GetGroupFactsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MemoryScope?>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EveryEnabledOperatorThatProducesSomethingIsWritten()
    {
        // Count, Delta and Latest all fire on a two-value numeric chain; SetEnumeration too, since the
        // values differ. Asserting the SET rather than a total catches an operator that silently stops
        // running while the count stays plausible.
        GroupReturns(Live("a", "800", 0), Live("b", "50", 1));
        var accountant = Create();

        await accountant.AccountAsync(Staged(("user", "savings_balance")), "alice");

        var written = _facts.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IFactRepository.UpsertDerivedAsync))
            .Select(call => ((Fact)call.GetArguments()[0]!).Metadata.GetDerivationOperator())
            .ToList();

        written.Should().BeEquivalentTo(
        [
            DerivationOperators.Count, DerivationOperators.Delta,
            DerivationOperators.Latest, DerivationOperators.SetEnumeration,
        ]);
    }

    [Fact]
    public async Task ADisabledOperatorNeverRunsEvenWhenItsGroupWouldProduceOne()
    {
        GroupReturns(Live("a", "800", 0), Live("b", "50", 1));
        var accountant = Create(o => o.Operators = DerivationOperators.Count);

        await accountant.AccountAsync(Staged(("user", "savings_balance")), "alice");

        await _facts.Received(1).UpsertDerivedAsync(
            Arg.Is<Fact>(f => f.Metadata.GetDerivationOperator() == DerivationOperators.Count),
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        await _facts.DidNotReceive().UpsertDerivedAsync(
            Arg.Is<Fact>(f => f.Metadata.GetDerivationOperator() == DerivationOperators.Delta),
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ADerivedFactCarriesItsProvenanceAndItsInputs()
    {
        GroupReturns(Live("a", "800", 0), Live("b", "50", 1));
        var accountant = Create(o => o.Operators = DerivationOperators.Delta);

        await accountant.AccountAsync(Staged(("user", "savings_balance")), "alice");

        await _facts.Received(1).UpsertDerivedAsync(
            Arg.Is<Fact>(f =>
                f.Object == "-750" &&
                f.OwnerId == "alice" &&
                f.Metadata.IsDerived() &&
                f.Metadata.GetInputFactIds().Count == 2),
            Arg.Is<IReadOnlyList<string>>(ids => ids.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ADerivedFactIsUntrustedLikeEverythingElseComputedFromUserText()
    {
        // Being arithmetic does not make its inputs trustworthy: it renders through the same admission
        // machinery as any other recalled item and earns no bypass.
        GroupReturns(Live("a", "800", 0), Live("b", "50", 1));
        var accountant = Create(o => o.Operators = DerivationOperators.Count);

        await accountant.AccountAsync(Staged(("user", "savings_balance")), "alice");

        await _facts.Received(1).UpsertDerivedAsync(
            Arg.Is<Fact>(f => f.Metadata.GetTrustLevel() == MemoryTrustLevel.Untrusted),
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    // ── incrementality and scope ──────────────────────────────────────

    [Fact]
    public async Task OnlyTheGroupsThisBatchTouchedAreRead()
    {
        // A full sweep would recompute the whole graph every turn and still be wrong in the same
        // places: a group nothing touched cannot have changed.
        GroupReturns(Live("a", "1", 0), Live("b", "2", 1));
        var accountant = Create();

        await accountant.AccountAsync(
            Staged(("user", "savings_balance"), ("user", "savings_balance")), "alice");

        await _facts.Received(1).GetGroupFactsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MemoryScope?>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheGroupReadIsOwnerScopedAndNeverIncludesShared()
    {
        // A group read mixing a tenant's facts with global ones would compute an aggregate spanning
        // both and store the result under one owner.
        GroupReturns(Live("a", "1", 0), Live("b", "2", 1));
        var accountant = Create();

        await accountant.AccountAsync(Staged(("user", "savings_balance")), "alice");

        await _facts.Received(1).GetGroupFactsAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<MemoryScope?>(s => s != null && s.OwnerId == "alice" && !s.IncludeShared),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AGroupWithFewerThanTwoFactsCostsNoEmbedding()
    {
        GroupReturns(Live("a", "800", 0));
        var accountant = Create();

        var written = await accountant.AccountAsync(Staged(("user", "savings_balance")), "alice");

        written.Should().Be(0);
        await _embeddings.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── failure posture ───────────────────────────────────────────────

    [Fact]
    public async Task AFailingGroupDoesNotTakeDownTheIngestion()
    {
        // This runs after persistence: the batch's facts are already stored, and throwing here would
        // trade a missing convenience for a failed write.
        _facts.GetGroupFactsAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MemoryScope?>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<Fact>>>(_ => throw new InvalidOperationException("graph down"));
        var accountant = Create();

        var act = async () => await accountant.AccountAsync(Staged(("user", "x")), "alice");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task AFailingEmbeddingStillStoresTheAggregate()
    {
        // The back-fill picks up facts with a null embedding, so an unembedded aggregate becomes
        // retrievable later rather than never.
        _embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<float[]>>(_ => throw new InvalidOperationException("embedding provider down"));
        GroupReturns(Live("a", "800", 0), Live("b", "50", 1));
        var accountant = Create(o => o.Operators = DerivationOperators.Count);

        await accountant.AccountAsync(Staged(("user", "savings_balance")), "alice");

        await _facts.Received(1).UpsertDerivedAsync(
            Arg.Is<Fact>(f => f.Embedding == null),
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheBatchCapIsHonoured()
    {
        GroupReturns(Live("a", "800", 0), Live("b", "50", 1));
        var accountant = Create(o => o.MaxDerivedFactsPerBatch = 1);

        var written = await accountant.AccountAsync(
            Staged(("user", "a_predicate"), ("user", "b_predicate")), "alice");

        // The cap is checked between groups, so one group's operators all land and the next group is
        // skipped -- deliberate, because abandoning a group half-computed would store some of its
        // aggregates and not others, which is worse than storing none.
        written.Should().BeGreaterThan(0);
        await _facts.Received(1).GetGroupFactsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MemoryScope?>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
