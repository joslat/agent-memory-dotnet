using AgentMemory.Abstractions.Domain;
using AgentMemory.Extraction.Llm;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// What a multi-session batch may be split for, and what it may not.
/// </summary>
/// <remarks>
/// Splitting is a recovery for a batch that is <i>itself</i> the problem — too many input tokens, an
/// incomplete acknowledgement, an unusable source-session key, an unparseable response. All of those
/// arrive as <see cref="FormatException"/>, and halving the batch is a genuine remedy for each.
/// <para>
/// A provider transport failure is not that. Halving the batch and re-sending puts the same request
/// shape at the same endpoint that just failed, so a split neither diagnoses nor fixes it — and it
/// doubles the call count, which breaks the strict per-question call accounting the prepared-pair
/// harness relies on to certify a sealed graph.
/// </para>
/// <para>
/// This is not hypothetical: an n=50 preparation ran 37 minutes and then aborted at question 20 with
/// "observed 14 calls ... expected exactly 12", caused by one
/// <c>System.ClientModel.ClientResultException</c> classified as split reason <c>other</c>. Transport
/// failures belong to the configured retry policy, not to the splitter.
/// </para>
/// </remarks>
public sealed class LlmMultiSessionBatchSplitPolicyTests
{
    [Fact]
    public async Task AProviderTransportFailureIsNotTreatedAsAnOversizedBatch()
    {
        // The load-bearing case. Two sessions, one transport failure: the exception must reach the
        // caller rather than being answered with a split.
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns<Task<ChatResponse>>(_ => throw new HttpRequestException("429 Too Many Requests"));

        var act = () => Sut(client).ExtractAsync(
            [Request("session-00"), Request("session-01")], maxSessionsPerBatch: 2, maxInputTokens: 100_000);

        await act.Should().ThrowAsync<HttpRequestException>().ConfigureAwait(true);

        // Exactly one attempt at the batch. A split would have re-sent each half.
        await client.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnUnparseableResponseStillSplits()
    {
        // The control: batch-shape failures arrive as FormatException and splitting genuinely helps,
        // so this behaviour must survive the fix. Two halves are attempted after the whole fails.
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "not json"))));

        var act = () => Sut(client).ExtractAsync(
            [Request("session-00"), Request("session-01")], maxSessionsPerBatch: 2, maxInputTokens: 100_000);

        await act.Should().ThrowAsync<Exception>().ConfigureAwait(true);

        // More than the single whole-batch attempt: the splitter tried a half too. The exact count
        // is deliberately not asserted - it is an implementation detail of how far the recursion
        // gets before the halves fail as well. That it splits at all is the property.
        client.ReceivedCalls()
            .Count(call => call.GetMethodInfo().Name == nameof(IChatClient.GetResponseAsync))
            .Should().BeGreaterThan(1);
    }

    private static LlmMultiSessionUnifiedMemoryExtractor Sut(IChatClient client) =>
        new(client,
            Options.Create(new LlmExtractionOptions
            {
                UseUnifiedExtraction = true,
                UseMultiSessionBatchExtraction = true,
                MaxRetries = 0,
            }),
            NullLogger<LlmMultiSessionUnifiedMemoryExtractor>.Instance);

    private static ExtractionRequest Request(string sessionId) => new()
    {
        SessionId = sessionId,
        Messages =
        [
            new Message
            {
                MessageId = $"message-{sessionId}",
                ConversationId = "conversation-00",
                SessionId = sessionId,
                Role = "user",
                Content = "Person works at a company and prefers tea.",
                TimestampUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            },
        ],
    };
}
