using AgentEval.Memory.External.LongMemEval;
using AgentMemory.Abstractions.Domain;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// BUG-E1. Gold attribution is computed only over recalled raw messages, but Structured mode
/// allocates a zero message budget. The metric must say "not observable", never a fabricated 0.0
/// that downstream diagnostics then report as a product retrieval defect.
/// </summary>
public sealed class LongMemEvalGoldAttributionObservabilityTests
{
    private static LongMemEvalEvidenceQuestion ResolvedQuestion()
    {
        var entry = LongMemEvalEvidenceIndexTests.Entry();
        var options = LongMemEvalEvidenceIndexTests.Options();
        var formatted = LongMemEvalHistoryFormatter.Format(entry, options);
        return LongMemEvalEvidenceIndex.Create([entry], options)
            .Resolve(formatted, LongMemEvalEvidenceIndexTests.InvocationPrompt(entry));
    }

    /// <summary>
    /// The Structured shape: gold sessions exist, but no message was recalled because none could be.
    /// Reporting 0.0 here asserts that retrieval looked and failed; it never looked.
    /// </summary>
    [Fact]
    public void Build_WithNoMessageBudget_ReportsGoldMetricsAsNotObservable()
    {
        var question = ResolvedQuestion();

        var evidence = LongMemEvalRetrievalEvidence.Build(
            question,
            recalled: Array.Empty<Message>(),
            rankedItems: Array.Empty<MemoryContextRankedItem>(),
            originsByMessageId: new Dictionary<string, LongMemEvalMessageOrigin>(StringComparer.Ordinal),
            detail: LongMemEvalEvidenceDetail.Identifiers,
            answerPromptCharacters: 400,
            configuredMessageBudget: 0);

        question.AnswerSessionIds.Should().NotBeEmpty(
            "the fixture must have gold sessions, or this test proves nothing");
        evidence.GoldAttributionObservable.Should().BeFalse();
        evidence.GoldSessionRecallAtK.Should().BeNull(
            "a zero message budget means gold attribution was never attempted");
        evidence.GoldTurnHitAtK.Should().BeNull();
        evidence.ReciprocalRank.Should().BeNull();
    }

    /// <summary>
    /// Raw mode with a real budget that returned nothing is a genuine miss and must stay observable —
    /// the fix must not blanket-null the metric and hide real retrieval failures.
    /// </summary>
    [Fact]
    public void Build_WithMessageBudgetButNoHits_RemainsObservableAndReportsAMiss()
    {
        var question = ResolvedQuestion();

        var evidence = LongMemEvalRetrievalEvidence.Build(
            question,
            recalled: Array.Empty<Message>(),
            rankedItems: Array.Empty<MemoryContextRankedItem>(),
            originsByMessageId: new Dictionary<string, LongMemEvalMessageOrigin>(StringComparer.Ordinal),
            detail: LongMemEvalEvidenceDetail.Identifiers,
            answerPromptCharacters: 400,
            configuredMessageBudget: 30);

        evidence.GoldAttributionObservable.Should().BeTrue();
        evidence.GoldSessionRecallAtK.Should().Be(0d,
            "a budget of 30 that returned nothing is a real retrieval miss, not an unobservable one");
    }

    /// <summary>
    /// The consequence that matters: an unobservable metric must not be classified as a product
    /// retrieval defect, and must not silently fall through to an answer-synthesis verdict either.
    /// </summary>
    [Fact]
    public void Classify_UnobservableGoldAttribution_IsNotReportedAsRetrievalMiss()
    {
        var question = ResolvedQuestion();
        var evidence = LongMemEvalRetrievalEvidence.Build(
            question,
            recalled: Array.Empty<Message>(),
            rankedItems: Array.Empty<MemoryContextRankedItem>(),
            originsByMessageId: new Dictionary<string, LongMemEvalMessageOrigin>(StringComparer.Ordinal),
            detail: LongMemEvalEvidenceDetail.Identifiers,
            answerPromptCharacters: 400,
            configuredMessageBudget: 0);

        var classification = LongMemEvalPostRunDiagnostics.ClassifyForTest(evidence);

        classification.Should().Be("retrieval-not-observable");
        classification.Should().NotBe("retrieval-miss");
        classification.Should().NotBe("answer-synthesis-failure");
    }
}
