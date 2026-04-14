using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Core.Extraction.MergeStrategies;

namespace Neo4j.AgentMemory.Tests.Unit.Extraction.MergeStrategies;

public sealed class ConfidenceMergeStrategyTests
{
    private readonly ConfidenceMergeStrategy<ExtractedEntity> _sut = new(
        e => e.Name, e => e.Confidence);

    private static ExtractedEntity Entity(string name, double confidence = 1.0) =>
        new() { Name = name, Type = "Person", Confidence = confidence };

    [Fact]
    public void StrategyType_ReturnsConfidence()
    {
        _sut.StrategyType.Should().Be(MergeStrategyType.Confidence);
    }

    [Fact]
    public void Merge_EmptyInput_ReturnsEmpty()
    {
        var result = _sut.Merge(Array.Empty<IReadOnlyList<ExtractedEntity>>());
        result.Should().BeEmpty();
    }

    [Fact]
    public void Merge_PicksHighestConfidence()
    {
        var input = new List<IReadOnlyList<ExtractedEntity>>
        {
            new[] { Entity("Alice", 0.6) },
            new[] { Entity("Alice", 0.9) }
        };

        var result = _sut.Merge(input);
        result.Should().ContainSingle()
            .Which.Confidence.Should().Be(0.9);
    }

    [Fact]
    public void Merge_EqualConfidence_KeepsLatest()
    {
        var input = new List<IReadOnlyList<ExtractedEntity>>
        {
            new[] { Entity("Alice", 0.8) },
            new[] { Entity("Alice", 0.8) }
        };

        // Equal confidence — the later one wins (last-write semantics).
        var result = _sut.Merge(input);
        result.Should().ContainSingle();
    }

    [Fact]
    public void Merge_NoDuplicates_PreservesAll()
    {
        var input = new List<IReadOnlyList<ExtractedEntity>>
        {
            new[] { Entity("Alice", 0.9) },
            new[] { Entity("Bob", 0.7) }
        };

        var result = _sut.Merge(input);
        result.Should().HaveCount(2);
    }

    [Fact]
    public void Merge_MultipleExtractors_KeepsBestPerItem()
    {
        var input = new List<IReadOnlyList<ExtractedEntity>>
        {
            new[] { Entity("Alice", 0.5), Entity("Bob", 0.9) },
            new[] { Entity("Alice", 0.8), Entity("Bob", 0.3) },
            new[] { Entity("Alice", 0.6), Entity("Charlie", 0.7) }
        };

        var result = _sut.Merge(input);
        result.Should().HaveCount(3);
        result.First(e => e.Name.Equals("Alice", StringComparison.OrdinalIgnoreCase))
            .Confidence.Should().Be(0.8);
        result.First(e => e.Name.Equals("Bob", StringComparison.OrdinalIgnoreCase))
            .Confidence.Should().Be(0.9);
        result.First(e => e.Name.Equals("Charlie", StringComparison.OrdinalIgnoreCase))
            .Confidence.Should().Be(0.7);
    }

    [Fact]
    public void Merge_SingleExtractor_PassesThrough()
    {
        var input = new List<IReadOnlyList<ExtractedEntity>>
        {
            new[] { Entity("Alice", 0.8), Entity("Bob", 0.6) }
        };

        var result = _sut.Merge(input);
        result.Should().HaveCount(2);
    }
}
