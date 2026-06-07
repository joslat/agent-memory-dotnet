using FluentAssertions;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Neo4j.Infrastructure;

namespace AgentMemory.Tests.Unit.Infrastructure;

public sealed class VectorIndexDimensionValidatorTests
{
    private static VectorIndexDimension Idx(string name, int dims) => new(name, dims);

    [Fact]
    public void FindMismatches_AllMatch_ReturnsEmpty()
    {
        var existing = new[] { Idx("fact_embedding_idx", 1536), Idx("entity_embedding_idx", 1536) };

        VectorIndexDimensionValidator.FindMismatches(1536, existing).Should().BeEmpty();
    }

    [Fact]
    public void FindMismatches_ReturnsOnlyTheOffendingIndexes()
    {
        var existing = new[]
        {
            Idx("fact_embedding_idx", 1536),
            Idx("entity_embedding_idx", 3072),   // stale
            Idx("message_embedding_idx", 768),   // stale
        };

        var mismatches = VectorIndexDimensionValidator.FindMismatches(1536, existing);

        mismatches.Should().HaveCount(2);
        mismatches.Select(m => m.IndexName)
            .Should().BeEquivalentTo("entity_embedding_idx", "message_embedding_idx");
    }

    [Fact]
    public void FindMismatches_EmptyInput_ReturnsEmpty()
        => VectorIndexDimensionValidator.FindMismatches(1536, Array.Empty<VectorIndexDimension>())
            .Should().BeEmpty();

    [Fact]
    public void EnsureMatches_AllMatch_DoesNotThrow()
    {
        var existing = new[] { Idx("fact_embedding_idx", 1536) };

        var act = () => VectorIndexDimensionValidator.EnsureMatches(1536, existing);

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureMatches_NoIndexes_DoesNotThrow()
    {
        var act = () => VectorIndexDimensionValidator.EnsureMatches(1536, Array.Empty<VectorIndexDimension>());

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureMatches_Mismatch_ThrowsWithExpectedDimensionsAndOffenders()
    {
        var existing = new[]
        {
            Idx("fact_embedding_idx", 1536),
            Idx("entity_embedding_idx", 3072),
        };

        var act = () => VectorIndexDimensionValidator.EnsureMatches(1536, existing);

        var ex = act.Should().Throw<EmbeddingDimensionMismatchException>().Which;
        ex.ExpectedDimensions.Should().Be(1536);
        ex.Mismatches.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(Idx("entity_embedding_idx", 3072));
        ex.Message.Should().Contain("entity_embedding_idx").And.Contain("3072").And.Contain("1536");
    }

    [Fact]
    public void EmbeddingDimensionMismatchException_IsASchemaInitializationException()
    {
        // Existing catch handlers for schema-bootstrap failures must keep catching this.
        var ex = new EmbeddingDimensionMismatchException(1536, new[] { Idx("x", 3072) });

        ex.Should().BeAssignableTo<SchemaInitializationException>();
        ex.Should().BeAssignableTo<MemoryException>();
    }
}
