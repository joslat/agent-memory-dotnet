using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// Gold-<b>turn</b> coverage must be observable on the structured arm (27.1).
/// </summary>
/// <remarks>
/// <para>
/// 22.3 made <i>session</i> coverage observable on the structured arm and stopped there. Turn coverage
/// kept counting only <c>evidence</c>, which is built from recalled raw messages, so it read
/// <b>0 on every structured question</b> — correct answers and wrong ones alike.
/// </para>
/// <para>
/// <b>Why that was the expensive half to leave blind.</b> Turn coverage is the retrieval signal that
/// actually separates outcomes: on the hybrid arm, correct answers averaged 0.937 and wrong answers
/// 0.667, while session coverage separated them far less (1.000 against 0.833). The signal with the
/// most discriminating power was unobservable on the arm that ships — which would have left any test
/// of query formulation, the last untested retrieval lever, unable to see its own effect.
/// </para>
/// </remarks>
public sealed class StructuredTurnCoverageTests
{
    private const string Gold = "gold-session";

    // m0 and m1 are annotated gold turns; m2 sits in the gold session but answers nothing. The
    // distinction is the whole point: session coverage cannot tell m0 from m2, turn coverage can.
    private static LongMemEvalMessageOrigin Origin(int ordinal, bool hasAnswer) =>
        new(ordinal, Gold, 0, ordinal, "2023/05/30 (Tue) 12:00", "user", $"m{ordinal}",
            false, false, hasAnswer);

    private static LongMemEvalEvidenceQuestion Question() =>
        new("q1", "multi-session", "How many?", "How many?", "2", "2023/05/30 (Tue) 12:00",
            IsAbstention: false,
            new HashSet<string>(StringComparer.Ordinal) { Gold },
            AnnotatedGoldTurnCount: 2,
            [Origin(0, true), Origin(1, true), Origin(2, false)]);

    private static Dictionary<string, LongMemEvalMessageOrigin> Origins() => new(StringComparer.Ordinal)
    {
        ["m0"] = Origin(0, true),
        ["m1"] = Origin(1, true),
        ["m2"] = Origin(2, false),
    };

    private static LongMemEvalRetrievalEvidence Build(IReadOnlyCollection<string>? structuredIds) =>
        LongMemEvalRetrievalEvidence.Build(
            Question(),
            recalled: [],
            rankedItems: [],
            Origins(),
            LongMemEvalEvidenceDetail.Identifiers,
            answerPromptCharacters: 100,
            configuredMessageBudget: 0,
            structuredSourceMessageIds: structuredIds);

    [Fact]
    public void AStructuredRunCountsGoldTurnsReachedThroughProvenance()
    {
        // THE fix. Red before 27.1: GoldTurnsHit was 0 and GoldTurnHitAtK was false, because both read
        // only from recalled raw messages and a structured run has none.
        var evidence = Build(["m0", "m1"]);

        evidence.GoldTurnsHit.Should().Be(2);
        evidence.GoldTurnHitAtK.Should().BeTrue();
    }

    [Fact]
    public void ReachingTheGoldSessionWithoutItsGoldTurnIsFullSessionCoverageAndPartialTurnCoverage()
    {
        // The exact shape of failure 352ab8bd: session coverage 1.0, turn coverage 0.0 — the right
        // conversation retrieved and the wrong turns within it. Collapsing these two numbers into one
        // is what made that failure look identical to four that had the evidence and still got it
        // wrong, and it is why this metric had to become observable before query formulation is tested.
        var evidence = Build(["m2"]);

        evidence.GoldSessionRecallAtK.Should().Be(1.0);
        evidence.GoldTurnsHit.Should().Be(0);
        evidence.GoldTurnHitAtK.Should().BeFalse();
    }

    [Fact]
    public void TheSameTurnReachedTwiceIsCountedOnce()
    {
        // Union by message id, not a sum. On the hybrid arm a gold turn is routinely reached through
        // BOTH a recalled message and a fact extracted from that same message; summing would report
        // turn coverage above 1.0 precisely on the arm that has both channels.
        Build(["m0", "m0", "m0"]).GoldTurnsHit.Should().Be(1);
    }

    [Fact]
    public void ARunWithNoResolvableProvenanceReportsUnobservableRatherThanZero()
    {
        // The guard 22.3 protected must survive this change too: with nothing to attribute through,
        // the honest answer is null. A false here would manufacture a retrieval miss out of a harness
        // limitation — indistinguishable, in a report, from the real thing.
        Build(structuredIds: []).GoldTurnHitAtK.Should().BeNull();
        Build(structuredIds: null).GoldTurnHitAtK.Should().BeNull();
        Build(["not-a-real-id"]).GoldTurnHitAtK.Should().BeNull();
    }

    [Fact]
    public void FirstGoldTurnRankStaysNullOnAPureStructuredRun()
    {
        // Deliberate, and asserted so nobody "fixes" it into a fabricated number. A rank means
        // position in the answer context; structured items are ranked within their own sections rather
        // than in one ordering shared with messages, so a cross-section rank would look comparable
        // between arms without being so. GoldTurnsHit is a count and has no such problem.
        Build(["m0", "m1"]).FirstGoldTurnRank.Should().BeNull();
    }
}
