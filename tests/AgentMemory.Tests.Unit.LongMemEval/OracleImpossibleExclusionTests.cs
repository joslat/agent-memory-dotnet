using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// 27.3. The oracle-impossible exclusion must be visible, reversible, and falsifiable.
/// </summary>
/// <remarks>
/// <para>
/// Four questions are answered wrongly by a perfect-context oracle 8 times out of 8, so no memory
/// system can reach them and leaving them in the denominator caps the score for reasons unrelated to
/// memory. Excluding them is right; excluding them <i>quietly</i> would be much worse than not
/// excluding them at all, which is what these tests are for.
/// </para>
/// <para>
/// The list is a claim about the world and can be wrong — an earlier version of it named two questions
/// that turned out to be solvable with perfect context (3/4 and 4/4). So the contradiction path is
/// tested as carefully as the happy path.
/// </para>
/// </remarks>
public sealed class OracleImpossibleExclusionTests
{
    [Fact]
    public void BothDenominatorsAreReportedAndTheExcludedQuestionsAreNamed()
    {
        var results = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["352ab8bd"] = false,   // oracle-impossible
            ["58470ed2"] = false,   // oracle-impossible
            ["solvable-a"] = true,
            ["solvable-b"] = true,
            ["solvable-c"] = false,
        };

        var score = LongMemEvalOracleImpossible.Score(results);

        score.RawAccuracy.Should().Be(2.0 / 5);
        score.ImprovableAccuracy.Should().Be(2.0 / 3);
        score.ExcludedQuestionIds.Should().BeEquivalentTo(["352ab8bd", "58470ed2"]);
        score.ExclusionContradicted.Should().BeNull();
    }

    [Fact]
    public void AnExcludedQuestionAnsweredCorrectlyFalsifiesItsOwnExclusion()
    {
        // The load-bearing test. If an oracle-impossible question is ever answered correctly, the list
        // is wrong about it, and the report must say so loudly rather than absorb the evidence against
        // itself. Without this, a curated exclusion list decays into a way of not counting questions
        // that are merely inconvenient.
        var score = LongMemEvalOracleImpossible.Score(new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["352ab8bd"] = true,
            ["solvable-a"] = true,
        });

        score.ExclusionContradicted.Should().NotBeNull();
        score.ExclusionContradicted.Should().Contain("oracle-impossible");

        // And it must not silently inflate the improvable score by leaving the correct answer in the
        // numerator while removing the question from the denominator.
        score.ImprovableCorrect.Should().Be(1);
        score.ImprovableQuestions.Should().Be(1);
        score.ImprovableAccuracy.Should().Be(1.0);
    }

    [Fact]
    public void ARunContainingNoneOfThemIsUnaffected()
    {
        // The common case: most runs sample questions that are all improvable, and the exclusion must
        // then be a no-op rather than a quiet shift in the denominator.
        var score = LongMemEvalOracleImpossible.Score(new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["solvable-a"] = true,
            ["solvable-b"] = false,
        });

        score.ExcludedQuestionIds.Should().BeEmpty();
        score.RawAccuracy.Should().Be(score.ImprovableAccuracy);
    }

    [Fact]
    public void EveryExcludedQuestionCarriesItsEvidence()
    {
        // An exclusion without a reason is indistinguishable from a convenient one. Reflected rather
        // than listed so the assertion cannot go stale when the list changes.
        LongMemEvalOracleImpossible.Questions.Should().NotBeEmpty();
        LongMemEvalOracleImpossible.Questions.Values.Should()
            .OnlyContain(evidence => evidence.Contains("with perfect context"),
                "an excluded question must record the oracle result that excluded it");
    }

    [Fact]
    public void QuestionsProvenSolvableAreNotOnTheList()
    {
        // Regression guard for a real error. An earlier writeup named these two as oracle-impossible;
        // the oracle then scored them 3/4 and 4/4 respectively, and gpt4_8279ba03 is in fact a pure
        // retrieval miss. Excluding either would have hidden a genuine memory failure.
        LongMemEvalOracleImpossible.IsImpossible("031748ae_abs").Should().BeFalse();
        LongMemEvalOracleImpossible.IsImpossible("gpt4_8279ba03").Should().BeFalse();
    }
}
