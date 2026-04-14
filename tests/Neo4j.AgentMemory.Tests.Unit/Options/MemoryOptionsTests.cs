using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Options;

namespace Neo4j.AgentMemory.Tests.Unit.OptionsTests;

public class MemoryOptionsTests
{
    [Fact]
    public void DefaultOptions_ShortTermIsNotNull()
    {
        var options = new MemoryOptions();
        options.ShortTerm.Should().NotBeNull();
    }

    [Fact]
    public void DefaultOptions_LongTermIsNotNull()
    {
        var options = new MemoryOptions();
        options.LongTerm.Should().NotBeNull();
    }

    [Fact]
    public void DefaultOptions_ReasoningIsNotNull()
    {
        var options = new MemoryOptions();
        options.Reasoning.Should().NotBeNull();
    }

    [Fact]
    public void DefaultOptions_RecallIsNotNull()
    {
        var options = new MemoryOptions();
        options.Recall.Should().NotBeNull();
    }

    [Fact]
    public void DefaultOptions_ContextBudgetIsNotNull()
    {
        var options = new MemoryOptions();
        options.ContextBudget.Should().NotBeNull();
    }

    [Fact]
    public void DefaultOptions_ExtractionIsNotNull()
    {
        var options = new MemoryOptions();
        options.Extraction.Should().NotBeNull();
    }

    [Fact]
    public void DefaultOptions_EnableAutoExtractionIsTrue()
    {
        var options = new MemoryOptions();
        options.EnableAutoExtraction.Should().BeTrue();
    }

    [Fact]
    public void DefaultOptions_EnableGraphRagIsFalse()
    {
        var options = new MemoryOptions();
        options.EnableGraphRag.Should().BeFalse();
    }

    [Fact]
    public void WithInit_CanOverrideNestedOptions()
    {
        var options = new MemoryOptions
        {
            ShortTerm = new ShortTermMemoryOptions { MaxMessagesPerQuery = 50 },
            EnableGraphRag = true
        };

        options.ShortTerm.MaxMessagesPerQuery.Should().Be(50);
        options.EnableGraphRag.Should().BeTrue();
    }

    [Fact]
    public void WithInit_CanDisableAutoExtraction()
    {
        var options = new MemoryOptions { EnableAutoExtraction = false };
        options.EnableAutoExtraction.Should().BeFalse();
    }
}
