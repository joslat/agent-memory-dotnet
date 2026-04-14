using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Core.Extraction.MergeStrategies;

namespace Neo4j.AgentMemory.Tests.Unit.Extraction.MergeStrategies;

public sealed class CascadeMergeStrategyTests
{
    private readonly CascadeMergeStrategy<ExtractedEntity> _sut = new();

    private static ExtractedEntity Entity(string name) =>
        new() { Name = name, Type = "Person" };

    [Fact]
    public void StrategyType_ReturnsCascade()
    {
        _sut.StrategyType.Should().Be(MergeStrategyType.Cascade);
    }

    [Fact]
    public void Merge_EmptyInput_ReturnsEmpty()
    {
        var result = _sut.Merge(Array.Empty<IReadOnlyList<ExtractedEntity>>());
        result.Should().BeEmpty();
    }

    [Fact]
    public void Merge_FirstNonEmpty_ReturnsIt()
    {
        var input = new List<IReadOnlyList<ExtractedEntity>>
        {
            Array.Empty<ExtractedEntity>(),
            new[] { Entity("Alice"), Entity("Bob") },
            new[] { Entity("Charlie") }
        };

        var result = _sut.Merge(input);
        result.Should().HaveCount(2);
        result.Select(e => e.Name).Should().BeEquivalentTo("Alice", "Bob");
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
    public void Merge_FirstIsNonEmpty_ReturnsThat()
    {
        var input = new List<IReadOnlyList<ExtractedEntity>>
        {
            new[] { Entity("Alice") },
            new[] { Entity("Bob") }
        };

        var result = _sut.Merge(input);
        result.Should().ContainSingle().Which.Name.Should().Be("Alice");
    }

    [Fact]
    public void Merge_SkipsEmptyToFindFirst()
    {
        var input = new List<IReadOnlyList<ExtractedEntity>>
        {
            Array.Empty<ExtractedEntity>(),
            Array.Empty<ExtractedEntity>(),
            new[] { Entity("Found") }
        };

        var result = _sut.Merge(input);
        result.Should().ContainSingle().Which.Name.Should().Be("Found");
    }
}
