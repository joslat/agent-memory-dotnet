using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Memory;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Memory;

/// <summary>
/// Synthesized entity summaries (S1) — and the one property that decides whether they are an asset
/// or a liability: a summary that cannot prove it is current is never used.
/// </summary>
/// <remarks>
/// <para>
/// Recall about a well-known entity returns twenty facts that each cost context to say one thing. A
/// summary says the same in one item. But a summary is <b>derived</b> memory, and derived memory is
/// where a store quietly starts lying: the sources change, the summary does not, and afterwards
/// nothing about it looks any different.
/// </para>
/// <para>
/// So the tests here are mostly about a summary being <i>withheld</i>. The synthesis is the easy half.
/// </para>
/// </remarks>
public sealed class EntitySummaryTests
{
    private readonly IFactRepository _facts = Substitute.For<IFactRepository>();
    private readonly IEntitySummaryRepository _summaries = Substitute.For<IEntitySummaryRepository>();
    private readonly IClock _clock = Substitute.For<IClock>();

    private static readonly Entity Alice = new()
    {
        EntityId = "e-alice",
        Name = "Alice",
        Type = "Person",
        Confidence = 1.0,
        OwnerId = "owner-1",
        CreatedAtUtc = DateTimeOffset.UnixEpoch,
    };

    private static readonly MemoryScope Scope = MemoryScope.For("owner-1");

    private static Fact F(
        string id, string predicate, string @object,
        double confidence = 0.9, DateTimeOffset? invalidatedAt = null) => new()
    {
        FactId = id,
        Subject = "Alice",
        Predicate = predicate,
        Object = @object,
        Confidence = confidence,
        OwnerId = "owner-1",
        CreatedAtUtc = DateTimeOffset.UnixEpoch,
        InvalidatedAtUtc = invalidatedAt,
    };

    public EntitySummaryTests() => _clock.UtcNow.Returns(DateTimeOffset.UnixEpoch);

    private EntitySummaryService CreateSut()
    {
        var ids = Substitute.For<IIdGenerator>();
        ids.GenerateId().Returns("sum-1");
        return new EntitySummaryService(
            _facts, _summaries, new DeterministicEntitySummarySynthesizer(),
            _clock, ids, NullLogger<EntitySummaryService>.Instance);
    }

    private void StoreHolds(params Fact[] facts) =>
        _facts.GetBySubjectAsync("Alice", Arg.Any<MemoryScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>(facts));

    private void SummaryOnRecord(EntitySummary summary) =>
        _summaries.GetByEntityAsync("e-alice", Arg.Any<MemoryScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<EntitySummary?>(summary));

    // ── staleness: the point of the whole design ──────────────────────────

    [Fact]
    public async Task ASummaryIsUsedWhileItsSourcesAreUnchanged()
    {
        StoreHolds(F("f-1", "lives in", "Zurich"));
        var written = await CreateSut().RefreshAsync(Alice, Scope);
        SummaryOnRecord(written!);

        (await CreateSut().GetIfCurrentAsync(Alice, Scope)).Should().NotBeNull();
    }

    [Fact]
    public async Task ASupersededSourceMakesTheSummaryUnusable()
    {
        // THE test. A fact was contradicted; the stored text still asserts it. Nothing about that
        // summary looks different, so the only thing standing between it and a confident wrong answer
        // is this check.
        StoreHolds(F("f-1", "lives in", "Zurich"));
        var written = await CreateSut().RefreshAsync(Alice, Scope);
        SummaryOnRecord(written!);

        StoreHolds(F("f-1", "lives in", "Zurich", invalidatedAt: DateTimeOffset.UnixEpoch));

        (await CreateSut().GetIfCurrentAsync(Alice, Scope)).Should().BeNull();
    }

    [Fact]
    public async Task ANewFactMakesTheSummaryUnusable()
    {
        // Incompleteness is staleness too. A summary that omits something the store now knows is
        // exactly as misleading as one asserting something it no longer believes.
        StoreHolds(F("f-1", "lives in", "Zurich"));
        var written = await CreateSut().RefreshAsync(Alice, Scope);
        SummaryOnRecord(written!);

        StoreHolds(F("f-1", "lives in", "Zurich"), F("f-2", "works at", "Acme"));

        (await CreateSut().GetIfCurrentAsync(Alice, Scope)).Should().BeNull();
    }

    [Fact]
    public async Task AMovedConfidenceMakesTheSummaryUnusable()
    {
        // Confidence is in the fingerprint deliberately. S2 reinforcement moves it, and a summary
        // stating flatly what the store has since grown doubtful about is the stale shadow this
        // design exists to prevent.
        StoreHolds(F("f-1", "lives in", "Zurich", confidence: 0.9));
        var written = await CreateSut().RefreshAsync(Alice, Scope);
        SummaryOnRecord(written!);

        StoreHolds(F("f-1", "lives in", "Zurich", confidence: 0.6));

        (await CreateSut().GetIfCurrentAsync(Alice, Scope)).Should().BeNull();
    }

    [Fact]
    public async Task NoSummaryOnRecordIsNotAnError()
    {
        _summaries.GetByEntityAsync("e-alice", Arg.Any<MemoryScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<EntitySummary?>(null));

        (await CreateSut().GetIfCurrentAsync(Alice, Scope)).Should().BeNull();
    }

    // ── the fingerprint ───────────────────────────────────────────────────

    [Fact]
    public void TheFingerprintIsOrderIndependent()
    {
        // It must answer "are these the same facts?", not "did they come back in the same order?".
        // Otherwise a query-plan change invalidates every summary in the store without a single fact
        // having moved.
        var a = new EntitySummarySource("f-1", 0.9, false);
        var b = new EntitySummarySource("f-2", 0.7, false);

        EntitySummary.ComputeFingerprint([a, b])
            .Should().Be(EntitySummary.ComputeFingerprint([b, a]));
    }

    [Fact]
    public void InvalidationChangesTheFingerprintOnItsOwn()
    {
        // Even with the id and confidence untouched: a fact the store has stopped believing is not
        // the same source it was.
        EntitySummary.ComputeFingerprint([new EntitySummarySource("f-1", 0.9, false)])
            .Should().NotBe(EntitySummary.ComputeFingerprint([new EntitySummarySource("f-1", 0.9, true)]));
    }

    [Fact]
    public void AnEmptySourceSetStillHasAFingerprint() =>
        EntitySummary.ComputeFingerprint([]).Should().NotBeNullOrWhiteSpace();

    // ── synthesis ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SynthesisIsReproducible()
    {
        // Two runs over the same facts produce the same bytes, so a change in the text always means a
        // change in the facts rather than a change in a sampler's mood. That is also what makes the
        // summary safe to fingerprint at all.
        var synth = new DeterministicEntitySummarySynthesizer();
        var facts = new[] { F("f-1", "lives in", "Zurich"), F("f-2", "works at", "Acme") };

        var first = await synth.SynthesizeAsync(Alice, facts);
        // AsEnumerable() first: on an array, Reverse() binds to Array.Reverse, which returns void.
        var second = await synth.SynthesizeAsync(Alice, facts.AsEnumerable().Reverse().ToArray());

        first.Should().Be(second);
    }

    [Fact]
    public async Task LowConfidenceFactsAreLeftOut()
    {
        // A summary states things flatly, with none of the per-fact confidence a caller would see.
        // Folding a guess in gives it the same authority as everything else.
        var summary = await new DeterministicEntitySummarySynthesizer()
            .SynthesizeAsync(Alice, [F("f-1", "lives in", "Zurich"), F("f-2", "owns", "a yacht", confidence: 0.2)]);

        summary.Should().Contain("Zurich");
        summary.Should().NotContain("yacht");
    }

    [Fact]
    public async Task NothingWorthSummarisingReturnsNullNotAnEmptySummary()
    {
        // An empty summary would satisfy every has-a-summary check while saying nothing.
        var summary = await new DeterministicEntitySummarySynthesizer()
            .SynthesizeAsync(Alice, [F("f-1", "owns", "a yacht", confidence: 0.1)]);

        summary.Should().BeNull();
    }

    [Fact]
    public async Task NothingWorthSummarisingIsNotStored()
    {
        StoreHolds(F("f-1", "owns", "a yacht", confidence: 0.1));

        var result = await CreateSut().RefreshAsync(Alice, Scope);

        result.Should().BeNull();
        await _summaries.DidNotReceiveWithAnyArgs().UpsertAsync(default!, default);
    }

    [Fact]
    public async Task SupersededFactsAreNotSummarised()
    {
        // Correctness, not merely staleness: the summary must not assert something the store has
        // already stopped believing at the moment it is written.
        StoreHolds(
            F("f-1", "lives in", "Basel", invalidatedAt: DateTimeOffset.UnixEpoch),
            F("f-2", "lives in", "Zurich"));

        var summary = await CreateSut().RefreshAsync(Alice, Scope);

        summary!.Content.Should().Contain("Zurich");
        summary.Content.Should().NotContain("Basel");
        summary.SourceFactIds.Should().Equal("f-2");
    }
}
