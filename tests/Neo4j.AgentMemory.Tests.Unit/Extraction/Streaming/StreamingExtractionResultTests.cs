using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Domain.Extraction.Streaming;

namespace Neo4j.AgentMemory.Tests.Unit.Extraction.Streaming;

public sealed class StreamingExtractionResultTests
{
    [Fact]
    public void ToExtractionResult_MapsEntitiesAndRelationships()
    {
        var entities = new[]
        {
            new ExtractedEntity { Name = "Alice", Type = "PERSON" }
        };
        var rels = new[]
        {
            new ExtractedRelationship
                { SourceEntity = "A", RelationshipType = "R", TargetEntity = "B" }
        };

        var sut = new StreamingExtractionResult
        {
            Entities = entities,
            Relationships = rels,
            Stats = new StreamingExtractionStats()
        };

        var result = sut.ToExtractionResult();

        result.Entities.Should().BeEquivalentTo(entities);
        result.Relationships.Should().BeEquivalentTo(rels);
    }

    [Fact]
    public void ToExtractionResult_EmptyCollections_ReturnsEmptyResult()
    {
        var sut = new StreamingExtractionResult
        {
            Stats = new StreamingExtractionStats()
        };

        var result = sut.ToExtractionResult();

        result.Entities.Should().BeEmpty();
        result.Relationships.Should().BeEmpty();
    }

    [Fact]
    public void Stats_DefaultValues_AreZero()
    {
        var stats = new StreamingExtractionStats();

        stats.TotalChunks.Should().Be(0);
        stats.SuccessfulChunks.Should().Be(0);
        stats.FailedChunks.Should().Be(0);
        stats.TotalEntities.Should().Be(0);
        stats.TotalRelations.Should().Be(0);
        stats.DeduplicatedEntities.Should().Be(0);
        stats.TotalDurationMs.Should().Be(0.0);
        stats.TotalCharacters.Should().Be(0);
        stats.TotalTokensApprox.Should().Be(0);
    }
}
