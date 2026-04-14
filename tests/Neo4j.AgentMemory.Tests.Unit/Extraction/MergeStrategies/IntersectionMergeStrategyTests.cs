using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Core.Extraction.MergeStrategies;

namespace Neo4j.AgentMemory.Tests.Unit.Extraction.MergeStrategies;

public sealed class IntersectionMergeStrategyTests
{
    private readonly IntersectionMergeStrategy<ExtractedEntity> _sut = new(
        e => e.Name, e => e.Confidence);

    private static ExtractedEntity Entity(string name, double confidence = 1.0) =>
        new() { Name = name, Type = "Person", Confidence = confidence };

    [Fact]
    public void StrategyType_ReturnsIntersection()
    {
        _sut.StrategyType.Should().Be(MergeStrategyType.Intersection);
    }

    [Fact]
    public void Merge_EmptyInput_ReturnsEmpty()
    {
        var result = _sut.Merge(Array.Empty<IReadOnlyList<ExtractedEntity>>());
        result.Should().BeEmpty();
    }

    [Fact]
    public void Merge_SingleExtractor_ReturnsEmpty_BecauseNoOverlap()
    {
        var input = new List<IReadOnlyList<ExtractedEntity>>
        {
            new[] { Entity("Alice"), Entity("Bob") }
        };

        // With only one extractor, no item is found by 2+ extractors.
        var result = _sut.Merge(input);
        result.Should().BeEmpty();
    }

    [Fact]
    public void Merge_TwoExtractors_KeepsOnlyCommonItems()
    {
        var input = new List<IReadOnlyList<ExtractedEntity>>
        {
            new[] { Entity("Alice"), Entity("Bob") },
            new[] { Entity("alice"), Entity("Charlie") }
        };

        var result = _sut.Merge(input);
        result.Should().ContainSingle()
            .Which.Name.Should().BeOneOf("Alice", "alice");
    }

    [Fact]
    public void Merge_NoOverlap_ReturnsEmpty()
    {
        var input = new List<IReadOnlyList<ExtractedEntity>>
        {
            new[] { Entity("Alice") },
            new[] { Entity("Bob") }
        };

        var result = _sut.Merge(input);
        result.Should().BeEmpty();
    }

    [Fact]
    public void Merge_AllExtractorsHaveSameItem_KeepsIt()
    {
        var input = new List<IReadOnlyList<ExtractedEntity>>
        {
            new[] { Entity("Alice", 0.7) },
            new[] { Entity("Alice", 0.9) },
            new[] { Entity("Alice", 0.5) }
        };

        var result = _sut.Merge(input);
        result.Should().ContainSingle()
            .Which.Confidence.Should().Be(0.9);
    }

    [Fact]
    public void Merge_PicksHighestConfidenceFromDuplicates()
    {
        var input = new List<IReadOnlyList<ExtractedEntity>>
        {
            new[] { Entity("Alice", 0.6), Entity("Bob", 0.8) },
            new[] { Entity("Alice", 0.9), Entity("Bob", 0.5) }
        };

        var result = _sut.Merge(input);
        result.Should().HaveCount(2);
        result.First(e => e.Name.Equals("Alice", StringComparison.OrdinalIgnoreCase))
            .Confidence.Should().Be(0.9);
        result.First(e => e.Name.Equals("Bob", StringComparison.OrdinalIgnoreCase))
            .Confidence.Should().Be(0.8);
    }

    [Fact]
    public void Merge_CaseInsensitiveMatching()
    {
        var input = new List<IReadOnlyList<ExtractedEntity>>
        {
            new[] { Entity("ALICE") },
            new[] { Entity("alice") }
        };

        var result = _sut.Merge(input);
        result.Should().ContainSingle();
    }
}
