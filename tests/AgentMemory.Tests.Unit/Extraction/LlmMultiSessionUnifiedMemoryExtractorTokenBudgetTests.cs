using AgentMemory.Abstractions.Domain;
using AgentMemory.Extraction.Llm;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Extraction;

public sealed class LlmMultiSessionUnifiedMemoryExtractorTokenBudgetTests
{
    [Fact]
    public async Task ExtractAsync_ConservativeBudgetRejectsBeforeProviderCall()
    {
        var client = Substitute.For<IChatClient>();
        var sut = new LlmMultiSessionUnifiedMemoryExtractor(
            client,
            Options.Create(new LlmExtractionOptions
            {
                UseUnifiedExtraction = true,
                UseMultiSessionBatchExtraction = true,
                MaxRetries = 0,
            }),
            NullLogger<LlmMultiSessionUnifiedMemoryExtractor>.Instance);

        var request = new ExtractionRequest
        {
            SessionId = "session-00",
            Messages =
            [
                new Message
                {
                    MessageId = "message-00",
                    ConversationId = "conversation-00",
                    SessionId = "session-00",
                    Role = "user",
                    Content = "Person 00 works at Company 00 and prefers tea.",
                    TimestampUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                },
            ],
        };

        var act = () => sut.ExtractAsync([request], maxSessionsPerBatch: 1, maxInputTokens: 500);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*exceeds*token budget*");
        await client.DidNotReceive().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>());
    }
}
