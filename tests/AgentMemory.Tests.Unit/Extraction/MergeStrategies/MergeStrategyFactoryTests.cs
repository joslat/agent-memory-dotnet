using FluentAssertions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Core.Extraction.MergeStrategies;

namespace AgentMemory.Tests.Unit.Extraction.MergeStrategies;

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

    // ER-UNION-KEY: the entity strategies keyed by name alone, so a person and a place of one name from two extractors became
    // one entity (Neo4j's merge keys on name and type). The key is now the name and the type, both without case or spaces
    // around them.
    [Theory]
    [InlineData(MergeStrategyType.Union)]
    [InlineData(MergeStrategyType.Confidence)]
    public void EntityStrategy_keeps_a_person_and_a_place_of_one_name_apart(MergeStrategyType strategyType)
    {
        var strategy = MergeStrategyFactory.CreateEntityStrategy(strategyType);
        var input = new List<IReadOnlyList<ExtractedEntity>>
        {
            new[] { new ExtractedEntity { Name = "Paris", Type = "PERSON", Confidence = 0.9 } },
            new[] { new ExtractedEntity { Name = "Paris", Type = "LOCATION", Confidence = 0.8 } }
        };

        var result = strategy.Merge(input);

        result.Select(e => e.Type).Should().BeEquivalentTo(["PERSON", "LOCATION"]);
    }

    [Fact]
    public void Intersection_needs_both_extractors_to_agree_on_the_type()
    {
        var strategy = MergeStrategyFactory.CreateEntityStrategy(MergeStrategyType.Intersection);
        var input = new List<IReadOnlyList<ExtractedEntity>>
        {
            new[] { new ExtractedEntity { Name = "Paris", Type = "PERSON" }, new ExtractedEntity { Name = "Ana", Type = "PERSON" } },
            new[] { new ExtractedEntity { Name = "Paris", Type = "LOCATION" }, new ExtractedEntity { Name = "Ana", Type = "PERSON" } }
        };

        var result = strategy.Merge(input);

        result.Should().ContainSingle().Which.Name.Should().Be("Ana");
    }

    [Theory]
    [InlineData(MergeStrategyType.Union)]
    [InlineData(MergeStrategyType.Intersection)]
    [InlineData(MergeStrategyType.Confidence)]
    public void EntityStrategy_still_folds_one_entity_written_in_two_cases(MergeStrategyType strategyType)
    {
        var strategy = MergeStrategyFactory.CreateEntityStrategy(strategyType);
        var input = new List<IReadOnlyList<ExtractedEntity>>
        {
            new[] { new ExtractedEntity { Name = "Paris", Type = "Location", Confidence = 0.9 } },
            new[] { new ExtractedEntity { Name = "paris ", Type = " LOCATION", Confidence = 0.8 } }
        };

        var result = strategy.Merge(input);

        result.Should().ContainSingle().Which.Confidence.Should().Be(0.9);
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
