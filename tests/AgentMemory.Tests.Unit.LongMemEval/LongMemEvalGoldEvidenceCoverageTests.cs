using AgentMemory.LongMemEval;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// G3B.5. The prepared path already proves the cold build is <i>sound</i> — non-empty, fully
/// provenanced, and bit-identical to the sealed snapshot. None of that proves it is <i>adequate</i>:
/// three facts learned from 474 sessions passes every existing guard. These cover the adequacy check.
/// </summary>
public sealed class LongMemEvalGoldEvidenceCoverageTests
{
    [Fact]
    public void LearningNothingFromTheAnswerSessionsIsAnExtractionFailureNotARetrievalOne()
    {
        // The question was unanswerable before recall ever ran, so blaming retrieval would hide an
        // extraction defect behind a retrieval label.
        var coverage = new LongMemEvalGoldEvidenceCoverage(
            GoldLearnedItems: 0, GoldSourceMessagesCovered: 0, GoldSourceMessages: 12);

        Assert.False(coverage.EvidenceLearned);
        Assert.Equal(
            "extraction-lost-evidence",
            LongMemEvalPostRunDiagnostics.ClassifyForTest(evidence: null, goldCoverage: coverage));
    }

    [Fact]
    public void LostEvidenceOutranksTheNotObservableVerdictItWouldOtherwiseGet()
    {
        // BUG-E1 reports Structured failures as "not observable" because gold attribution resolves
        // only through messages. That is honest but uninformative; a proven extraction loss is a
        // stronger, more specific finding and must win.
        var evidence = Evidence(goldSessionRecall: null, goldTurnHit: null, observable: false);

        Assert.Equal(
            "extraction-lost-evidence",
            LongMemEvalPostRunDiagnostics.ClassifyForTest(
                evidence,
                new LongMemEvalGoldEvidenceCoverage(0, 0, 12)));
    }

    [Fact]
    public void AGraphThatDidLearnFromTheAnswerSessionsFallsThroughToTheExistingVerdicts()
    {
        // The new check must not swallow the attribution it sits in front of.
        var evidence = Evidence(goldSessionRecall: 1d, goldTurnHit: true);

        Assert.Equal(
            "answer-synthesis-failure",
            LongMemEvalPostRunDiagnostics.ClassifyForTest(
                evidence,
                new LongMemEvalGoldEvidenceCoverage(7, 3, 12)));
    }

    [Fact]
    public void AbsentCoverageLeavesEveryExistingVerdictUnchanged()
    {
        // Raw mode has no extraction, so it must classify exactly as it did before this change.
        var evidence = Evidence(goldSessionRecall: 0.5d, goldTurnHit: true);

        Assert.Equal(
            "retrieval-miss",
            LongMemEvalPostRunDiagnostics.ClassifyForTest(evidence, goldCoverage: null));
    }

    private static LongMemEvalRetrievalEvidence Evidence(
        double? goldSessionRecall,
        bool? goldTurnHit,
        bool observable = true) => new(
            K: 30,
            AnswerPromptCharacters: 10_000,
            EstimatedAnswerPromptTokens: 2_500,
            DistinctSourceSessions: 10,
            MaxItemsFromSingleSession: 4,
            GoldSessionsRequired: 1,
            GoldSessionsHit: goldSessionRecall == 1 ? 1 : 0,
            GoldSessionRecallAtK: goldSessionRecall,
            AnnotatedGoldTurns: 1,
            GoldTurnsHit: goldTurnHit is true ? 1 : 0,
            GoldTurnHitAtK: goldTurnHit,
            FirstGoldSessionRank: goldSessionRecall == 1 ? 5 : null,
            FirstGoldTurnRank: goldTurnHit is true ? 5 : null,
            ReciprocalRank: goldSessionRecall == 1 ? 0.2 : null,
            RankedItems: [],
            GoldAttributionObservable: observable);

    [Fact]
    public void SourceMessageCoverageReportsTheFractionThatContributedAnything()
    {
        var coverage = new LongMemEvalGoldEvidenceCoverage(
            GoldLearnedItems: 9, GoldSourceMessagesCovered: 3, GoldSourceMessages: 12);

        Assert.True(coverage.EvidenceLearned);
        Assert.Equal(0.25d, coverage.SourceMessageCoverage);
    }

    [Fact]
    public void NoAnswerBearingMessagesAtAllDoesNotDivideByZero()
    {
        var coverage = new LongMemEvalGoldEvidenceCoverage(0, 0, 0);

        Assert.Equal(0d, coverage.SourceMessageCoverage);
    }
}
