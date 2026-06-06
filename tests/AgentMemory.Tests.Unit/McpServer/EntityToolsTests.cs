using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.McpServer;
using AgentMemory.McpServer.Tools;
using NSubstitute;

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class EntityToolsTests
{
    private readonly ILongTermMemoryService _longTermMemory = Substitute.For<ILongTermMemoryService>();
    private readonly IExtractorRepository _extractorRepo = Substitute.For<IExtractorRepository>();
    private readonly IIdGenerator _idGenerator = Substitute.For<IIdGenerator>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IOptions<McpServerOptions> _options = Options.Create(new McpServerOptions());

    private static readonly DateTimeOffset FixedTime = new(2025, 1, 15, 10, 0, 0, TimeSpan.Zero);

    public EntityToolsTests()
    {
        _idGenerator.GenerateId().Returns("rel-id-1");
        _clock.UtcNow.Returns(FixedTime);
    }

    // ── memory_get_entity ──

    [Fact]
    public async Task MemoryGetEntity_CallsGetEntitiesByNameAsyncWithIncludeAliasesTrue()
    {
        _longTermMemory.GetEntitiesByNameAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Entity>());

        await EntityTools.MemoryGetEntity(_longTermMemory, "Alice");

        await _longTermMemory.Received(1).GetEntitiesByNameAsync("Alice", true, Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryGetEntity_ReturnsJsonArray()
    {
        var entities = new List<Entity>
        {
            new()
            {
                EntityId = "e-1",
                Name = "Alice",
                Type = "Person",
                Description = "A developer",
                Confidence = 0.9,
                CreatedAtUtc = FixedTime
            }
        };
        _longTermMemory.GetEntitiesByNameAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(entities);

        var result = await EntityTools.MemoryGetEntity(_longTermMemory, "Alice");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().Should().Be(1);
        doc.RootElement[0].GetProperty("entityId").GetString().Should().Be("e-1");
        doc.RootElement[0].GetProperty("name").GetString().Should().Be("Alice");
        doc.RootElement[0].GetProperty("type").GetString().Should().Be("Person");
    }

    [Fact]
    public async Task MemoryGetEntity_ReturnsEmptyArrayWhenNoEntitiesFound()
    {
        _longTermMemory.GetEntitiesByNameAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Entity>());

        var result = await EntityTools.MemoryGetEntity(_longTermMemory, "Nobody");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().Should().Be(0);
    }

    // ── memory_get_entity_provenance ──

    [Fact]
    public async Task MemoryGetEntityProvenance_ReturnsSourcesAndExtractors_WhenFound()
    {
        var provenance = new EntityProvenance(
            "e-1",
            new[] { new ProvenanceSource("msg-1", 0.9, 0, 5) },
            new[] { new ProvenanceExtractor("llm", 0.95, 120) });
        _extractorRepo.GetProvenanceAsync("e-1", Arg.Any<CancellationToken>())
            .Returns(provenance);

        var result = await EntityTools.MemoryGetEntityProvenance(_extractorRepo, "e-1");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("found").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("entityId").GetString().Should().Be("e-1");
        doc.RootElement.GetProperty("sources").GetArrayLength().Should().Be(1);
        doc.RootElement.GetProperty("extractors")[0].GetProperty("extractorName").GetString().Should().Be("llm");
    }

    [Fact]
    public async Task MemoryGetEntityProvenance_ReturnsNotFound_WhenNull()
    {
        _extractorRepo.GetProvenanceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((EntityProvenance?)null);

        var result = await EntityTools.MemoryGetEntityProvenance(_extractorRepo, "missing");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("found").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("entityId").GetString().Should().Be("missing");
    }

    // ── memory_record_entity_feedback ──

    [Fact]
    public async Task MemoryRecordEntityFeedback_DelegatesAndReturnsUpdatedEntity()
    {
        var updated = new Entity
        {
            EntityId = "e-1", Name = "Alice", Type = "Person",
            Confidence = 0.85, CreatedAtUtc = FixedTime, UpdatedAtUtc = FixedTime
        };
        _longTermMemory.RecordEntityFeedbackAsync("e-1", true, Arg.Any<double?>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(updated);

        var result = await EntityTools.MemoryRecordEntityFeedback(_longTermMemory, "e-1", positive: true);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("found").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("confidence").GetDouble().Should().Be(0.85);
        await _longTermMemory.Received(1).RecordEntityFeedbackAsync("e-1", true, Arg.Any<double?>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryRecordEntityFeedback_ReturnsNotFound_WhenEntityMissing()
    {
        _longTermMemory.RecordEntityFeedbackAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<double?>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns((Entity?)null);

        var result = await EntityTools.MemoryRecordEntityFeedback(_longTermMemory, "missing", positive: false);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("found").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("entityId").GetString().Should().Be("missing");
    }

    // ── memory_create_relationship ──

    [Fact]
    public async Task MemoryCreateRelationship_CallsAddRelationshipAsyncWithCorrectProperties()
    {
        _longTermMemory.AddRelationshipAsync(Arg.Any<Relationship>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Relationship>());

        await EntityTools.MemoryCreateRelationship(
            _longTermMemory, _idGenerator, _clock, _options,
            "e-1", "e-2", "WORKS_FOR", "Employment relationship");

        await _longTermMemory.Received(1).AddRelationshipAsync(
            Arg.Is<Relationship>(r =>
                r.RelationshipId == "rel-id-1" &&
                r.SourceEntityId == "e-1" &&
                r.TargetEntityId == "e-2" &&
                r.RelationshipType == "WORKS_FOR" &&
                r.Description == "Employment relationship" &&
                r.CreatedAtUtc == FixedTime),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryCreateRelationship_UsesDefaultConfidence()
    {
        _longTermMemory.AddRelationshipAsync(Arg.Any<Relationship>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Relationship>());

        await EntityTools.MemoryCreateRelationship(
            _longTermMemory, _idGenerator, _clock, _options,
            "e-1", "e-2", "KNOWS");

        await _longTermMemory.Received(1).AddRelationshipAsync(
            Arg.Is<Relationship>(r => r.Confidence == 0.9),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryCreateRelationship_ReturnsJsonWithRelationshipProperties()
    {
        _longTermMemory.AddRelationshipAsync(Arg.Any<Relationship>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Relationship>());

        var result = await EntityTools.MemoryCreateRelationship(
            _longTermMemory, _idGenerator, _clock, _options,
            "e-1", "e-2", "WORKS_FOR", "Employment", 0.85);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("relationshipId").GetString().Should().Be("rel-id-1");
        doc.RootElement.GetProperty("sourceEntityId").GetString().Should().Be("e-1");
        doc.RootElement.GetProperty("targetEntityId").GetString().Should().Be("e-2");
        doc.RootElement.GetProperty("relationshipType").GetString().Should().Be("WORKS_FOR");
        doc.RootElement.GetProperty("confidence").GetDouble().Should().Be(0.85);
    }
}
