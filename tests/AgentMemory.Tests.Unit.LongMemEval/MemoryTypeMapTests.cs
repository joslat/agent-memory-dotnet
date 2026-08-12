using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// The task-label → memory-type mapping that every per-type figure inherits.
/// </summary>
public sealed class MemoryTypeMapTests
{
    private static readonly LongMemEvalMemoryTypeMap Map = LongMemEvalMemoryTypeMap.Default;

    [Theory]
    [InlineData("single-session-user", "semantic")]
    [InlineData("single-session-preference", "semantic")]
    [InlineData("multi-session", "semantic")]
    [InlineData("single-session-assistant", "episodic")]
    [InlineData("temporal-reasoning", "temporal")]
    [InlineData("knowledge-update", "temporal")]
    public void EveryDatasetTaskTypeIsMapped(string taskType, string expected)
    {
        // All six of LongMemEval's labels, pinned. An unmapped label silently becoming "unmapped" in a
        // published table is the failure this covers.
        Map.ForQuestion(taskType, isAbstention: false).Should().ContainSingle().Which.Should().Be(expected);
    }

    [Fact]
    public void AbstentionOverridesTheTaskLabel()
    {
        // An _abs question is answerable only by knowing memory does NOT hold the answer, whatever its
        // task label says it is about -- so the label must not win here.
        Map.ForQuestion("single-session-user", isAbstention: true)
            .Should().ContainSingle().Which.Should().Be(LongMemEvalMemoryTypeMap.MetaMemory);
    }

    [Fact]
    public void AnUnknownLabelIsReportedRatherThanDropped()
    {
        // A shrinking denominator is how a metric stops adding up: an unrecognised label must appear in
        // the output as unmapped, not vanish from the totals.
        Map.ForQuestion("some-future-task-type", isAbstention: false)
            .Should().ContainSingle().Which.Should().Be(LongMemEvalMemoryTypeMap.Unmapped);

        Map.ForQuestion(null, isAbstention: false)
            .Should().ContainSingle().Which.Should().Be(LongMemEvalMemoryTypeMap.Unmapped);
    }

    [Fact]
    public void TheRevisionTravelsWithTheMapping()
    {
        // The mapping is an opinion and will change. A per-type number that cannot name which opinion
        // produced it is not reproducible.
        Map.Revision.Should().NotBeNullOrWhiteSpace();
        Map.Revision.Should().NotBe("unknown");
    }

    [Fact]
    public void ProceduralIsReachableFromNoTaskType()
    {
        // The load-bearing negative. LongMemEval-S contains no tool trajectories, so procedural memory
        // is unreachable here AT ANY SAMPLE SIZE -- and publishing a LongMemEval number as evidence for
        // a procedural tier is exactly the metric substitution this mapping exists to prevent.
        var allTypes = new[]
        {
            "single-session-user", "single-session-preference", "multi-session",
            "single-session-assistant", "temporal-reasoning", "knowledge-update",
        };

        allTypes.SelectMany(t => Map.ForQuestion(t, isAbstention: false))
            .Should().NotContain("procedural");
        Map.ForQuestion("single-session-user", isAbstention: true)
            .Should().NotContain("procedural");
    }
}
