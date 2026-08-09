using AgentMemory.Abstractions.Domain;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// Retrieval-side gold coverage: was the evidence in the context, or only in the graph?
/// </summary>
/// <remarks>
/// The n=50 comparison found Structured losing multi-session questions to Hybrid (53.8% vs 84.6%)
/// while retrieving an indistinguishable volume — 61.8 facts on the losses against 59.0 on the wins.
/// Volume was not the problem, and the existing gold-coverage probe could not say what was, because
/// it measures what was <i>learned</i> and runs only during preparation.
/// <para>
/// This measures what was <i>retrieved</i>, which separates three failures a score cannot: never
/// extracted, extracted but not retrieved, retrieved but misread.
/// </para>
/// </remarks>
public sealed class RetrievedGoldCoverageTests
{
    [Fact]
    public void FullCoverageWhenEveryGoldMessageBackedARetrievedFact()
    {
        AgentMemoryLongMemEvalAdapter
            .RetrievedGoldCoverage([FactFrom("m-1"), FactFrom("m-2")], ["m-1", "m-2"])
            .Should().Be(1d);
    }

    [Fact]
    public void PartialCoverageIsReportedAsAFraction()
    {
        AgentMemoryLongMemEvalAdapter
            .RetrievedGoldCoverage([FactFrom("m-1")], ["m-1", "m-2", "m-3", "m-4"])
            .Should().Be(0.25);
    }

    [Fact]
    public void FactsFromOtherMessagesDoNotCount()
    {
        // The load-bearing case: retrieving plenty of facts is not the same as retrieving the right
        // ones, which is precisely the distinction the n=50 telemetry could not draw.
        AgentMemoryLongMemEvalAdapter
            .RetrievedGoldCoverage([FactFrom("m-9"), FactFrom("m-8"), FactFrom("m-7")], ["m-1"])
            .Should().Be(0d);
    }

    [Fact]
    public void OneFactCoveringSeveralGoldMessagesCountsForEach()
    {
        AgentMemoryLongMemEvalAdapter
            .RetrievedGoldCoverage([FactFrom("m-1", "m-2")], ["m-1", "m-2"])
            .Should().Be(1d);
    }

    [Fact]
    public void NoGoldMessagesIsNullRatherThanZero()
    {
        // Zero coverage of nothing is not a miss. Reporting it as 0.0 would drag any average down
        // with questions that never had gold evidence to find.
        AgentMemoryLongMemEvalAdapter
            .RetrievedGoldCoverage([FactFrom("m-1")], [])
            .Should().BeNull();
    }

    private static Fact FactFrom(params string[] sourceMessageIds) => new()
    {
        FactId = string.Join('-', sourceMessageIds),
        Subject = "Alice",
        Predicate = "likes",
        Object = "coffee",
        Confidence = 1,
        CreatedAtUtc = DateTimeOffset.UnixEpoch,
        SourceMessageIds = sourceMessageIds,
    };
}
