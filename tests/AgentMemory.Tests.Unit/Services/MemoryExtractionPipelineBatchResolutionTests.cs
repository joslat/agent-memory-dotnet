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

public sealed class MemoryExtractionPipelineBatchResolutionTests
{
    [Fact]
    public async Task ExtractBatchAsync_PersistenceFailure_InvalidatesAndDisposesResolutionBatch()
    {
        var extractionStage = Substitute.For<IExtractionStage>();
        var persistenceStage = Substitute.For<IPersistenceStage>();
        var batchExtractor = Substitute.For<IMultiSessionUnifiedMemoryExtractor>();
        var lease = new RecordingLease();
        var request = Request();
        var extracted = new UnifiedExtractionResult
        {
            Facts = [new ExtractedFact { Subject = "s", Predicate = "p", Object = "o", Confidence = 1 }],
        };
        batchExtractor.IsEnabled.Returns(true);
        batchExtractor.ExtractAsync(
                Arg.Any<IReadOnlyList<ExtractionRequest>>(),
                1,
                1000,
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, UnifiedExtractionResult> { [request.SessionId] = extracted });
        extractionStage.BeginResolutionBatch().Returns(lease);
        extractionStage.ProcessUnifiedAsync(
                Arg.Any<IReadOnlyList<Message>>(),
                extracted,
                ExtractionTypes.All,
                Arg.Any<MemoryScope?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ExtractionStageResult
            {
                RawFacts = extracted.Facts,
                SourceMessageIds = request.Messages.Select(message => message.MessageId).ToArray(),
            });
        persistenceStage.PersistAsync(
                Arg.Any<ExtractionStageResult>(),
                Arg.Any<string?>(),
                Arg.Any<MemoryTrustLevel>(),
                Arg.Any<CancellationToken>())
            .Returns(new PersistenceResult
            {
                Outcomes =
                [
                    new IngestionItemOutcome
                    {
                        Kind = MemoryItemKind.Fact,
                        Stage = IngestionStage.Persistence,
                        Status = IngestionItemStatus.Failed,
                    },
                ],
            });
        var sut = new MemoryExtractionPipeline(
            extractionStage,
            persistenceStage,
            NullLogger<MemoryExtractionPipeline>.Instance,
            new DefaultMemoryIsolationPolicy(
                Options.Create(new MemoryIsolationOptions()),
                NullLogger<DefaultMemoryIsolationPolicy>.Instance),
            Options.Create(new ExtractionOptions()),
            [batchExtractor]);

        await sut.ExtractBatchAsync([request], 1, 1000);

        extractionStage.Received(1).BeginResolutionBatch();
        extractionStage.Received(1).InvalidateResolutionBatch();
        lease.Disposed.Should().BeTrue();
    }

    private static ExtractionRequest Request() => new()
    {
        SessionId = "session-1",
        UserId = "owner-1",
        Messages =
        [
            new Message
            {
                MessageId = "message-1",
                ConversationId = "conversation-1",
                SessionId = "session-1",
                Role = "user",
                Content = "content",
                TimestampUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            },
        ],
    };

    private sealed class RecordingLease : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
