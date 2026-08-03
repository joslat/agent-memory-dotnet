using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Services;

public sealed class MemoryExtractionPipelineDefaultContractTests
{
    [Fact]
    public async Task ExtractBatchAsync_LegacyImplementation_FallsBackInSourceChronology()
    {
        IMemoryExtractionPipeline pipeline = new LegacyPipeline();
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        var results = await pipeline.ExtractBatchAsync(
            [Request("late", start.AddMinutes(1)), Request("early", start)],
            maxSessionsPerBatch: 4,
            maxInputTokens: 4_096);

        ((LegacyPipeline)pipeline).ObservedSessions.Should().Equal("early", "late");
        results.Select(result => result.Metadata["sessionId"]).Should().Equal("early", "late");
    }

    private static ExtractionRequest Request(string sessionId, DateTimeOffset timestamp) =>
        new()
        {
            SessionId = sessionId,
            Messages =
            [
                new Message
                {
                    MessageId = $"{sessionId}-message",
                    ConversationId = $"{sessionId}-conversation",
                    SessionId = sessionId,
                    Role = "user",
                    Content = sessionId,
                    TimestampUtc = timestamp,
                },
            ],
        };

    private sealed class LegacyPipeline : IMemoryExtractionPipeline
    {
        public List<string> ObservedSessions { get; } = [];

        public Task<ExtractionResult> ExtractAsync(
            ExtractionRequest request,
            CancellationToken cancellationToken = default)
        {
            ObservedSessions.Add(request.SessionId);
            return Task.FromResult(new ExtractionResult
            {
                Metadata = new Dictionary<string, object> { ["sessionId"] = request.SessionId },
            });
        }
    }
}
