using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Core.Extraction.MergeStrategies;

namespace Neo4j.AgentMemory.Tests.Unit.Extraction.MergeStrategies;

public sealed class UnionMergeStrategyTests
{
    private readonly UnionMergeStrategy<ExtractedEntity> _sut = new(
        e => e.Name, e => e.Confidence);

    private static ExtractedEntity Entity(string name, double confidence = 1.0) =>
        new() { Name = name, Type = "Person", Confidence = confidence };

    [Fact]
    public void StrategyType_ReturnsUnion()
    {
        _sut.StrategyType.Should().Be(MergeStrategyType.Union);
    }

    [Fact]
    public void Merge_EmptyInput_ReturnsEmpty()
    {
        var result = _sut.Merge(Array.Empty<IReadOnlyList<ExtractedEntity>>());
        result.Should().BeEmpty();
    }

    [Fact]
    public void Merge_SingleExtractorResults_ReturnsAll()
    {
        var input = new List<IReadOnlyList<ExtractedEntity>>
        {
            new[] { Entity("Alice"), Entity("Bob") }
        };

        var result = _sut.Merge(input);
        result.Should().HaveCount(2);
    }

    [Fact]
    public void Merge_TwoExtractors_DeduplicatesByName()
    {
        var input = new List<IReadOnlyList<ExtractedEntity>>
        {
            new[] { Entity("Alice"), Entity("Bob") },
            new[] { Entity("alice"), Entity("Charlie") }
        };

        var result = _sut.Merge(input);
        result.Should().HaveCount(3);
        result.Select(e => e.Name.ToLowerInvariant()).Should()
            .BeEquivalentTo("alice", "bob", "charlie");
    }

    [Fact]
    public void Merge_DuplicateNames_KeepsHighestConfidence()
    {
        var input = new List<IReadOnlyList<ExtractedEntity>>
        {
            new[] { Entity("Alice", 0.7) },
            new[] { Entity("alice", 0.9) }
        };

        var result = _sut.Merge(input);
        result.Should().ContainSingle()
            .Which.Confidence.Should().Be(0.9);
    }

    [Fact]
    public void Merge_PreservesAllUniqueItems()
    {
        var input = new List<IReadOnlyList<ExtractedEntity>>
        {
            new[] { Entity("A"), Entity("B") },
            new[] { Entity("C"), Entity("D") },
            new[] { Entity("E") }
        };

        var result = _sut.Merge(input);
        result.Should().HaveCount(5);
    }

    [Fact]
    public void Merge_AllEmpty_ReturnsEmpty()
    {
        var input = new List<IReadOnlyList<ExtractedEntity>>
        {
            Array.Empty<ExtractedEntity>(),
            Array.Empty<ExtractedEntity>()
        };

        var result = _sut.Merge(input);
        result.Should().BeEmpty();
    }

    [Fact]
    public void Merge_Facts_DeduplicatesBySpoTriple()
    {
        var factStrategy = new UnionMergeStrategy<ExtractedFact>(
            f => $"{f.Subject}|{f.Predicate}|{f.Object}".ToUpperInvariant(),
            f => f.Confidence);

        var input = new List<IReadOnlyList<ExtractedFact>>
        {
            new[] { new ExtractedFact { Subject = "Alice", Predicate = "likes", Object = "cats", Confidence = 0.8 } },
            new[] { new ExtractedFact { Subject = "alice", Predicate = "likes", Object = "cats", Confidence = 0.95 } }
        };

        var result = factStrategy.Merge(input);
        result.Should().ContainSingle()
            .Which.Confidence.Should().Be(0.95);
    }
}
