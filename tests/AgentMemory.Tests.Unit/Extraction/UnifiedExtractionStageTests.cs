using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Resolution;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AgentMemory.Tests.Unit.Extraction;

public sealed class UnifiedExtractionStageTests
{
    private static readonly IReadOnlyList<Message> Messages =
    [
        new Message
        {
            MessageId = "message-1",
            ConversationId = "conversation-1",
            SessionId = "session-1",
            Role = "user",
            Content = "Alice knows Bob and prefers tea.",
            TimestampUtc = DateTimeOffset.UtcNow,
        },
    ];

    [Fact]
    public async Task EnabledUnifiedExtractor_ReplacesAllCategoryExtractors()
    {
        var entity = Substitute.For<IEntityExtractor>();
        var fact = Substitute.For<IFactExtractor>();
        var preference = Substitute.For<IPreferenceExtractor>();
        var relationship = Substitute.For<IRelationshipExtractor>();
        var unified = Substitute.For<IUnifiedMemoryExtractor>();
        unified.IsEnabled.Returns(true);
        unified.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(CompleteResult());

        var sut = CreateSut(
            unified,
            entityExtractors: [entity],
            factExtractors: [fact],
            preferenceExtractors: [preference],
            relationshipExtractors: [relationship]);

        var result = await sut.ExtractAsync(Messages, ExtractionTypes.All);

        await unified.Received(1).ExtractAsync(Messages, Arg.Any<CancellationToken>());
        await entity.DidNotReceive().ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>());
        await fact.DidNotReceive().ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>());
        await preference.DidNotReceive().ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>());
        await relationship.DidNotReceive().ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>());
        result.RawEntities.Should().HaveCount(2);
        result.RawFacts.Should().HaveCount(2);
        result.RawPreferences.Should().ContainSingle();
        result.RawRelationships.Should().ContainSingle();
        result.FilteredRelationships.Should().ContainSingle();
    }

    [Fact]
    public async Task DisabledUnifiedExtractor_PreservesCategoryPath()
    {
        var entity = Substitute.For<IEntityExtractor>();
        entity.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns([Entity("Alice")]);
        var unified = Substitute.For<IUnifiedMemoryExtractor>();
        unified.IsEnabled.Returns(false);
        var sut = CreateSut(unified, entityExtractors: [entity]);

        var result = await sut.ExtractAsync(Messages, ExtractionTypes.Entities);

        await unified.DidNotReceive().ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>());
        await entity.Received(1).ExtractAsync(Messages, Arg.Any<CancellationToken>());
        result.RawEntities.Should().ContainSingle();
    }

    [Fact]
    public async Task UnifiedExtractor_RespectsRequestedTypes()
    {
        var unified = Substitute.For<IUnifiedMemoryExtractor>();
        unified.IsEnabled.Returns(true);
        unified.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(CompleteResult());
        var sut = CreateSut(unified);

        var result = await sut.ExtractAsync(Messages, ExtractionTypes.Facts);

        result.RawEntities.Should().BeEmpty();
        result.RawFacts.Should().HaveCount(2);
        result.RawPreferences.Should().BeEmpty();
        result.RawRelationships.Should().BeEmpty();
    }

    [Fact]
    public async Task UnifiedFailure_BestEffortRecordsEveryRequestedCategory()
    {
        var unified = Substitute.For<IUnifiedMemoryExtractor>();
        unified.IsEnabled.Returns(true);
        unified.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new FormatException("invalid unified response"));
        var sut = CreateSut(unified);

        var result = await sut.ExtractAsync(Messages, ExtractionTypes.All);

        result.Outcomes.Should().HaveCount(4);
        result.Outcomes.Should().OnlyContain(outcome =>
            outcome.Stage == IngestionStage.Extraction &&
            outcome.Status == IngestionItemStatus.Failed &&
            outcome.Retryable);
        result.Outcomes.Should().ContainSingle(outcome => outcome.Kind == MemoryItemKind.Entity);
        result.Outcomes.Should().ContainSingle(outcome => outcome.Kind == MemoryItemKind.Fact);
        result.Outcomes.Should().ContainSingle(outcome => outcome.Kind == MemoryItemKind.Preference);
        result.Outcomes.Should().ContainSingle(outcome => outcome.Kind == MemoryItemKind.Relationship);
    }

    [Fact]
    public async Task UnifiedFailure_FailFastCarriesAllRequestedOutcomes()
    {
        var unified = Substitute.For<IUnifiedMemoryExtractor>();
        unified.IsEnabled.Returns(true);
        unified.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("provider unavailable"));
        var sut = CreateSut(unified, new ExtractionOptions { FailureMode = IngestionFailureMode.FailFast });

        var act = () => sut.ExtractAsync(Messages, ExtractionTypes.All);

        var exception = await act.Should().ThrowAsync<MemoryIngestionException>();
        exception.Which.CompletedOutcomes.Should().HaveCount(4);
    }

    private static ExtractionStage CreateSut(
        IUnifiedMemoryExtractor unified,
        ExtractionOptions? options = null,
        IEnumerable<IEntityExtractor>? entityExtractors = null,
        IEnumerable<IFactExtractor>? factExtractors = null,
        IEnumerable<IPreferenceExtractor>? preferenceExtractors = null,
        IEnumerable<IRelationshipExtractor>? relationshipExtractors = null)
    {
        var resolver = Substitute.For<IEntityResolver>();
        resolver.ResolveEntityAsync(
                Arg.Any<ExtractedEntity>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<MemoryScope?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var extracted = call.Arg<ExtractedEntity>();
                return new Entity
                {
                    EntityId = $"entity-{extracted.Name.ToLowerInvariant()}",
                    Name = extracted.Name,
                    Type = extracted.Type,
                    Confidence = extracted.Confidence,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                };
            });

        return new ExtractionStage(
            entityExtractors ?? [],
            factExtractors ?? [],
            preferenceExtractors ?? [],
            relationshipExtractors ?? [],
            [unified],
            resolver,
            Options.Create(options ?? new ExtractionOptions()),
            NullLogger<ExtractionStage>.Instance);
    }

    private static UnifiedExtractionResult CompleteResult() =>
        new()
        {
            Entities = [Entity("Alice"), Entity("Bob")],
            Facts =
            [
                new ExtractedFact { Subject = "Alice", Predicate = "knows", Object = "Bob", Confidence = 0.9 },
                new ExtractedFact { Subject = "Alice", Predicate = "likes", Object = "tea", Confidence = 0.9 },
            ],
            Preferences =
            [
                new ExtractedPreference { Category = "drink", PreferenceText = "tea", Confidence = 0.9 },
            ],
            Relationships =
            [
                new ExtractedRelationship
                {
                    SourceEntity = "Alice",
                    TargetEntity = "Bob",
                    RelationshipType = "KNOWS",
                    Confidence = 0.9,
                },
            ],
        };

    private static ExtractedEntity Entity(string name) =>
        new()
        {
            Name = name,
            Type = "PERSON",
            Confidence = 0.95,
        };
}
