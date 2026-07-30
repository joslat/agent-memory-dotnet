using System.Collections.Concurrent;
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
    private const int MaxFailureDetails = 32;
    private readonly ConcurrentQueue<LongMemEvalChatCallFailure> _failureDetails = new();
    private long _calls;
    private long _failures;
    private long _elapsedTimestampTicks;
    private long _failureDetailSlots;
    private long _droppedFailureDetails;

    public LongMemEvalChatCallSnapshot Snapshot()
    {
        var elapsedTicks = Interlocked.Read(ref _elapsedTimestampTicks);
        return new LongMemEvalChatCallSnapshot(
            Calls: Interlocked.Read(ref _calls),
            Failures: Interlocked.Read(ref _failures),
            Duration: TimeSpan.FromSeconds(
                (double)elapsedTicks / Stopwatch.Frequency))
        {
            FailureDetails = _failureDetails.ToArray(),
            DroppedFailureDetails = Interlocked.Read(ref _droppedFailureDetails)
        };
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var materializedMessages =
            messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();
        var purpose = ClassifyPurpose(materializedMessages);
        var callOrdinal = Interlocked.Increment(ref _calls);
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await inner.GetResponseAsync(
                    materializedMessages, options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _failures);
            RecordFailure(callOrdinal, purpose, exception);
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

    private void RecordFailure(
        long callOrdinal,
        string purpose,
        Exception exception)
    {
        var slot = Interlocked.Increment(ref _failureDetailSlots);
        if (slot > MaxFailureDetails)
        {
            Interlocked.Increment(ref _droppedFailureDetails);
            return;
        }

        _failureDetails.Enqueue(new LongMemEvalChatCallFailure(
            callOrdinal,
            purpose,
            exception.GetType().FullName ?? exception.GetType().Name,
            ProviderStatus(exception)));
    }

    private static int? ProviderStatus(Exception exception) =>
        exception switch
        {
            Azure.RequestFailedException requestFailed => requestFailed.Status,
            System.ClientModel.ClientResultException clientResult =>
                clientResult.Status,
            HttpRequestException { StatusCode: not null } http =>
                (int)http.StatusCode.Value,
            _ => null
        };

    private static string ClassifyPurpose(
        IReadOnlyList<ChatMessage> messages)
    {
        var systemPrompt = messages
            .FirstOrDefault(message => message.Role == ChatRole.System)
            ?.Text;
        if (systemPrompt is null)
            return "other";
        if (systemPrompt.StartsWith(
                "You are an entity extraction assistant.",
                StringComparison.Ordinal))
            return "entity";
        if (systemPrompt.StartsWith(
                "You are a fact extraction assistant.",
                StringComparison.Ordinal))
            return "fact";
        if (systemPrompt.StartsWith(
                "You are a preference extraction assistant.",
                StringComparison.Ordinal))
            return "preference";
        if (systemPrompt.StartsWith(
                "You are a relationship extraction assistant.",
                StringComparison.Ordinal))
            return "relationship";
        return "other";
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
    public IReadOnlyList<LongMemEvalChatCallFailure> FailureDetails { get; init; } =
        Array.Empty<LongMemEvalChatCallFailure>();

    public long DroppedFailureDetails { get; init; }

    public static LongMemEvalChatCallSnapshot Zero { get; } =
        new(0, 0, TimeSpan.Zero);
}

public sealed record LongMemEvalChatCallFailure(
    long CallOrdinal,
    string Purpose,
    string ExceptionType,
    int? ProviderStatus);
