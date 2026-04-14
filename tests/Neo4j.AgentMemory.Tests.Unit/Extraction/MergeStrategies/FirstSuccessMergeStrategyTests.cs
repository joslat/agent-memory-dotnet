using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Core.Extraction.MergeStrategies;

namespace Neo4j.AgentMemory.Tests.Unit.Extraction.MergeStrategies;

public sealed class FirstSuccessMergeStrategyTests
{
    private readonly FirstSuccessMergeStrategy<ExtractedEntity> _sut = new();

    private static ExtractedEntity Entity(string name) =>
        new() { Name = name, Type = "Person" };

    [Fact]
    public void StrategyType_ReturnsFirstSuccess()
    {
        _sut.StrategyType.Should().Be(MergeStrategyType.FirstSuccess);
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
            new[] { Entity("Alice") },
            new[] { Entity("Bob") }
        };

        var result = _sut.Merge(input);
        result.Should().ContainSingle().Which.Name.Should().Be("Alice");
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
    public void Merge_FirstHasResults_UsesIt()
    {
        var input = new List<IReadOnlyList<ExtractedEntity>>
        {
            new[] { Entity("First"), Entity("First2") },
            new[] { Entity("Second") }
        };

        var result = _sut.Merge(input);
        result.Should().HaveCount(2);
        result.Select(e => e.Name).Should().BeEquivalentTo("First", "First2");
    }

    [Fact]
    public void Merge_EmptyThenSuccess_SkipsEmpty()
    {
        var input = new List<IReadOnlyList<ExtractedEntity>>
        {
            Array.Empty<ExtractedEntity>(),
            Array.Empty<ExtractedEntity>(),
            Array.Empty<ExtractedEntity>(),
            new[] { Entity("FoundIt") }
        };

        var result = _sut.Merge(input);
        result.Should().ContainSingle().Which.Name.Should().Be("FoundIt");
    }
}
