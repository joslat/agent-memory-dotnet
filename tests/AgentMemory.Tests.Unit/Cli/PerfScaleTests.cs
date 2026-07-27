using AgentMemory.Cli.Perf;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Cli;

public sealed class PerfScaleTests
{
    [Theory]
    [InlineData(null, PerfScale.Small)]
    [InlineData("", PerfScale.Small)]
    [InlineData("S", PerfScale.Small)]
    [InlineData("s", PerfScale.Small)]
    [InlineData("M", PerfScale.Medium)]
    [InlineData("m", PerfScale.Medium)]
    public void Parse_AcceptsDocumentedScaleValues(string? value, PerfScale expected)
    {
        PerfScaleParser.Parse(value).Should().Be(expected);
    }

    [Fact]
    public void Parse_RejectsUnknownScale()
    {
        var act = () => PerfScaleParser.Parse("L");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*--scale*'S'*'M'*");
    }

    [Fact]
    public void MediumDataset_HasExactPredictedShape()
    {
        ScaleMDataset.MemoryNodeCount.Should().Be(250_000);
        ScaleMDataset.NodesPerLabel.Should().Be(50_000);
        ScaleMDataset.ChunkSize.Should().Be(1_000);
        ScaleMDataset.MemoryLabels.Should().Equal(
            "Entity",
            "Fact",
            "Preference",
            "Message",
            "ReasoningTrace");
    }

    [Fact]
    public void MediumSnapshotName_IsVersionedByDimensions()
    {
        ScaleMDataset.SnapshotVolumeName(384)
            .Should().Be("agentmemory-perf-m-neo4j-5-26-d384-v1");
    }
}

