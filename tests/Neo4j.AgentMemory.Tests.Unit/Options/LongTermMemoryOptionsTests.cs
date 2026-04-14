using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Options;

namespace Neo4j.AgentMemory.Tests.Unit.OptionsTests;

public class LongTermMemoryOptionsTests
{
    [Fact]
    public void Default_GenerateEntityEmbeddingsIsTrue()
    {
        var options = new LongTermMemoryOptions();
        options.GenerateEntityEmbeddings.Should().BeTrue();
    }

    [Fact]
    public void Default_GenerateFactEmbeddingsIsTrue()
    {
        var options = new LongTermMemoryOptions();
        options.GenerateFactEmbeddings.Should().BeTrue();
    }

    [Fact]
    public void Default_GeneratePreferenceEmbeddingsIsTrue()
    {
        var options = new LongTermMemoryOptions();
        options.GeneratePreferenceEmbeddings.Should().BeTrue();
    }

    [Fact]
    public void Default_EnableEntityResolutionIsTrue()
    {
        var options = new LongTermMemoryOptions();
        options.EnableEntityResolution.Should().BeTrue();
    }

    [Fact]
    public void Default_MinConfidenceThresholdIs050()
    {
        var options = new LongTermMemoryOptions();
        options.MinConfidenceThreshold.Should().Be(0.5);
    }

    [Fact]
    public void Default_MinConfidenceThresholdIsInValidRange()
    {
        var options = new LongTermMemoryOptions();
        options.MinConfidenceThreshold.Should().BeInRange(0.0, 1.0);
    }

    [Fact]
    public void WithInit_CanDisableEntityResolution()
    {
        var options = new LongTermMemoryOptions { EnableEntityResolution = false };
        options.EnableEntityResolution.Should().BeFalse();
    }
}
