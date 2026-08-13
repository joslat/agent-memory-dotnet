using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// The provider-free half of the decomposed oracle arm (B2/B3).
/// </summary>
/// <remarks>
/// Parsing the decomposer's reply and accounting for its calls are the two places this arm can be
/// quietly wrong: a parse that silently yields one sub-question makes the arm a copy of the control,
/// and a call count that disagrees with behaviour gets the whole run rejected by the validator. Both
/// are checked here rather than discovered by paying for a run.
/// </remarks>
public sealed class LongMemEvalDecomposedOracleTests
{
    private const string Original = "How old was I when my grandma gave me the silver necklace?";

    [Fact]
    public void EnumeratorsAreStrippedSoTheSubQuestionIsAskable()
    {
        // The prompt forbids numbering and models supply it anyway. "1. When did..." asked verbatim
        // invites the answerer to treat the digit as part of the question.
        var parsed = LongMemEvalDecomposedOracle.ParseSubQuestions(
            "1. What is my date of birth?\n2) When did my grandma give me the necklace?\n- And what year was that?",
            Original,
            maxSubQuestions: 4);

        parsed.Should().Equal(
            "What is my date of birth?",
            "When did my grandma give me the necklace?",
            "And what year was that?");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  \n ")]
    public void AnUnusableReplyFallsBackToTheOriginalQuestion(string? reply)
    {
        // THE safe direction. Falling back to the original degrades this arm into the control, which
        // the void witness then reports as undecomposed. Any other fallback -- an empty list, an
        // invented sub-question -- would either crash the run or answer something nobody asked.
        var parsed = LongMemEvalDecomposedOracle.ParseSubQuestions(reply, Original, maxSubQuestions: 4);

        parsed.Should().ContainSingle().Which.Should().Be(Original);
    }

    [Fact]
    public void AnAtomicQuestionComesBackAsOneSubQuestion()
    {
        // The decomposer is deliberately allowed to refuse. A prompt that always split would measure
        // split-everything rather than decomposition, and would make the comparison's witness vacuous
        // by construction -- it would never be able to report that nothing was decomposed.
        var parsed = LongMemEvalDecomposedOracle.ParseSubQuestions(
            "Where do I live?", "Where do I live?", maxSubQuestions: 4);

        parsed.Should().ContainSingle();
    }

    [Fact]
    public void OverSplittingIsTruncatedRatherThanRejected()
    {
        // Cost per question has to stay predictable: calls are subQuestions + 3, so an unbounded split
        // is an unbounded bill. A decomposer emitting twelve steps has misread the task rather than
        // found twelve, and the recorded count makes the over-split visible in the artifact.
        var reply = string.Join('\n', Enumerable.Range(1, 12).Select(i => $"Sub question {i}?"));

        var parsed = LongMemEvalDecomposedOracle.ParseSubQuestions(reply, Original, maxSubQuestions: 4);

        parsed.Should().HaveCount(4);
        parsed[0].Should().Be("Sub question 1?");
    }

    [Fact]
    public void TheCallCountIsAFunctionOfTheSplitNotAConstant()
    {
        // Decompose + one answer per sub-question + compose + judge. The run validator fail-closes on
        // an exact call count and has already REJECTED a good run whose accounting disagreed with its
        // behaviour, so this is published rather than assumed.
        LongMemEvalDecomposedOracle.ExpectedCalls(1).Should().Be(4);
        LongMemEvalDecomposedOracle.ExpectedCalls(2).Should().Be(5);
        LongMemEvalDecomposedOracle.ExpectedCalls(4).Should().Be(7);
    }

    [Fact]
    public void TheUndecomposedArmStillCostsMoreThanTheControl()
    {
        // Worth pinning: even when the decomposer refuses, this arm spends 4 calls against the
        // monolithic arm's 2. A cost comparison that assumed the arms were equal when nothing split
        // would understate the price of the architecture.
        LongMemEvalDecomposedOracle.ExpectedCalls(1).Should().BeGreaterThan(2);
    }

    [Fact]
    public void MaxSubQuestionsMustBeAtLeastOne()
    {
        // Zero would mean "decompose into nothing", which has no answer and no safe fallback.
        var act = () => LongMemEvalDecomposedOracle.ParseSubQuestions("a\nb", Original, maxSubQuestions: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
