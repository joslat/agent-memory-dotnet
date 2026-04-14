using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Options;

namespace Neo4j.AgentMemory.Tests.Unit.OptionsTests;

public class ReasoningMemoryOptionsTests
{
    [Fact]
    public void Default_GenerateTaskEmbeddingsIsTrue()
    {
        var options = new ReasoningMemoryOptions();
        options.GenerateTaskEmbeddings.Should().BeTrue();
    }

    [Fact]
    public void Default_StoreToolCallsIsTrue()
    {
        var options = new ReasoningMemoryOptions();
        options.StoreToolCalls.Should().BeTrue();
    }

    [Fact]
    public void Default_MaxTracesPerSessionIsNull()
    {
        var options = new ReasoningMemoryOptions();
        options.MaxTracesPerSession.Should().BeNull();
    }

    [Fact]
    public void WithInit_CanSetMaxTracesPerSession()
    {
        var options = new ReasoningMemoryOptions { MaxTracesPerSession = 50 };
        options.MaxTracesPerSession.Should().Be(50);
    }

    [Fact]
    public void WithInit_CanDisableToolCallStorage()
    {
        var options = new ReasoningMemoryOptions { StoreToolCalls = false };
        options.StoreToolCalls.Should().BeFalse();
    }
}
