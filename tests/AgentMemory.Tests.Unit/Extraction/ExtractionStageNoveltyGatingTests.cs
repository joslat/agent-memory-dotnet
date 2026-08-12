using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// The wiring half of E4: the gate has to prevent the call, not merely be able to judge it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ExtractionNoveltyGate"/>'s own tests prove a batch of pleasantries scores as
/// uninformative. <b>None of them prove a single completion was avoided.</b> A gate nothing consults
/// passes all of them while every turn still costs exactly what it did before — and since the saving
/// is invisible from the outside, nothing else in the system would report the feature as inert.
/// </para>
/// <para>
/// So these assert on the extractor: was it invoked, and on which of the two paths.
/// </para>
/// </remarks>
public sealed class ExtractionStageNoveltyGatingTests
{
    private readonly IFactExtractor _extractor = Substitute.For<IFactExtractor>();
    private readonly IUnifiedMemoryExtractor _unified = Substitute.For<IUnifiedMemoryExtractor>();

    public ExtractionStageNoveltyGatingTests()
    {
        _extractor.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ExtractedFact>>([]));

        _unified.IsEnabled.Returns(true);
        _unified.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new UnifiedExtractionResult()));
    }

    private static Message M(string content) => new()
    {
        MessageId = $"m-{content.GetHashCode():X}",
        ConversationId = "c-1",
        SessionId = "s-1",
        Role = "user",
        Content = content,
        TimestampUtc = DateTimeOffset.UnixEpoch,
    };

    private ExtractionStage CreateSut(bool skipUninformativeTurns, bool withUnified = false) =>
        new([], [_extractor], [], [],
            withUnified ? [_unified] : [],
            Substitute.For<IEntityResolver>(),
            Options.Create(new ExtractionOptions { SkipUninformativeTurns = skipUninformativeTurns }),
            NullLogger<ExtractionStage>.Instance);

    [Fact]
    public async Task AnUninformativeBatchNeverReachesTheExtractor()
    {
        // THE test. Everything else in E4 is arrangement around this call not being made.
        await CreateSut(skipUninformativeTurns: true)
            .ExtractAsync([M("ok, thanks!")], ExtractionTypes.All);

        await _extractor.DidNotReceive().ExtractAsync(
            Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheUnifiedExtractorIsSkippedToo()
    {
        // The unified extractor is a separate dispatch path and is the one a real deployment uses --
        // gating only the per-type extractors would save nothing where it counts.
        await CreateSut(skipUninformativeTurns: true, withUnified: true)
            .ExtractAsync([M("thanks!")], ExtractionTypes.All);

        await _unified.DidNotReceive().ExtractAsync(
            Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AContentfulBatchStillReachesTheExtractor()
    {
        await CreateSut(skipUninformativeTurns: true)
            .ExtractAsync([M("I moved to Zurich")], ExtractionTypes.All);

        await _extractor.Received(1).ExtractAsync(
            Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisabledMeansEveryTurnStillCosts()
    {
        // The off switch, and the byte-identical guarantee: with the gate off, a pleasantry is
        // extracted from exactly as it was before E4 existed.
        await CreateSut(skipUninformativeTurns: false)
            .ExtractAsync([M("ok, thanks!")], ExtractionTypes.All);

        await _extractor.Received(1).ExtractAsync(
            Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AlreadyExtractedResultsAreNeverGatedAway()
    {
        // The path that must NOT be gated. When the caller supplies a pre-extracted result the
        // completion has already been paid for; discarding it there would throw away work that cost
        // money instead of avoiding the cost -- the gate's exact inverse.
        var preExtracted = new UnifiedExtractionResult
        {
            Facts = [new ExtractedFact { Subject = "user", Predicate = "lives in", Object = "Zurich", Confidence = 0.9 }],
        };

        var result = await CreateSut(skipUninformativeTurns: true)
            .ProcessUnifiedAsync([M("ok, thanks!")], preExtracted, ExtractionTypes.All);

        result.RawFacts.Should().HaveCount(1,
            "the caller already paid for this extraction; gating it discards work rather than avoiding it");
    }

    [Fact]
    public async Task ASkippedBatchStillReportsItsSourceMessages()
    {
        // Skipping the call must not skip the bookkeeping: provenance is what lets a later audit tell
        // "these turns were considered and carried nothing" apart from "these turns were never seen".
        var result = await CreateSut(skipUninformativeTurns: true)
            .ExtractAsync([M("ok"), M("thanks!")], ExtractionTypes.All);

        result.SourceMessageIds.Should().HaveCount(2);
        result.RawFacts.Should().BeEmpty();
    }
}
