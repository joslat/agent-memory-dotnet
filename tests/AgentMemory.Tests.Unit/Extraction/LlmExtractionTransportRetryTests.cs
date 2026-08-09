using AgentMemory.Abstractions.Domain;
using AgentMemory.Extraction.Llm;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// A transient provider failure must not end an extraction on its first occurrence.
/// </summary>
/// <remarks>
/// <c>MaxRetries</c> was honoured only for <i>parse</i> failures: the runner re-prompted when the
/// response was unparseable JSON, but its provider call sat outside any catch, so a transport
/// exception propagated on the first attempt. Nothing else retried — the call meter counts retries
/// without performing any.
/// <para>
/// The cost was measured, twice. Two n=50 preparations — 614 provider calls each — died mid-run on a
/// single transient, at 37 and 26 minutes. At that call volume, "no transport retry" means "a long
/// measurement cannot finish".
/// </para>
/// <para>
/// The policy mirrors the batch splitter's, deliberately: a <see cref="FormatException"/> is caused
/// by the request's own shape and re-sending it unchanged cannot help, so it is not retried here —
/// the parse loop already handles it, and the splitter handles the batch-level version. Everything
/// else is treated as transient.
/// </para>
/// </remarks>
public sealed class LlmExtractionTransportRetryTests
{
    [Fact]
    public async Task ATransientTransportFailureIsRetriedAndTheExtractionSucceeds()
    {
        // The load-bearing case: one failure then success must yield a result, not an exception.
        var client = Substitute.For<IChatClient>();
        var calls = 0;
        client.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls++;
                if (calls == 1)
                    throw new HttpRequestException("503 Service Unavailable");
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, ValidJson)));
            });

        var result = await Sut(client)
            .ExtractAsync([Request("session-00")], maxSessionsPerBatch: 1, maxInputTokens: 100_000)
            .ConfigureAwait(true);

        result.Should().NotBeNull();
        calls.Should().Be(2, "the first attempt failed in transport and the second succeeded");
    }

    [Fact]
    public async Task APersistentTransportFailureStillGivesUp()
    {
        // Bounded, not infinite. A provider that is genuinely down must end the run rather than
        // retry forever inside a measurement that has a watchdog waiting on it.
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns<Task<ChatResponse>>(_ => throw new HttpRequestException("503 Service Unavailable"));

        var act = () => Sut(client).ExtractAsync(
            [Request("session-00")], maxSessionsPerBatch: 1, maxInputTokens: 100_000);

        await act.Should().ThrowAsync<HttpRequestException>().ConfigureAwait(true);
        await client.Received(3).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancellationIsNeverRetried()
    {
        // A cancelled run must stop immediately. Retrying a cancellation would make the watchdog's
        // timeout unenforceable, which is the opposite of what it is for.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync().ConfigureAwait(true);
        var client = Substitute.For<IChatClient>();

        var act = () => Sut(client).ExtractAsync(
            [Request("session-00")], maxSessionsPerBatch: 1, maxInputTokens: 100_000,
            cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(true);
        await client.DidNotReceive().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>());
    }

    // The alias, not the session id: the contract acknowledges sources as s1..sN.
    private const string ValidJson =
        """{"processed_source_sessions":["s1"],"entities":[],"facts":[],"preferences":[]}""";

    private static LlmMultiSessionUnifiedMemoryExtractor Sut(IChatClient client) =>
        new(client,
            Options.Create(new LlmExtractionOptions
            {
                UseUnifiedExtraction = true,
                UseMultiSessionBatchExtraction = true,
                MaxRetries = 2,
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
