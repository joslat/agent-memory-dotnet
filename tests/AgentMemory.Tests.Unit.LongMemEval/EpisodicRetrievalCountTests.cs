using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// The retrieval half of episodic memory. Capture was measured (0 → 3,048 relations); nothing ever
/// recorded whether any of it comes BACK.
/// </summary>
/// <remarks>
/// <c>FactsRetrieved</c> counts facts without identifying them, and <c>RetrievalEvidence.RankedItems</c>
/// is empty on the prepared path — so the question had no instrument at all, which is a different
/// state from having been measured and found wanting.
/// </remarks>
public sealed class EpisodicRetrievalCountTests
{
    private static LongMemEvalQuestionTelemetry Q(int n, int facts, int episodic) =>
        new(n, 0, facts, false)
        {
            QuestionId = $"q{n}",
            FactsRetrieved = facts,
            EpisodicFactsRetrieved = episodic,
        };

    [Fact]
    public void EpisodicFactsAreCountedSeparatelyFromTheFactsTheyArePartOf()
    {
        // Episodic facts are a SUBSET of FactsRetrieved, not a parallel channel. Reporting them as a
        // separate total would double-count the context and overstate what retrieval returned.
        var q = Q(1, facts: 38, episodic: 4);

        q.EpisodicFactsRetrieved.Should().BeLessThanOrEqualTo(q.FactsRetrieved);
        (q.FactsRetrieved - q.EpisodicFactsRetrieved).Should().Be(34);
    }

    [Fact]
    public void AQuestionThatRetrievedNoEpisodicFactIsZeroRatherThanUnmeasured()
    {
        // Zero is a finding here: the graph HELD episodic facts and retrieval returned none of them.
        // That is the crowding hypothesis, and it is only visible if zero is recorded rather than
        // treated as "no data".
        Q(1, facts: 38, episodic: 0).EpisodicFactsRetrieved.Should().Be(0);
    }

    [Fact]
    public void TheCounterDefaultsToZeroSoAQuestionThatNeverSetItReadsAsNoneRatherThanUnknown()
    {
        // Measured false-positive floor of the subject heuristic, by probing both frozen bases:
        // Ignore = 17/25,668 facts (0.07%), Utterance = 13,251/36,489 (36.3%). The floor is NOT zero
        // — a user turn genuinely about "the assistant" is captured too — so the claim this counter
        // supports is "36.3% against a 0.07% floor", not "any non-zero proves episodic memory".
        var untouched = new LongMemEvalQuestionTelemetry(1, 0, 10, false) { QuestionId = "q1" };

        untouched.EpisodicFactsRetrieved.Should().Be(0);
    }
}
