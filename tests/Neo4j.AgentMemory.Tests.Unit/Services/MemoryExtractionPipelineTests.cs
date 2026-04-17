using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Core.Extraction;
using Neo4j.AgentMemory.Core.Services;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="MemoryExtractionPipeline"/>: orchestration behaviour only.
/// Detailed extraction/persistence logic is covered by ExtractionStageTests and PersistenceStageTests.
/// </summary>
public sealed class MemoryExtractionPipelineTests
{
    private readonly IExtractionStage _extractionStage = Substitute.For<IExtractionStage>();
    private readonly IPersistenceStage _persistenceStage = Substitute.For<IPersistenceStage>();

    private MemoryExtractionPipeline CreateSut() =>
        new(_extractionStage, _persistenceStage, NullLogger<MemoryExtractionPipeline>.Instance);

    private static ExtractionRequest MakeRequest(ExtractionTypes types = ExtractionTypes.All) =>
        new()
        {
            Messages = new[] { MakeMessage("msg-1"), MakeMessage("msg-2") },
            SessionId = "session-42",
            TypesToExtract = types
        };

    private static Message MakeMessage(string id) => new()
    {
        MessageId = id,
        ConversationId = "conv-1",
        SessionId = "session-42",
        Role = "user",
        Content = "Hello world",
        TimestampUtc = DateTimeOffset.UtcNow
    };

    private static ExtractionStageResult MakeStageResult(
        IReadOnlyList<ExtractedEntity>? entities = null,
        IReadOnlyList<ExtractedFact>? facts = null,
        IReadOnlyList<ExtractedPreference>? prefs = null,
        IReadOnlyList<ExtractedRelationship>? rels = null) =>
        new()
        {
            RawEntities = entities ?? Array.Empty<ExtractedEntity>(),
            RawFacts = facts ?? Array.Empty<ExtractedFact>(),
            RawPreferences = prefs ?? Array.Empty<ExtractedPreference>(),
            RawRelationships = rels ?? Array.Empty<ExtractedRelationship>(),
            SourceMessageIds = new[] { "msg-1", "msg-2" },
            MergeStrategy = MergeStrategyType.Union
        };

    private static PersistenceResult MakePersistenceResult(
        int entities = 0, int facts = 0, int prefs = 0, int rels = 0) =>
        new() { EntityCount = entities, FactCount = facts, PreferenceCount = prefs, RelationshipCount = rels };

    [Fact]
    public async Task ExtractAsync_DelegatesToExtractionAndPersistenceStages()
    {
        var request = MakeRequest();
        _extractionStage.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<ExtractionTypes>(), Arg.Any<CancellationToken>())
            .Returns(MakeStageResult());
        _persistenceStage.PersistAsync(Arg.Any<ExtractionStageResult>(), Arg.Any<CancellationToken>())
            .Returns(MakePersistenceResult());

        var sut = CreateSut();
        await sut.ExtractAsync(request);

        await _extractionStage.Received(1).ExtractAsync(
            Arg.Is<IReadOnlyList<Message>>(m => m.Count == 2),
            ExtractionTypes.All,
            Arg.Any<CancellationToken>());
        await _persistenceStage.Received(1).PersistAsync(
            Arg.Any<ExtractionStageResult>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_ReturnsRawItemsFromExtractionStage()
    {
        var entities = new[] { new ExtractedEntity { Name = "Alice", Type = "Person", Confidence = 0.9 } };
        var facts = new[] { new ExtractedFact { Subject = "A", Predicate = "b", Object = "c", Confidence = 0.9 } };

        _extractionStage.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<ExtractionTypes>(), Arg.Any<CancellationToken>())
            .Returns(MakeStageResult(entities: entities, facts: facts));
        _persistenceStage.PersistAsync(Arg.Any<ExtractionStageResult>(), Arg.Any<CancellationToken>())
            .Returns(MakePersistenceResult(entities: 1, facts: 1));

        var sut = CreateSut();
        var result = await sut.ExtractAsync(MakeRequest());

        result.Entities.Should().HaveCount(1);
        result.Entities[0].Name.Should().Be("Alice");
        result.Facts.Should().HaveCount(1);
    }

    [Fact]
    public async Task ExtractAsync_MetadataContainsSessionIdAndCounts()
    {
        _extractionStage.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<ExtractionTypes>(), Arg.Any<CancellationToken>())
            .Returns(MakeStageResult());
        _persistenceStage.PersistAsync(Arg.Any<ExtractionStageResult>(), Arg.Any<CancellationToken>())
            .Returns(MakePersistenceResult(entities: 2, facts: 3, prefs: 1, rels: 0));

        var sut = CreateSut();
        var result = await sut.ExtractAsync(MakeRequest());

        result.Metadata.Should().ContainKey("sessionId").WhoseValue.Should().Be("session-42");
        result.Metadata.Should().ContainKey("entityCount").WhoseValue.Should().Be(2);
        result.Metadata.Should().ContainKey("factCount").WhoseValue.Should().Be(3);
        result.Metadata.Should().ContainKey("preferenceCount").WhoseValue.Should().Be(1);
        result.Metadata.Should().ContainKey("relationshipCount").WhoseValue.Should().Be(0);
    }

    [Fact]
    public async Task ExtractAsync_PassesExtractionTypeFlagToStage()
    {
        _extractionStage.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<ExtractionTypes>(), Arg.Any<CancellationToken>())
            .Returns(MakeStageResult());
        _persistenceStage.PersistAsync(Arg.Any<ExtractionStageResult>(), Arg.Any<CancellationToken>())
            .Returns(MakePersistenceResult());

        var sut = CreateSut();
        await sut.ExtractAsync(MakeRequest(ExtractionTypes.Facts));

        await _extractionStage.Received(1).ExtractAsync(
            Arg.Any<IReadOnlyList<Message>>(),
            ExtractionTypes.Facts,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_SourceMessageIdsFromStage()
    {
        _extractionStage.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<ExtractionTypes>(), Arg.Any<CancellationToken>())
            .Returns(MakeStageResult());
        _persistenceStage.PersistAsync(Arg.Any<ExtractionStageResult>(), Arg.Any<CancellationToken>())
            .Returns(MakePersistenceResult());

        var sut = CreateSut();
        var result = await sut.ExtractAsync(MakeRequest());

        result.SourceMessageIds.Should().BeEquivalentTo(new[] { "msg-1", "msg-2" });
    }
}
