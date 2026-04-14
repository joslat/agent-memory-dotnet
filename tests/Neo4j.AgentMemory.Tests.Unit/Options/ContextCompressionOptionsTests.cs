using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Options;

namespace Neo4j.AgentMemory.Tests.Unit.OptionsTests;

public class ContextCompressionOptionsTests
{
    [Fact]
    public void Default_TokenThresholdIs30000()
    {
        var options = new ContextCompressionOptions();
        options.TokenThreshold.Should().Be(30_000);
    }

    [Fact]
    public void Default_RecentMessageCountIs10()
    {
        var options = new ContextCompressionOptions();
        options.RecentMessageCount.Should().Be(10);
    }

    [Fact]
    public void Default_MaxObservationsIs5()
    {
        var options = new ContextCompressionOptions();
        options.MaxObservations.Should().Be(5);
    }

    [Fact]
    public void Default_EnableReflectionsIsTrue()
    {
        var options = new ContextCompressionOptions();
        options.EnableReflections.Should().BeTrue();
    }

    [Fact]
    public void Default_TokenThresholdIsPositive()
    {
        var options = new ContextCompressionOptions();
        options.TokenThreshold.Should().BePositive();
    }

    [Fact]
    public void CanOverride_AllProperties()
    {
        var options = new ContextCompressionOptions
        {
            TokenThreshold = 10_000,
            RecentMessageCount = 5,
            MaxObservations = 3,
            EnableReflections = false
        };

        options.TokenThreshold.Should().Be(10_000);
        options.RecentMessageCount.Should().Be(5);
        options.MaxObservations.Should().Be(3);
        options.EnableReflections.Should().BeFalse();
    }
}
