using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Core.Extraction.Streaming;

namespace Neo4j.AgentMemory.Tests.Unit.Extraction.Streaming;

public sealed class EntityDeduplicatorTests
{
    // ── DeduplicateEntities ──────────────────────────────────────────────────

    [Fact]
    public void DeduplicateEntities_EmptyList_ReturnsEmpty()
    {
        var result = EntityDeduplicator.DeduplicateEntities(Array.Empty<ExtractedEntity>());
        result.Should().BeEmpty();
    }

    [Fact]
    public void DeduplicateEntities_NoDuplicates_ReturnsAll()
    {
        var entities = new[]
        {
            new ExtractedEntity { Name = "Alice", Type = "PERSON", Confidence = 0.9 },
            new ExtractedEntity { Name = "Acme", Type = "ORGANIZATION", Confidence = 0.8 }
        };

        var result = EntityDeduplicator.DeduplicateEntities(entities);
        result.Should().HaveCount(2);
    }

    [Fact]
    public void DeduplicateEntities_WithDuplicates_KeepsHighestConfidence()
    {
        var entities = new[]
        {
            new ExtractedEntity { Name = "Alice", Type = "PERSON", Confidence = 0.7 },
            new ExtractedEntity { Name = "Alice", Type = "PERSON", Confidence = 0.95 },
            new ExtractedEntity { Name = "Alice", Type = "PERSON", Confidence = 0.8 }
        };

        var result = EntityDeduplicator.DeduplicateEntities(entities);
        result.Should().HaveCount(1);
        result[0].Confidence.Should().Be(0.95);
    }

    [Fact]
    public void DeduplicateEntities_SameNameDifferentTypes_AreNotDeduplicated()
    {
        var entities = new[]
        {
            new ExtractedEntity { Name = "Mercury", Type = "PLANET", Confidence = 0.9 },
            new ExtractedEntity { Name = "Mercury", Type = "CAR", Confidence = 0.85 }
        };

        var result = EntityDeduplicator.DeduplicateEntities(entities);
        result.Should().HaveCount(2);
    }

    [Fact]
    public void DeduplicateEntities_CaseInsensitiveNames_AreDeduped()
    {
        var entities = new[]
        {
            new ExtractedEntity { Name = "alice", Type = "PERSON", Confidence = 0.8 },
            new ExtractedEntity { Name = "ALICE", Type = "PERSON", Confidence = 0.9 }
        };

        var result = EntityDeduplicator.DeduplicateEntities(entities);
        result.Should().HaveCount(1);
        result[0].Confidence.Should().Be(0.9);
    }

    // ── DeduplicateRelationships ─────────────────────────────────────────────

    [Fact]
    public void DeduplicateRelationships_EmptyList_ReturnsEmpty()
    {
        var result = EntityDeduplicator.DeduplicateRelationships(
            Array.Empty<ExtractedRelationship>());
        result.Should().BeEmpty();
    }

    [Fact]
    public void DeduplicateRelationships_NoDuplicates_ReturnsAll()
    {
        var rels = new[]
        {
            new ExtractedRelationship
                { SourceEntity = "Alice", RelationshipType = "WORKS_AT", TargetEntity = "Acme" },
            new ExtractedRelationship
                { SourceEntity = "Bob", RelationshipType = "LIVES_IN", TargetEntity = "London" }
        };

        var result = EntityDeduplicator.DeduplicateRelationships(rels);
        result.Should().HaveCount(2);
    }

    [Fact]
    public void DeduplicateRelationships_WithDuplicates_KeepsHighestConfidence()
    {
        var rels = new[]
        {
            new ExtractedRelationship
            {
                SourceEntity = "Alice", RelationshipType = "WORKS_AT",
                TargetEntity = "Acme", Confidence = 0.6
            },
            new ExtractedRelationship
            {
                SourceEntity = "Alice", RelationshipType = "WORKS_AT",
                TargetEntity = "Acme", Confidence = 0.95
            }
        };

        var result = EntityDeduplicator.DeduplicateRelationships(rels);
        result.Should().HaveCount(1);
        result[0].Confidence.Should().Be(0.95);
    }

    [Fact]
    public void DeduplicateRelationships_CaseInsensitive_AreDeduped()
    {
        var rels = new[]
        {
            new ExtractedRelationship
            {
                SourceEntity = "alice", RelationshipType = "works_at",
                TargetEntity = "acme", Confidence = 0.7
            },
            new ExtractedRelationship
            {
                SourceEntity = "ALICE", RelationshipType = "WORKS_AT",
                TargetEntity = "ACME", Confidence = 0.9
            }
        };

        var result = EntityDeduplicator.DeduplicateRelationships(rels);
        result.Should().HaveCount(1);
        result[0].Confidence.Should().Be(0.9);
    }
}
