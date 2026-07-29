using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgentMemory.LongMemEval;

/// <summary>
/// Content-free provider-call accounting for one explicit LongMemEval purpose.
/// It records only counts, failures, and elapsed provider time.
/// </summary>
internal sealed class LongMemEvalChatCallMeter(IChatClient inner) : IChatClient
{
    private long _calls;
    private long _failures;
    private long _elapsedTimestampTicks;

    public LongMemEvalChatCallSnapshot Snapshot()
    {
        var elapsedTicks = Interlocked.Read(ref _elapsedTimestampTicks);
        return new LongMemEvalChatCallSnapshot(
            Calls: Interlocked.Read(ref _calls),
            Failures: Interlocked.Read(ref _failures),
            Duration: TimeSpan.FromSeconds(
                (double)elapsedTicks / Stopwatch.Frequency));
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await inner.GetResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Increment(ref _failures);
            throw;
        }
        finally
        {
            Interlocked.Add(
                ref _elapsedTimestampTicks,
                Stopwatch.GetTimestamp() - started);
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        var started = Stopwatch.GetTimestamp();
        try
        {
            await foreach (var update in inner
                .GetStreamingResponseAsync(messages, options, cancellationToken)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                yield return update;
            }
        }
        finally
        {
            Interlocked.Add(
                ref _elapsedTimestampTicks,
                Stopwatch.GetTimestamp() - started);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this)
            ? this
            : inner.GetService(serviceType, serviceKey);

    public void Dispose() => inner.Dispose();
}

public sealed record LongMemEvalChatCallSnapshot(
    long Calls,
    long Failures,
    TimeSpan Duration)
{
    public static LongMemEvalChatCallSnapshot Zero { get; } =
        new(0, 0, TimeSpan.Zero);
}
