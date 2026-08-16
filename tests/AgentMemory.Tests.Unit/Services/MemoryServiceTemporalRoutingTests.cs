using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// The wiring half of R4: a turn that names a past time must actually reach bitemporal recall.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AgentMemory.Core.Memory.TemporalQueryParser"/> has its own tests and they prove only that
/// a string becomes an instant. <b>That is the half that cannot fail quietly.</b> A parser nothing calls
/// passes every one of its own tests while <c>RecallAsOfAsync</c> stays exactly as unreachable as it was
/// before — which is the shape this track has now hit four separate times.
/// </para>
/// <para>
/// So these tests assert on the seam: which assembler method was invoked, and what the recall reported
/// about the clocks it used.
/// </para>
/// </remarks>
public sealed class MemoryServiceTemporalRoutingTests
{
    private readonly IMemoryContextAssembler _assembler = Substitute.For<IMemoryContextAssembler>();
    private readonly IClock _clock = Substitute.For<IClock>();

    private static readonly DateTimeOffset Now = new(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);

    public MemoryServiceTemporalRoutingTests()
    {
        _clock.UtcNow.Returns(Now);

        var empty = new MemoryContext { SessionId = "s-1", AssembledAtUtc = Now };
        _assembler.AssembleContextAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(empty));
        _assembler.AssembleContextAsOfAsync(
                Arg.Any<RecallRequest>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(empty));
    }

    private MemoryService CreateSut(
        bool resolveTemporalQueries,
        TemporalQueryClocks clocks = TemporalQueryClocks.ValidTimeOnly) =>
        new(Substitute.For<IShortTermMemoryService>(),
            _assembler,
            Substitute.For<IMemoryExtractionPipeline>(),
            Substitute.For<IEntityRepository>(),
            Substitute.For<IFactRepository>(),
            Substitute.For<IPreferenceRepository>(),
            Substitute.For<IEmbeddingOrchestrator>(),
            Options.Create(new MemoryOptions
            {
                ResolveTemporalQueries = resolveTemporalQueries,
                TemporalQueryClocks = clocks,
            }),
            _clock,
            Substitute.For<IIdGenerator>(),
            NullLogger<MemoryService>.Instance);

    private Task<RecallResult> RecallAsync(string query, bool enabled = true) =>
        CreateSut(enabled).RecallAsync(new RecallRequest { SessionId = "s-1", Query = query });

    [Fact]
    public async Task ATemporalQueryReachesBitemporalRecall()
    {
        // THE test. Everything else in R4 is arrangement around this one call actually being made.
        var result = await RecallAsync("what did I think back in March");

        await _assembler.Received(1).AssembleContextAsOfAsync(
            Arg.Any<RecallRequest>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
        await _assembler.DidNotReceive().AssembleContextAsync(
            Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>());

        result.Metadata["validAsOf"].Should()
            .Be(new DateTimeOffset(2026, 3, 31, 23, 59, 59, TimeSpan.Zero));
    }

    [Fact]
    public async Task BothClocksMoveTogetherUnderBeliefReconstruction()
    {
        // "What did I think in March" asks what was true then AS KNOWN THEN. Moving only the valid
        // clock would answer with today's corrections applied to the past -- a different question, and
        // a subtly misleading one, because the answer would look like a faithful reconstruction.
        //
        // 13.2 made that the OPT-IN reading rather than the default, and the invariant is asserted at
        // the assembler seam here because that is where a caller would actually be harmed. The default
        // now binds valid time only: see ATemporalQueryBindsValidTimeOnlyByDefault for why -- binding
        // the transaction clock on a host whose created_at is import time empties the whole store.
        var expected = new DateTimeOffset(2026, 3, 31, 23, 59, 59, TimeSpan.Zero);

        await CreateSut(true, TemporalQueryClocks.ValidAndTransactionTime)
            .RecallAsync(new RecallRequest { SessionId = "s-1", Query = "what did I think back in March" });

        await _assembler.Received(1).AssembleContextAsOfAsync(
            Arg.Any<RecallRequest>(),
            Arg.Is<DateTimeOffset>(v => v == expected),
            Arg.Is<DateTimeOffset>(s => s == expected),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheDefaultDoesNotBindTheTransactionClockToThePast()
    {
        // The seam-level guard for the default. Asserted as "systemAsOf is NOT the resolved instant"
        // rather than only as a metadata value, because the exclusion happens inside the assembler's
        // Cypher (`node.created_at <= datetime($systemAsOf)`) and a caller reading only the metadata
        // would never see the rows it silently removed.
        var resolved = new DateTimeOffset(2026, 3, 31, 23, 59, 59, TimeSpan.Zero);

        await RecallAsync("what did I think back in March");

        await _assembler.Received(1).AssembleContextAsOfAsync(
            Arg.Any<RecallRequest>(),
            Arg.Is<DateTimeOffset>(v => v == resolved),
            Arg.Is<DateTimeOffset>(s => s == Now),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnOrdinaryQueryTakesTheOrdinaryPath()
    {
        // The common case, and the one that must not regress: no temporal marker means the live path,
        // byte for byte as before.
        await RecallAsync("what is my deploy command");

        await _assembler.Received(1).AssembleContextAsync(
            Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>());
        await _assembler.DidNotReceive().AssembleContextAsOfAsync(
            Arg.Any<RecallRequest>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheOptionIsOffByDefault()
    {
        // It changes which memories a temporal question sees, so it is opt-in -- and every sealed
        // measurement in this track was taken with it off.
        new MemoryOptions().ResolveTemporalQueries.Should().BeFalse();
    }

    [Fact]
    public async Task DisabledMeansTheParserNeverRuns()
    {
        // The off switch has to be genuinely off, not merely default-off: a temporal query with the
        // option disabled must take exactly the path it took before R4 existed.
        await RecallAsync("what did I think back in March", enabled: false);

        await _assembler.Received(1).AssembleContextAsync(
            Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>());
        await _assembler.DidNotReceive().AssembleContextAsOfAsync(
            Arg.Any<RecallRequest>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheParserResolvesAgainstTheInjectedClock()
    {
        // Not DateTimeOffset.UtcNow. "Last week" is meaningless without a reference instant, and a
        // parser reading the wall clock while the rest of the service reads IClock would drift in
        // exactly the tests written to pin temporal behaviour down.
        _clock.UtcNow.Returns(new DateTimeOffset(2020, 6, 15, 0, 0, 0, TimeSpan.Zero));

        var result = await RecallAsync("what did I think last week");

        result.Metadata["validAsOf"].Should()
            .Be(new DateTimeOffset(2020, 6, 8, 0, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankQueryTakesTheOrdinaryPath(string query)
    {
        // Recall with a blank query is legal -- it assembles recent context. The temporal branch must
        // neither throw on it nor treat "no query" as some resolvable instant.
        await RecallAsync(query);

        await _assembler.Received(1).AssembleContextAsync(
            Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheRequestsReferenceTimeOverridesTheClock()
    {
        // 13.2. THE test for the replayed-transcript case. "Ten days ago" is measured from when the
        // turn was SPOKEN; for a host draining a backlog or replaying a recorded conversation that is
        // not wall-clock. Resolving against the clock does not merely fail to help -- it binds the
        // query to a window years away from anything the corpus holds, so recall returns nothing and
        // the result reads as "temporal resolution does not work" rather than "it was asked the wrong
        // question". A whole benchmark arm can be spent on that mistake.
        var spoken = new DateTimeOffset(2023, 5, 20, 0, 0, 0, TimeSpan.Zero);

        var result = await CreateSut(true).RecallAsync(new RecallRequest
        {
            SessionId = "s-1",
            Query = "what kitchen appliance did I buy 10 days ago",
            TemporalReferenceTime = spoken,
        });

        result.Metadata["validAsOf"].Should().Be(spoken.AddDays(-10));
    }

    [Fact]
    public async Task TheResolvedInstantIsWitnessedOnTheContext()
    {
        // The witness half. Query-time resolution is biased hard toward returning null, so "nothing
        // changed" is its normal outcome -- and therefore indistinguishable from the option being
        // unwired, the parser never being reached, or the reference time being wrong. A measurement
        // that cannot tell those apart is the defect shape that voided six runs of 7.6.
        var result = await RecallAsync("what did I think back in March");

        result.Context.ResolvedTemporalAsOf.Should()
            .Be(new DateTimeOffset(2026, 3, 31, 23, 59, 59, TimeSpan.Zero));
    }

    [Fact]
    public async Task AnOrdinaryRecallLeavesTheWitnessUnset()
    {
        var result = await RecallAsync("what is my favourite colour");

        result.Context.ResolvedTemporalAsOf.Should().BeNull();
    }

    [Fact]
    public async Task ATemporalQueryWithTheOptionOffLeavesTheWitnessUnset()
    {
        // Otherwise a run could report resolutions it never performed, which is worse than reporting
        // none: it would license believing a null result.
        var result = await RecallAsync("what did I think back in March", enabled: false);

        result.Context.ResolvedTemporalAsOf.Should().BeNull();
    }

    [Fact]
    public async Task ATemporalQueryBindsValidTimeOnlyByDefault()
    {
        // 13.2. THE bug this default exists to prevent. created_at is INGESTION time on any host that
        // imported, migrated or backfilled its history -- so binding the transaction clock to a past
        // instant excludes every row in the store. That host asks "what happened last month", gets an
        // empty context, no error, and reads it as the memory holding nothing.
        var result = await RecallAsync("what did I think back in March");

        result.Metadata["validAsOf"].Should()
            .Be(new DateTimeOffset(2026, 3, 31, 23, 59, 59, TimeSpan.Zero));
        result.Metadata["systemAsOf"].Should().Be(Now,
            "the transaction clock stays at now: the question is about the past WORLD, answered with "
            + "everything known today");
    }

    [Fact]
    public async Task BeliefReconstructionBindsBothClocksWhenAskedFor()
    {
        // The other reading, kept reachable rather than removed: "what did I think then" is a real
        // question, and it is the one an audit asks.
        var result = await CreateSut(true, TemporalQueryClocks.ValidAndTransactionTime)
            .RecallAsync(new RecallRequest { SessionId = "s-1", Query = "what did I think back in March" });

        var expected = new DateTimeOffset(2026, 3, 31, 23, 59, 59, TimeSpan.Zero);
        result.Metadata["validAsOf"].Should().Be(expected);
        result.Metadata["systemAsOf"].Should().Be(expected);
    }

    [Fact]
    public async Task TheTransactionClockFollowsTheRequestsReferenceTimeNotTheWallClock()
    {
        // Composed with the replay case: a host replaying a transcript wants "everything known as of
        // the turn", not as of today. Taking _clock.UtcNow here would leak wall-clock back into the
        // one path that exists precisely because wall-clock is wrong.
        var spoken = new DateTimeOffset(2023, 5, 20, 0, 0, 0, TimeSpan.Zero);

        var result = await CreateSut(true).RecallAsync(new RecallRequest
        {
            SessionId = "s-1",
            Query = "what kitchen appliance did I buy 10 days ago",
            TemporalReferenceTime = spoken,
        });

        result.Metadata["systemAsOf"].Should().Be(spoken);
    }

    [Fact]
    public async Task AnExplicitAsOfRecallLeavesTheWitnessUnset()
    {
        // Deliberate. That caller already knows which instant it asked for, and stamping it here would
        // make "the parser fired" and "someone passed a date" the same observation -- so a harness
        // counting resolutions would count its own explicit calls and always look wired.
        var result = await CreateSut(true).RecallAsOfAsync(
            new RecallRequest { SessionId = "s-1", Query = "anything" },
            new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));

        result.Context.ResolvedTemporalAsOf.Should().BeNull();
    }
}
