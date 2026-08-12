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

    private MemoryService CreateSut(bool resolveTemporalQueries) =>
        new(Substitute.For<IShortTermMemoryService>(),
            _assembler,
            Substitute.For<IMemoryExtractionPipeline>(),
            Substitute.For<IEntityRepository>(),
            Substitute.For<IFactRepository>(),
            Substitute.For<IPreferenceRepository>(),
            Substitute.For<IEmbeddingOrchestrator>(),
            Options.Create(new MemoryOptions { ResolveTemporalQueries = resolveTemporalQueries }),
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
    public async Task BothClocksMoveTogether()
    {
        // "What did I think in March" asks what was true then AS KNOWN THEN. Moving only the valid
        // clock would answer with today's corrections applied to the past -- a different question, and
        // a subtly misleading one, because the answer would look like a faithful reconstruction.
        await RecallAsync("what did I think back in March");

        await _assembler.Received(1).AssembleContextAsOfAsync(
            Arg.Any<RecallRequest>(),
            Arg.Is<DateTimeOffset>(v => v == new DateTimeOffset(2026, 3, 31, 23, 59, 59, TimeSpan.Zero)),
            Arg.Is<DateTimeOffset>(s => s == new DateTimeOffset(2026, 3, 31, 23, 59, 59, TimeSpan.Zero)),
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
}
