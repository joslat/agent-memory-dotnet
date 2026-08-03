using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

public sealed class MemoryExtractionPipelineBatchTests
{
    [Fact]
    public async Task ExtractBatchAsync_OrdersBeforeExtractionAndPersistsEachKeyedResult()
    {
        var extractionStage = Substitute.For<IExtractionStage>();
        var persistenceStage = Substitute.For<IPersistenceStage>();
        var batchExtractor = Substitute.For<IMultiSessionUnifiedMemoryExtractor>();
        batchExtractor.IsEnabled.Returns(true);
        var late = Request("late", minute: 2);
        var early = Request("early", minute: 1);
        var earlyResult = new UnifiedExtractionResult
        {
            Facts = [new ExtractedFact { Subject = "early", Predicate = "p", Object = "o", Confidence = 1 }],
        };
        var lateResult = new UnifiedExtractionResult
        {
            Facts = [new ExtractedFact { Subject = "late", Predicate = "p", Object = "o", Confidence = 1 }],
        };
        batchExtractor.ExtractAsync(
                Arg.Any<IReadOnlyList<ExtractionRequest>>(),
                2,
                1000,
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, UnifiedExtractionResult>
            {
                [early.SessionId] = earlyResult,
                [late.SessionId] = lateResult,
            });
        extractionStage.ProcessUnifiedAsync(
                Arg.Any<IReadOnlyList<Message>>(),
                Arg.Any<UnifiedExtractionResult>(),
                Arg.Any<ExtractionTypes>(),
                Arg.Any<MemoryScope?>(),
                Arg.Any<CancellationToken>())
            .Returns(call => Stage(
                call.ArgAt<IReadOnlyList<Message>>(0),
                call.ArgAt<UnifiedExtractionResult>(1)));
        persistenceStage.PersistAsync(
                Arg.Any<ExtractionStageResult>(),
                Arg.Any<string?>(),
                Arg.Any<MemoryTrustLevel>(),
                Arg.Any<CancellationToken>())
            .Returns(new PersistenceResult());
        var sut = new MemoryExtractionPipeline(
            extractionStage,
            persistenceStage,
            NullLogger<MemoryExtractionPipeline>.Instance,
            new DefaultMemoryIsolationPolicy(
                Options.Create(new MemoryIsolationOptions()),
                NullLogger<DefaultMemoryIsolationPolicy>.Instance),
            Options.Create(new ExtractionOptions()),
            [batchExtractor]);

        var results = await sut.ExtractBatchAsync([late, early], 2, 1000);

        results.Select(result => result.Metadata["sessionId"])
            .Should().Equal("early", "late");
        await batchExtractor.Received(1).ExtractAsync(
            Arg.Is<IReadOnlyList<ExtractionRequest>>(items =>
                items.Select(item => item.SessionId).SequenceEqual(new[] { "early", "late" })),
            2,
            1000,
            Arg.Any<CancellationToken>());
        await extractionStage.Received(1).ProcessUnifiedAsync(
            Arg.Is<IReadOnlyList<Message>>(messages => messages.Single().SessionId == "early"),
            earlyResult,
            ExtractionTypes.All,
            Arg.Any<MemoryScope?>(),
            Arg.Any<CancellationToken>());
        await extractionStage.Received(1).ProcessUnifiedAsync(
            Arg.Is<IReadOnlyList<Message>>(messages => messages.Single().SessionId == "late"),
            lateResult,
            ExtractionTypes.All,
            Arg.Any<MemoryScope?>(),
            Arg.Any<CancellationToken>());
        await persistenceStage.Received(2).PersistAsync(
            Arg.Any<ExtractionStageResult>(),
            Arg.Any<string?>(),
            Arg.Any<MemoryTrustLevel>(),
            Arg.Any<CancellationToken>());
    }

    private static ExtractionRequest Request(string sessionId, int minute) => new()
    {
        SessionId = sessionId,
        UserId = $"{sessionId}-owner",
        Messages =
        [
            new Message
            {
                MessageId = $"{sessionId}-message",
                ConversationId = $"{sessionId}-conversation",
                SessionId = sessionId,
                Role = "user",
                Content = sessionId,
                TimestampUtc = new DateTimeOffset(2026, 1, 1, 0, minute, 0, TimeSpan.Zero),
            },
        ],
    };

    private static ExtractionStageResult Stage(
        IReadOnlyList<Message> messages,
        UnifiedExtractionResult result) => new()
    {
        RawEntities = result.Entities,
        RawFacts = result.Facts,
        RawPreferences = result.Preferences,
        RawRelationships = result.Relationships,
        SourceMessageIds = messages.Select(message => message.MessageId).ToArray(),
    };
}
