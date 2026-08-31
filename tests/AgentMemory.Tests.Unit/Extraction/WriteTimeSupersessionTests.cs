using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Memory;
using AgentMemory.Core.Services;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// Write-time supersession (M1): a new assertion about a functional relation replaces the one it
/// contradicts, instead of accumulating beside it.
/// </summary>
/// <remarks>
/// <para>
/// The falsifier has <b>two</b> halves and both must hold, because each is trivially satisfiable
/// alone. Superseding everything would shrink the graph beautifully and destroy true facts;
/// superseding nothing keeps every fact and leaves the graph growing with the conversation rather than
/// with what is true. Fewer facts is only a win if nothing true was closed.
/// </para>
/// <para>
/// The dangerous direction is the second one, so it is the one covered hardest: a multi-valued
/// predicate must never be superseded, and "multi-valued" is the default for everything the vocabulary
/// has not explicitly declared functional — including every predicate the extractor invents.
/// </para>
/// </remarks>
public sealed class WriteTimeSupersessionTests
{
    private readonly IEmbeddingOrchestrator _orchestrator = Substitute.For<IEmbeddingOrchestrator>();
    private readonly IEntityRepository _entityRepo = Substitute.For<IEntityRepository>();
    private readonly IFactRepository _factRepo = Substitute.For<IFactRepository>();
    private readonly IPreferenceRepository _prefRepo = Substitute.For<IPreferenceRepository>();
    private readonly IRelationshipRepository _relRepo = Substitute.For<IRelationshipRepository>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IIdGenerator _idGen = Substitute.For<IIdGenerator>();

    private readonly List<(string Loser, string Winner)> _superseded = [];

    public WriteTimeSupersessionTests()
    {
        _clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        _idGen.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));
        _orchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[8]);

        _factRepo.UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<Fact>()));
        _factRepo.FindSupersededCandidatesAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([Stored("Basel")]));
        _factRepo.SupersedeAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _superseded.Add((ci.ArgAt<string>(0), ci.ArgAt<string>(1)));
                return Task.FromResult(true);
            });
    }

    private static Fact Stored(string @object) => new()
    {
        FactId = $"old-{@object}",
        Subject = "user",
        Predicate = "lives in",
        Object = @object,
        Confidence = 0.9,
        CreatedAtUtc = DateTimeOffset.UnixEpoch,
    };

    private readonly CapturingLogger _log = new();

    /// <summary>A logger that keeps what was written, so "it said nothing" is testable.</summary>
    private sealed class CapturingLogger : ILogger<PersistenceStage>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private PersistenceStage CreateSut(bool supersede) =>
        new(_orchestrator, _entityRepo, _factRepo, _prefRepo, _relRepo, _clock, _idGen,
            _log,
            new PassThroughMemoryPersistenceTransaction(),
            Options.Create(new ExtractionOptions
            {
                SupersedeReplacedFacts = supersede,
                // The batch path bypasses the per-item write hook; the item path is what this covers.
                EnableBatchMemoryUpserts = false,
            }));

    private static ExtractionStageResult Incoming(string predicate, string @object) => new()
    {
        FilteredFacts =
        [
            new ExtractedFact
            {
                Subject = "user", Predicate = predicate, Object = @object, Confidence = 0.9,
            },
        ],
    };

    // ── half one: the replacement happens ─────────────────────────────────

    [Fact]
    public async Task ANewValueForAFunctionalRelationSupersedesTheOldOne()
    {
        await CreateSut(supersede: true).PersistAsync(Incoming("lives in", "Zurich"), ownerId: "alice");

        _superseded.Should().ContainSingle().Which.Loser.Should().Be("old-Basel");
    }

    [Fact]
    public async Task TheSupersessionIsOwnerScoped()
    {
        // Supersession closes a fact. A cross-owner one would close somebody else's, from a
        // conversation they were not part of -- the worst shape this feature could take.
        await CreateSut(supersede: true).PersistAsync(Incoming("lives in", "Zurich"), ownerId: "alice");

        await _factRepo.Received().FindSupersededCandidatesAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<MemoryScope?>(scope => scope != null && scope.OwnerId == "alice" && !scope.IncludeShared),
            Arg.Any<CancellationToken>());
    }

    // ── half two: what must NOT be replaced ──────────────────────────────

    [Fact]
    public async Task AMultiValuedRelationIsNeverSuperseded()
    {
        // THE dangerous direction. A person likes many things; closing "likes coffee" when "likes tea"
        // arrives destroys a true fact and leaves a graph that still looks correct. The store is not
        // even asked, so this costs no query either.
        await CreateSut(supersede: true).PersistAsync(Incoming("likes", "tea"), ownerId: "alice");

        _superseded.Should().BeEmpty();
        await _factRepo.DidNotReceive().FindSupersededCandidatesAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("attended")]      // an event: additive by nature
    [InlineData("owns")]          // a state, but multi-valued
    [InlineData("is interested in")]
    [InlineData("vibed with")]    // outside the vocabulary entirely
    public async Task NothingUndeclaredIsEverSuperseded(string predicate)
    {
        await CreateSut(supersede: true).PersistAsync(Incoming(predicate, "something"), ownerId: "alice");

        _superseded.Should().BeEmpty();
    }

    [Fact]
    public async Task TheFeatureIsOffByDefault()
    {
        // It changes what live recall returns, and every recorded measurement was taken with
        // append-only writes. A default flip would move results with no setting changed.
        new ExtractionOptions().SupersedeReplacedFacts.Should().BeFalse();

        await CreateSut(supersede: false).PersistAsync(Incoming("lives in", "Zurich"), ownerId: "alice");

        _superseded.Should().BeEmpty();
    }

    [Fact]
    public async Task AStoreFailureLeavesTheFactStoredRatherThanFailingTheIngestion()
    {
        // Losing a supersession costs precision in live recall; failing the ingestion loses the memory
        // itself. The write already succeeded when this runs, so the fallback is exactly the
        // append-only behaviour -- never a half-resolved graph.
        _factRepo.FindSupersededCandidatesAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<Fact>>>(_ => throw new InvalidOperationException("store is down"));

        var result = await CreateSut(supersede: true)
            .PersistAsync(Incoming("lives in", "Zurich"), ownerId: "alice");

        result.FactCount.Should().Be(1);
    }

    // ── the cardinality declaration itself ────────────────────────────────

    [Fact]
    public void OnlyDeclaredRelationsAreFunctional()
    {
        MemoryRelationCardinality.IsSingleValued("lives in").Should().BeTrue();
        MemoryRelationCardinality.IsSingleValued("works at").Should().BeTrue();
        MemoryRelationCardinality.IsSingleValued("likes").Should().BeFalse();
        MemoryRelationCardinality.IsSingleValued("visited").Should().BeFalse();
        MemoryRelationCardinality.IsSingleValued("").Should().BeFalse();
        MemoryRelationCardinality.IsSingleValued(null).Should().BeFalse();
    }

    [Fact]
    public void SurfaceFormsOfAFunctionalRelationAreFunctionalToo()
    {
        // Otherwise "lived in" would accumulate beside "lives in" and both stay live -- the exact
        // accumulation this exists to stop, reappearing through a synonym.
        MemoryRelationCardinality.IsSingleValued("LIVES IN").Should().BeTrue();
        MemoryRelationCardinality.IsSingleValued("lives_in").Should().BeTrue();
    }

    [Fact]
    public void TheFunctionalSetIsSmallAndDeliberate()
    {
        // A guard on the direction of drift. Declaring a relation functional is a licence to close
        // facts, so the set growing quietly -- especially past the state relations into events -- is
        // the failure worth catching in review rather than in a corpus.
        var functional = MemoryRelationCardinality.SingleValuedPredicates;

        functional.Should().HaveCountLessThan(12,
            "each entry licenses closing a fact; a large set means someone stopped thinking about it");
        functional.Should().NotContain("likes").And.NotContain("owns").And.NotContain("visited");
    }

    // ── half three: the refusal is observable ─────────────────────────────

    [Fact]
    public async Task AnUnrecognisedPredicateWarnsThatTheFeatureIsOnAndInert()
    {
        // The silent no-op this closes. A caller enables supersession, their extractor writes
        // free-form predicates, nothing qualifies, no edge is written -- and before this, nothing
        // anywhere said so. A benchmark ran four scored arms against exactly that before the cause
        // was found, and only because the graph could be queried directly.
        await CreateSut(supersede: true).PersistAsync(Incoming("was at", "Zurich"), ownerId: "alice");

        _superseded.Should().BeEmpty("'was at' is not declared single-valued");
        _log.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("ENABLED but none of the"),
            "a feature that is on and doing nothing must say so");
        _log.Entries.Should().Contain(e => e.Message.Contains("lives in"),
            "the warning names the qualifying relations so the reader can act on it");
    }

    [Fact]
    public async Task TheRefusalNamesThePredicateItRefused()
    {
        await CreateSut(supersede: true).PersistAsync(Incoming("was at", "Zurich"), ownerId: "alice");

        _log.Entries.Should().Contain(e =>
            e.Level == LogLevel.Debug && e.Message.Contains("was at"),
            "per-fact detail is what makes the batch warning actionable");
    }

    [Fact]
    public async Task AQualifyingPredicateDoesNotWarn()
    {
        // The counter-check: a warning that fires when the feature IS working would train the reader
        // to ignore it, which is how a signal stops being a signal.
        await CreateSut(supersede: true).PersistAsync(Incoming("lives in", "Zurich"), ownerId: "alice");

        _log.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task TheFeatureBeingOffIsSilent()
    {
        // Nobody asked for supersession, so nobody is told it did not happen.
        await CreateSut(supersede: false).PersistAsync(Incoming("was at", "Zurich"), ownerId: "alice");

        _log.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
    }
}
