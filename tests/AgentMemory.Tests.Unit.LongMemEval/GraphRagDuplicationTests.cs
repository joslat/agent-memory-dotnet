using AgentMemory.Abstractions.Domain;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// K6. The duplication counter that decides whether GraphRAG adds evidence or re-fetches it.
/// </summary>
/// <remarks>
/// The measurement this supports is the whole point of giving GraphRAG a non-zero budget: pointed at
/// the memory layer's own fact index, does it return rows the structured surface already has? A
/// counter that silently over- or under-reports would produce a confident answer to that question
/// with nothing behind it, so the boundaries are pinned here rather than trusted.
/// </remarks>
public sealed class GraphRagDuplicationTests
{
    [Fact]
    public void AnItemNamingARetrievedFactCounts()
    {
        var count = AgentMemoryLongMemEvalAdapter.CountGraphRagFactsAlreadyRetrieved(
            [Item("f-1"), Item("f-2")], [FactWith("f-1"), FactWith("f-2")]);

        count.Should().Be(2);
    }

    [Fact]
    public void AnItemNamingAFactTheStructuredSurfaceMissedDoesNotCount()
    {
        var count = AgentMemoryLongMemEvalAdapter.CountGraphRagFactsAlreadyRetrieved(
            [Item("f-1"), Item("f-9")], [FactWith("f-1")]);

        count.Should().Be(1);
    }

    [Fact]
    public void AnItemWithNoFactIdIsNotCountedAsDuplicated()
    {
        // The load-bearing boundary. Without the harness's explicit retrieval query there is no node
        // identity at all (K10), and an unidentifiable item must read as "cannot tell", never as
        // "distinct evidence" - which would understate duplication exactly where it matters.
        var count = AgentMemoryLongMemEvalAdapter.CountGraphRagFactsAlreadyRetrieved(
            [new GraphRagContextItem { Text = "Alice likes coffee" }], [FactWith("f-1")]);

        count.Should().Be(0);
    }

    [Fact]
    public void NothingRetrievedMeansNothingDuplicated()
    {
        AgentMemoryLongMemEvalAdapter
            .CountGraphRagFactsAlreadyRetrieved([], [FactWith("f-1")])
            .Should().Be(0);
    }

    private static GraphRagContextItem Item(string factId) => new()
    {
        Text = "some passage",
        Metadata = new Dictionary<string, object> { ["fact_id"] = factId }
    };

    private static Fact FactWith(string factId) => new()
    {
        FactId = factId,
        Subject = "Alice",
        Predicate = "likes",
        Object = "coffee",
        Confidence = 1,
        CreatedAtUtc = DateTimeOffset.UnixEpoch
    };
}
