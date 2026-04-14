using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Options;

namespace Neo4j.AgentMemory.Tests.Unit.OptionsTests;

public class ContextBudgetTests
{
    [Fact]
    public void Default_MaxTokensIsNull()
    {
        var budget = new ContextBudget();
        budget.MaxTokens.Should().BeNull();
    }

    [Fact]
    public void Default_MaxCharactersIsNull()
    {
        var budget = new ContextBudget();
        budget.MaxCharacters.Should().BeNull();
    }

    [Fact]
    public void Default_TruncationStrategyIsOldestFirst()
    {
        var budget = new ContextBudget();
        budget.TruncationStrategy.Should().Be(TruncationStrategy.OldestFirst);
    }

    [Fact]
    public void Default_StaticDefaultMatchesNewInstance()
    {
        var instance = new ContextBudget();
        var staticDefault = ContextBudget.Default;

        staticDefault.MaxTokens.Should().Be(instance.MaxTokens);
        staticDefault.MaxCharacters.Should().Be(instance.MaxCharacters);
        staticDefault.TruncationStrategy.Should().Be(instance.TruncationStrategy);
    }

    [Fact]
    public void WithInit_CanSetMaxTokens()
    {
        var budget = new ContextBudget { MaxTokens = 4096 };
        budget.MaxTokens.Should().Be(4096);
    }

    [Fact]
    public void WithInit_CanSetMaxCharacters()
    {
        var budget = new ContextBudget { MaxCharacters = 16000 };
        budget.MaxCharacters.Should().Be(16000);
    }

    [Theory]
    [InlineData(TruncationStrategy.OldestFirst)]
    [InlineData(TruncationStrategy.LowestScoreFirst)]
    [InlineData(TruncationStrategy.Proportional)]
    [InlineData(TruncationStrategy.Fail)]
    public void WithInit_AllTruncationStrategiesAreValid(TruncationStrategy strategy)
    {
        var budget = new ContextBudget { TruncationStrategy = strategy };
        budget.TruncationStrategy.Should().Be(strategy);
    }
}
