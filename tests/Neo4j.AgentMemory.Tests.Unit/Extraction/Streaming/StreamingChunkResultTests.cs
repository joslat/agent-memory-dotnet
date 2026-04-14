using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Domain.Extraction.Streaming;

namespace Neo4j.AgentMemory.Tests.Unit.Extraction.Streaming;

public sealed class StreamingChunkResultTests
{
    [Fact]
    public void EntityCount_ReflectsResultEntities()
    {
        var result = new Abstractions.Domain.ExtractionResult
        {
            Entities = new[]
            {
                new Abstractions.Domain.ExtractedEntity { Name = "A", Type = "T" },
                new Abstractions.Domain.ExtractedEntity { Name = "B", Type = "T" }
            }
        };

        var chunk = new ChunkInfo
            { Index = 0, StartChar = 0, EndChar = 10, Text = "Hello!", IsFirst = true, IsLast = true };

        var sut = new StreamingChunkResult { Chunk = chunk, Result = result };

        sut.EntityCount.Should().Be(2);
    }

    [Fact]
    public void RelationCount_ReflectsResultRelationships()
    {
        var result = new Abstractions.Domain.ExtractionResult
        {
            Relationships = new[]
            {
                new Abstractions.Domain.ExtractedRelationship
                {
                    SourceEntity = "A", RelationshipType = "R", TargetEntity = "B"
                }
            }
        };

        var chunk = new ChunkInfo
            { Index = 0, StartChar = 0, EndChar = 5, Text = "text", IsFirst = true, IsLast = true };

        var sut = new StreamingChunkResult { Chunk = chunk, Result = result };

        sut.RelationCount.Should().Be(1);
    }

    [Fact]
    public void Success_DefaultsToTrue()
    {
        var chunk = new ChunkInfo
            { Index = 0, StartChar = 0, EndChar = 4, Text = "ok", IsFirst = true, IsLast = true };
        var sut = new StreamingChunkResult
        {
            Chunk = chunk,
            Result = new Abstractions.Domain.ExtractionResult()
        };
        sut.Success.Should().BeTrue();
    }

    [Fact]
    public void Error_DefaultsToNull()
    {
        var chunk = new ChunkInfo
            { Index = 0, StartChar = 0, EndChar = 4, Text = "ok", IsFirst = true, IsLast = true };
        var sut = new StreamingChunkResult
        {
            Chunk = chunk,
            Result = new Abstractions.Domain.ExtractionResult()
        };
        sut.Error.Should().BeNull();
    }
}
