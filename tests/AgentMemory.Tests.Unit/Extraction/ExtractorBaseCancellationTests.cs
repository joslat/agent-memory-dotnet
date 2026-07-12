using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Core.Extraction;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// Round-2 fix: extraction must propagate caller cancellation instead of swallowing it and reporting a
/// "successful" empty result. Genuine (non-cancellation) failures still degrade to empty for resilience.
/// </summary>
public sealed class ExtractorBaseCancellationTests
{
    private sealed class CoreThrows : ExtractorBase<ExtractedEntity>
    {
        private readonly Exception _toThrow;
        public CoreThrows(Exception toThrow) : base(NullLogger.Instance) => _toThrow = toThrow;
        protected override Task<IReadOnlyList<ExtractedEntity>> ExtractCoreAsync(
            IReadOnlyList<Message> messages, CancellationToken cancellationToken) => throw _toThrow;
    }

    private static IReadOnlyList<Message> OneMessage() => new[]
    {
        new Message
        {
            MessageId = "m", ConversationId = "c", SessionId = "s",
            Role = "user", Content = "hi", TimestampUtc = DateTimeOffset.UtcNow
        }
    };

    [Fact]
    public async Task ExtractAsync_CallerCancelled_RethrowsOperationCanceled_NotEmpty()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var sut = new CoreThrows(new OperationCanceledException(cts.Token));

        var act = () => sut.ExtractAsync(OneMessage(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "a cancelled extraction must surface OperationCanceledException, not a 'successful' empty list");
    }

    [Fact]
    public async Task ExtractAsync_GenuineFailure_DegradesToEmpty()
    {
        var sut = new CoreThrows(new InvalidOperationException("boom"));

        var result = await sut.ExtractAsync(OneMessage(), CancellationToken.None);

        result.Should().BeEmpty("a non-cancellation failure still degrades to empty (resilience preserved)");
    }

    [Fact]
    public async Task ExtractAsync_SpuriousCancellation_WithUncancelledToken_DegradesToEmpty()
    {
        // An OperationCanceledException whose token is NOT cancellation-requested (e.g. a client-side HTTP
        // timeout) is a failure to degrade, not a caller cancellation — the `when (cancellationToken.IsCancellationRequested)`
        // filter must let it fall through to the resilient empty path.
        var sut = new CoreThrows(new OperationCanceledException());

        var result = await sut.ExtractAsync(OneMessage(), CancellationToken.None);

        result.Should().BeEmpty();
    }
}
