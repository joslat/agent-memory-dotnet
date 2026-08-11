using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// Was the answer ever stored at all? The question no instrument in this track asked.
/// </summary>
/// <remarks>
/// Every existing metric answers "did retrieval find what was stored?" — relation completeness, gold
/// coverage, recall@K. None answered <b>"was the answer stored in the first place?"</b>, and the gap
/// cost a whole investigation: question <c>32260d93</c> asks what was recommended, the assistant's
/// recommendation was never extracted, and the resulting <c>D = 0</c> was diagnosed as a vocabulary
/// problem for weeks.
/// <para>
/// This separates an <b>extraction</b> failure from a <b>retrieval</b> failure. If the answer's
/// distinctive tokens appear nowhere in stored memory, the question is unanswerable from memory in
/// principle, and every retrieval metric computed on it is measuring noise.
/// </para>
/// <para>
/// Deliberately token-overlap rather than semantic matching: this is a <b>floor</b>, not a scorer. A
/// cheap check that can only ever say "the answer is definitely absent" is worth more here than an
/// expensive one that needs its own validation — and an embedding-based version would inherit the
/// very retrieval machinery whose failures it exists to detect.
/// </para>
/// </remarks>
public sealed class AnswerPresenceGateTests
{
    [Fact]
    public void AnAnswerWhoseTokensAppearInMemoryIsPresent()
    {
        var memory = new[]
        {
            "User likes stand-up comedy specials",
            "assistant recommended Hasan Minhaj Homecoming King to User",
        };

        LongMemEvalAnswerPresence.Evaluate("Hasan Minhaj's Homecoming King", memory)
            .Present.Should().BeTrue();
    }

    [Fact]
    public void TheRealFailureIsDetected()
    {
        // Verbatim shape of what owner-0022 actually held: a user profile, and no recommendation.
        var memory = new[]
        {
            "User wants to learn from experienced instructors",
            "User is interested in improving stand-up comedy craft",
            "User asked about online resources or books on comedy writing",
            "User is aspiring stand-up comedian",
        };

        var result = LongMemEvalAnswerPresence.Evaluate("Hasan Minhaj's Homecoming King", memory);

        result.Present.Should().BeFalse();
        result.MatchedTokens.Should().BeEmpty();
    }

    [Fact]
    public void StopwordsAloneNeverEstablishPresence()
    {
        // "the", "of", "a" appear in every corpus. An answer made only of them is not checkable, and
        // must not be reported as present — that would be the metric-that-cannot-fail problem again.
        LongMemEvalAnswerPresence.Evaluate("the and of a", ["User asked about the price of a book"])
            .Checkable.Should().BeFalse();
    }

    [Fact]
    public void AnUncheckableAnswerIsNeitherPresentNorAbsent()
    {
        var result = LongMemEvalAnswerPresence.Evaluate("yes", ["User asked about yes"]);

        result.Checkable.Should().BeFalse();
        result.Present.Should().BeFalse("an unmeasurable question must never be counted as passing");
    }

    [Fact]
    public void PartialOverlapIsReportedRatherThanRounded()
    {
        var result = LongMemEvalAnswerPresence.Evaluate(
            "Hasan Minhaj Homecoming King",
            ["User watched Homecoming King"]);

        result.MatchedTokens.Should().Contain("homecoming").And.Contain("king");
        result.MatchedTokens.Should().NotContain("minhaj");
        result.Coverage.Should().BeApproximately(0.5, 0.001);
    }

    [Fact]
    public void MatchingIsCaseAndPunctuationInsensitive()
    {
        LongMemEvalAnswerPresence.Evaluate("Homecoming King!", ["user watched homecoming king"])
            .Present.Should().BeTrue();
    }

    [Fact]
    public void EmptyMemoryIsAbsentNotUncheckable()
    {
        // The distinction matters: nothing stored is a real, reportable finding about extraction.
        var result = LongMemEvalAnswerPresence.Evaluate("Hasan Minhaj Homecoming King", []);

        result.Checkable.Should().BeTrue();
        result.Present.Should().BeFalse();
    }
}
