using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// Whether a content refusal cost a question its <b>answer</b>, or merely some context.
/// </summary>
/// <remarks>
/// <para>
/// A refused source session is skipped so the build survives — 3 of 2,386 sessions is well inside
/// tolerance. But tolerance is a statement about <i>volume</i>, not about <i>consequence</i>. If the
/// refused session held a question's gold evidence, that question is unanswerable from memory for a
/// reason unrelated to retrieval, and counting it wrong attributes a content-policy decision to
/// recall quality.
/// </para>
/// <para>
/// Recording that sessions were refused is not enough. What matters is whether they mattered.
/// </para>
/// </remarks>
public sealed class RefusedEvidenceTests
{
    private static LongMemEvalMessageOrigin Message(int sourceSession, bool hasAnswer) =>
        new(
            MessageOrdinal: 0,
            SourceSessionId: $"s{sourceSession}",
            SourceSessionOrdinal: sourceSession,
            SourceTurnOrdinal: 0,
            SourceTimestamp: "2026-01-01",
            Role: "user",
            FormattedContent: "",
            IsSyntheticBoundary: false,
            IsSyntheticFormatterPadding: false,
            HasAnswer: hasAnswer);

    private static LongMemEvalEvidenceQuestion Question(
        string id, params LongMemEvalMessageOrigin[] messages) =>
        new(
            QuestionId: id,
            QuestionType: "single-session-user",
            Question: "q",
            InvocationPrompt: "q",
            GoldAnswer: "a",
            QuestionDate: "2026-01-01",
            IsAbstention: false,
            AnswerSessionIds: new HashSet<string>(StringComparer.Ordinal),
            AnnotatedGoldTurnCount: 1,
            Messages: messages);

    private static string SessionId(int question, int source) =>
        $"longmemeval-prepared-20260812T140253Z-session-{question:D4}-source-{source:D4}";

    [Fact]
    public void ARefusedSessionHoldingGoldEvidenceIsFlagged()
    {
        // THE case. This question can no longer be answered from memory, and nothing about that is a
        // retrieval failure.
        var questions = new[] { Question("q-1", Message(25, hasAnswer: true)) };

        var analysed = LongMemEvalRefusedEvidence.Analyse([SessionId(1, 25)], questions);

        analysed.Should().ContainSingle();
        analysed[0].HeldGoldEvidence.Should().BeTrue();
        analysed[0].QuestionId.Should().Be("q-1");
    }

    [Fact]
    public void ARefusedSessionHoldingOnlyContextIsNotFlagged()
    {
        // The common and genuinely harmless case: a conversation the question never depended on. It
        // is still reported by id, because "harmless" is a judgement the reader should be able to
        // check rather than one buried in a filter.
        var questions = new[] { Question("q-1", Message(25, hasAnswer: false)) };

        var analysed = LongMemEvalRefusedEvidence.Analyse([SessionId(1, 25)], questions);

        analysed[0].HeldGoldEvidence.Should().BeFalse();
        analysed[0].QuestionId.Should().Be("q-1");
    }

    [Fact]
    public void TheGoldCheckIsScopedToTheRefusedSourceSession()
    {
        // A question whose answer lives in session 30 is untouched by a refusal of session 25. Without
        // the ordinal comparison every refusal would look catastrophic for its whole question.
        var questions = new[]
        {
            Question("q-1", Message(25, hasAnswer: false), Message(30, hasAnswer: true)),
        };

        LongMemEvalRefusedEvidence.Analyse([SessionId(1, 25)], questions)[0]
            .HeldGoldEvidence.Should().BeFalse();
    }

    [Fact]
    public void TheQuestionIndexInTheIdIsOneBased()
    {
        // Preparation numbers questions from 1 and the id carries that. An off-by-one here would
        // attribute every refusal to the neighbouring question -- and still produce a plausible
        // report.
        var questions = new[]
        {
            Question("q-1", Message(3, hasAnswer: true)),
            Question("q-2", Message(3, hasAnswer: true)),
        };

        LongMemEvalRefusedEvidence.Analyse([SessionId(2, 3)], questions)[0]
            .QuestionId.Should().Be("q-2");
    }

    [Fact]
    public void SyntheticFormatterMessagesNeverCountAsGoldEvidence()
    {
        // Session boundaries and padding are the harness's own text. Treating one as gold would report
        // a refusal as costing an answer that never lived there.
        var padding = Message(25, hasAnswer: true) with { IsSyntheticFormatterPadding = true };
        var boundary = Message(26, hasAnswer: true) with { IsSyntheticBoundary = true };
        var questions = new[] { Question("q-1", padding, boundary) };

        var analysed = LongMemEvalRefusedEvidence.Analyse(
            [SessionId(1, 25), SessionId(1, 26)], questions);

        analysed.Should().OnlyContain(r => !r.HeldGoldEvidence);
    }

    [Fact]
    public void AnUnparseableIdIsUnknownRatherThanHarmless()
    {
        // The id shape is a convention, not a contract. If it changes, the analysis must degrade to
        // "we cannot tell" -- reported, with no question attached -- rather than silently answering
        // "nothing was lost".
        var analysed = LongMemEvalRefusedEvidence.Analyse(["some-other-session-naming"], []);

        analysed.Should().ContainSingle();
        analysed[0].QuestionId.Should().BeNull();
        analysed[0].SessionId.Should().Be("some-other-session-naming");
    }

    [Fact]
    public void AQuestionIndexOutsideTheSampleIsUnknownToo()
    {
        var analysed = LongMemEvalRefusedEvidence.Analyse([SessionId(99, 3)], [Question("q-1")]);

        analysed[0].QuestionId.Should().BeNull();
        analysed[0].HeldGoldEvidence.Should().BeFalse();
    }

    [Fact]
    public void NoCompromisedQuestionsProducesNoValidationIssue()
    {
        // A clean run must stay clean. An issue raised on every refusal would train the reader to
        // ignore it, which is how the one that matters gets missed.
        var questions = new[] { Question("q-1", Message(25, hasAnswer: false)) };
        var analysed = LongMemEvalRefusedEvidence.Analyse([SessionId(1, 25)], questions);

        LongMemEvalRefusedEvidence.DescribeCompromisedQuestions(analysed).Should().BeNull();
    }

    [Fact]
    public void ACompromisedQuestionNamesItselfAndTheConsequence()
    {
        // The message has to say what changes about reading the number, not just that something
        // happened -- the count is already reported elsewhere.
        var questions = new[] { Question("q-7", Message(25, hasAnswer: true)) };
        var analysed = LongMemEvalRefusedEvidence.Analyse([SessionId(1, 25)], questions);

        var issue = LongMemEvalRefusedEvidence.DescribeCompromisedQuestions(analysed);

        issue.Should().NotBeNull();
        issue.Should().Contain("q-7").And.Contain("unrelated to retrieval");
    }

    [Fact]
    public void EachCompromisedQuestionIsNamedOnce()
    {
        // Two refused sessions of the same question is one compromised question, not two. A count
        // that double-reported would overstate the damage.
        var questions = new[]
        {
            Question("q-1", Message(3, hasAnswer: true), Message(4, hasAnswer: true)),
        };
        var analysed = LongMemEvalRefusedEvidence.Analyse(
            [SessionId(1, 3), SessionId(1, 4)], questions);

        LongMemEvalRefusedEvidence.DescribeCompromisedQuestions(analysed)
            .Should().Contain("1 question(s)");
    }
}
