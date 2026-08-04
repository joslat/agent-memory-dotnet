using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Resolution;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Extraction;

public sealed class ExtractionStageDeferredResolutionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BestEffort_UsesDeferredResolutionOnlyWhenCoalescingEnabled(bool enabled)
    {
        var extracted = new ExtractedEntity
        {
            Name = "Alice",
            Type = "Person",
            Confidence = 0.99,
        };
        var entity = new Entity
        {
            EntityId = "entity-1",
            Name = extracted.Name,
            Type = extracted.Type,
            Confidence = extracted.Confidence,
            CreatedAtUtc = DateTimeOffset.Parse("2026-08-04T12:00:00Z"),
        };
        var extractor = Substitute.For<IEntityExtractor>();
        extractor
            .ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns([extracted]);
        var resolver = Substitute.For<IEntityResolver, IExtractionEntityResolver>();
        resolver
            .ResolveEntityAsync(
                Arg.Any<ExtractedEntity>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<MemoryScope?>(),
                Arg.Any<CancellationToken>())
            .Returns(entity);
        ((IExtractionEntityResolver)resolver)
            .ResolveForPersistenceAsync(
                Arg.Any<ExtractedEntity>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<MemoryScope?>(),
                Arg.Any<CancellationToken>())
            .Returns(entity);
        var stage = new ExtractionStage(
            [extractor],
            [],
            [],
            [],
            [],
            resolver,
            Options.Create(new ExtractionOptions
            {
                FailureMode = IngestionFailureMode.BestEffort,
                UseCoalescedPersistenceTransactions = enabled,
            }),
            NullLogger<ExtractionStage>.Instance);
        var messages = new[]
        {
            new Message
            {
                MessageId = "message-1",
                SessionId = "session-1",
                ConversationId = "conversation-1",
                Role = "user",
                Content = "Alice joined the team.",
                TimestampUtc = DateTimeOffset.Parse("2026-08-04T12:00:00Z"),
            },
        };

        var result = await stage.ExtractAsync(messages, ExtractionTypes.Entities);

        result.ResolvedEntityMap.Should().ContainKey("Alice");
        if (enabled)
        {
            await ((IExtractionEntityResolver)resolver).Received(1)
                .ResolveForPersistenceAsync(
                    extracted, Arg.Any<IReadOnlyList<string>>(), null, Arg.Any<CancellationToken>());
            await resolver.DidNotReceive()
                .ResolveEntityAsync(
                    Arg.Any<ExtractedEntity>(), Arg.Any<IReadOnlyList<string>>(),
                    Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        }
        else
        {
            await resolver.Received(1)
                .ResolveEntityAsync(
                    extracted, Arg.Any<IReadOnlyList<string>>(), null, Arg.Any<CancellationToken>());
            await ((IExtractionEntityResolver)resolver).DidNotReceive()
                .ResolveForPersistenceAsync(
                    Arg.Any<ExtractedEntity>(), Arg.Any<IReadOnlyList<string>>(),
                    Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        }
    }
}
