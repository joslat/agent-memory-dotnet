using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Core.Extraction.MergeStrategies;

namespace Neo4j.AgentMemory.Tests.Unit.Extraction.MergeStrategies;

public sealed class MergeStrategyFactoryTests
{
    [Theory]
    [InlineData(MergeStrategyType.Union)]
    [InlineData(MergeStrategyType.Intersection)]
    [InlineData(MergeStrategyType.Confidence)]
    [InlineData(MergeStrategyType.Cascade)]
    [InlineData(MergeStrategyType.FirstSuccess)]
    public void CreateEntityStrategy_AllTypes_ReturnsCorrectStrategy(MergeStrategyType strategyType)
    {
        var strategy = MergeStrategyFactory.CreateEntityStrategy(strategyType);
        strategy.StrategyType.Should().Be(strategyType);
    }

    [Theory]
    [InlineData(MergeStrategyType.Union)]
    [InlineData(MergeStrategyType.Intersection)]
    [InlineData(MergeStrategyType.Confidence)]
    [InlineData(MergeStrategyType.Cascade)]
    [InlineData(MergeStrategyType.FirstSuccess)]
    public void CreateFactStrategy_AllTypes_ReturnsCorrectStrategy(MergeStrategyType strategyType)
    {
        var strategy = MergeStrategyFactory.CreateFactStrategy(strategyType);
        strategy.StrategyType.Should().Be(strategyType);
    }

    [Theory]
    [InlineData(MergeStrategyType.Union)]
    [InlineData(MergeStrategyType.Intersection)]
    [InlineData(MergeStrategyType.Confidence)]
    [InlineData(MergeStrategyType.Cascade)]
    [InlineData(MergeStrategyType.FirstSuccess)]
    public void CreatePreferenceStrategy_AllTypes_ReturnsCorrectStrategy(MergeStrategyType strategyType)
    {
        var strategy = MergeStrategyFactory.CreatePreferenceStrategy(strategyType);
        strategy.StrategyType.Should().Be(strategyType);
    }

    [Theory]
    [InlineData(MergeStrategyType.Union)]
    [InlineData(MergeStrategyType.Intersection)]
    [InlineData(MergeStrategyType.Confidence)]
    [InlineData(MergeStrategyType.Cascade)]
    [InlineData(MergeStrategyType.FirstSuccess)]
    public void CreateRelationshipStrategy_AllTypes_ReturnsCorrectStrategy(MergeStrategyType strategyType)
    {
        var strategy = MergeStrategyFactory.CreateRelationshipStrategy(strategyType);
        strategy.StrategyType.Should().Be(strategyType);
    }

    [Fact]
    public void CreateEntityStrategy_InvalidType_Throws()
    {
        var act = () => MergeStrategyFactory.CreateEntityStrategy((MergeStrategyType)999);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void FactStrategy_DeduplicatesBySpoTriple_CaseInsensitive()
    {
        var strategy = MergeStrategyFactory.CreateFactStrategy(MergeStrategyType.Union);
        var input = new List<IReadOnlyList<ExtractedFact>>
        {
            new[] { new ExtractedFact { Subject = "Alice", Predicate = "likes", Object = "Cats" } },
            new[] { new ExtractedFact { Subject = "alice", Predicate = "LIKES", Object = "cats" } }
        };

        var result = strategy.Merge(input);
        result.Should().ContainSingle();
    }

    [Fact]
    public void RelationshipStrategy_DeduplicatesByKey_CaseInsensitive()
    {
        var strategy = MergeStrategyFactory.CreateRelationshipStrategy(MergeStrategyType.Union);
        var input = new List<IReadOnlyList<ExtractedRelationship>>
        {
            new[] { new ExtractedRelationship { SourceEntity = "Alice", RelationshipType = "KNOWS", TargetEntity = "Bob" } },
            new[] { new ExtractedRelationship { SourceEntity = "alice", RelationshipType = "knows", TargetEntity = "bob" } }
        };

        var result = strategy.Merge(input);
        result.Should().ContainSingle();
    }
}
