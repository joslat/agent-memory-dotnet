using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Options;

namespace Neo4j.AgentMemory.Tests.Unit.OptionsTests;

public class ShortTermMemoryOptionsTests
{
    [Fact]
    public void Default_SessionStrategyIsPerConversation()
    {
        var options = new ShortTermMemoryOptions();
        options.SessionStrategy.Should().Be(SessionStrategy.PerConversation);
    }

    [Fact]
    public void Default_GenerateEmbeddingsIsTrue()
    {
        var options = new ShortTermMemoryOptions();
        options.GenerateEmbeddings.Should().BeTrue();
    }

    [Fact]
    public void Default_DefaultRecentMessageLimitIs10()
    {
        var options = new ShortTermMemoryOptions();
        options.DefaultRecentMessageLimit.Should().Be(10);
    }

    [Fact]
    public void Default_MaxMessagesPerQueryIs100()
    {
        var options = new ShortTermMemoryOptions();
        options.MaxMessagesPerQuery.Should().Be(100);
    }

    [Fact]
    public void Default_MaxMessagesPerQueryIsPositive()
    {
        var options = new ShortTermMemoryOptions();
        options.MaxMessagesPerQuery.Should().BePositive();
    }

    [Fact]
    public void WithInit_CanOverrideSessionStrategy()
    {
        var options = new ShortTermMemoryOptions { SessionStrategy = SessionStrategy.PerDay };
        options.SessionStrategy.Should().Be(SessionStrategy.PerDay);
    }

    [Fact]
    public void WithInit_CanSetPersistentPerUserStrategy()
    {
        var options = new ShortTermMemoryOptions { SessionStrategy = SessionStrategy.PersistentPerUser };
        options.SessionStrategy.Should().Be(SessionStrategy.PersistentPerUser);
    }
}
