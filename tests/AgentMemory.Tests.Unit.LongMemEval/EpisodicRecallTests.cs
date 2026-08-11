using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// The P3 instrument: does an episodic memory get STORED, and does it get RETRIEVED?
/// </summary>
/// <remarks>
/// <para>
/// LongMemEval asks what the <i>user</i> said and did, so it can only ever charge for episodic recall
/// and never reward it — measured: episodic capture consumed 32.3% of the structured retrieval budget
/// and +23.1% answer-prompt tokens for no accuracy movement. Answering "does episodic memory help"
/// with an accuracy delta would inherit a ~4 point same-base floor and a ~9 point cross-build one, and
/// could not resolve the effect.
/// </para>
/// <para>
/// So this measures recall directly and separates the two failures an accuracy number conflates:
/// <b>not stored</b> (extraction failed) versus <b>stored but not retrieved</b> (retrieval failed).
/// No judge, no answer model, no noise floor.
/// </para>
/// </remarks>
public sealed class EpisodicRecallTests
{
    private static EpisodicGold Gold(string predicate, string obj) =>
        new("q1", predicate, obj);

    [Fact]
    public void AGoldEpisodicFactThatWasStoredAndRetrievedCountsAsRecalled()
    {
        var verdict = LongMemEvalEpisodicRecall.Evaluate(
            Gold("recommended", "The Bear"),
            stored: [("assistant", "recommended", "The Bear")],
            retrieved: [("assistant", "recommended", "The Bear")]);

        verdict.Stored.Should().BeTrue();
        verdict.Retrieved.Should().BeTrue();
        verdict.Outcome.Should().Be(EpisodicRecallOutcome.Recalled);
    }

    [Fact]
    public void StoredButNotRetrievedIsARETRIEVALFailureAndMustNotReadAsMissing()
    {
        // THE DISTINCTION THIS INSTRUMENT EXISTS FOR. An accuracy number reports both of these as a
        // wrong answer; they call for opposite fixes - one for the extractor, one for retrieval.
        var verdict = LongMemEvalEpisodicRecall.Evaluate(
            Gold("recommended", "The Bear"),
            stored: [("assistant", "recommended", "The Bear")],
            retrieved: [("assistant", "recommended", "Succession")]);

        verdict.Stored.Should().BeTrue();
        verdict.Retrieved.Should().BeFalse();
        verdict.Outcome.Should().Be(EpisodicRecallOutcome.StoredNotRetrieved);
    }

    [Fact]
    public void NotStoredIsAnEXTRACTIONFailureEvenWhenSomethingElseWasRetrieved()
    {
        var verdict = LongMemEvalEpisodicRecall.Evaluate(
            Gold("recommended", "The Bear"),
            stored: [("assistant", "recommended", "Succession")],
            retrieved: [("assistant", "recommended", "Succession")]);

        verdict.Stored.Should().BeFalse();
        verdict.Outcome.Should().Be(EpisodicRecallOutcome.NotStored);
    }

    [Fact]
    public void RetrievedWithoutBeingStoredIsReportedAsIncoherentRatherThanSilentlyAccepted()
    {
        // Cannot happen if both sides read the same graph, so it means the two probes disagree - a
        // scope mismatch, a stale read, or a bug in the harness. Silently counting it as recalled
        // would hide a broken measurement behind a good-looking number.
        var verdict = LongMemEvalEpisodicRecall.Evaluate(
            Gold("recommended", "The Bear"),
            stored: [],
            retrieved: [("assistant", "recommended", "The Bear")]);

        verdict.Outcome.Should().Be(EpisodicRecallOutcome.Incoherent);
    }

    [Fact]
    public void MatchingIsCanonicalSoCapitalisationAndSpacingDoNotDecideTheResult()
    {
        // Same canonicalizer the write path keys on. Raw string comparison would report an extraction
        // failure for a fact that was stored perfectly well under different capitalisation.
        var verdict = LongMemEvalEpisodicRecall.Evaluate(
            Gold("Recommended", "the  bear"),
            stored: [("Assistant", "recommended", "The Bear")],
            retrieved: [("assistant", "RECOMMENDED", "The Bear")]);

        verdict.Outcome.Should().Be(EpisodicRecallOutcome.Recalled);
    }

    [Fact]
    public void ANonAssistantSubjectNeverSatisfiesAnEpisodicGold()
    {
        // The gold is about what the ASSISTANT did. A user-subject fact with the same predicate and
        // object is a different memory, and accepting it would let semantic recall masquerade as
        // episodic recall - the exact confusion this phase exists to avoid.
        var verdict = LongMemEvalEpisodicRecall.Evaluate(
            Gold("recommended", "The Bear"),
            stored: [("user", "recommended", "The Bear")],
            retrieved: [("user", "recommended", "The Bear")]);

        verdict.Outcome.Should().Be(EpisodicRecallOutcome.NotStored);
    }

    [Fact]
    public void SummaryCountsEachOutcomeSeparatelyRatherThanCollapsingToAScore()
    {
        // A single "episodic recall %" would merge extraction and retrieval failures back together,
        // undoing the whole point. The summary keeps them apart.
        var summary = LongMemEvalEpisodicRecall.Summarise(
        [
            LongMemEvalEpisodicRecall.Evaluate(Gold("recommended", "a"),
                stored: [("assistant", "recommended", "a")], retrieved: [("assistant", "recommended", "a")]),
            LongMemEvalEpisodicRecall.Evaluate(Gold("recommended", "b"),
                stored: [("assistant", "recommended", "b")], retrieved: []),
            LongMemEvalEpisodicRecall.Evaluate(Gold("recommended", "c"),
                stored: [], retrieved: []),
        ]);

        summary.Total.Should().Be(3);
        summary.Recalled.Should().Be(1);
        summary.StoredNotRetrieved.Should().Be(1);
        summary.NotStored.Should().Be(1);
        summary.RetrievalRecall.Should().BeApproximately(0.5, 0.001,
            "retrieval recall is measured over what was STORED (1 of 2), not over all questions - "
            + "charging retrieval for the extractor's misses would misattribute the failure");
    }
}
