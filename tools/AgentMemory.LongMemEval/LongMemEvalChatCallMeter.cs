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
    private const int MaxCallDetails = 64;
    private readonly ConcurrentQueue<LongMemEvalChatCallFailure> _failureDetails = new();
    private readonly ConcurrentQueue<LongMemEvalChatCallDetail> _callDetails = new();
    private readonly ConcurrentDictionary<string, ScopeCounter> _scopeCounters = new(StringComparer.Ordinal);
    private readonly AsyncLocal<string?> _currentScope = new();
    private long _calls;
    private long _failures;
    private long _elapsedTimestampTicks;
    private long _failureDetailSlots;
    private long _droppedFailureDetails;
    private long _droppedCallDetails;
    internal IDisposable BeginScope(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        var previous = _currentScope.Value;
        _currentScope.Value = scope;
        _scopeCounters.GetOrAdd(scope, static _ => new ScopeCounter());
        return new ScopeLease(this, previous);
    }

    internal LongMemEvalChatCallScopeSnapshot SnapshotScope(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return _scopeCounters.TryGetValue(scope, out var counter)
            ? counter.Snapshot()
            : LongMemEvalChatCallScopeSnapshot.Zero;
    }


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
            DroppedFailureDetails = Interlocked.Read(ref _droppedFailureDetails),
            CallDetails = _callDetails.OrderBy(detail => detail.CallOrdinal).ToArray(),
            DroppedCallDetails = Interlocked.Read(ref _droppedCallDetails)
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
        var scopeCounter = CurrentScopeCounter();
        scopeCounter?.RecordCall(purpose);
        Exception? failure = null;
        try
        {
            return await inner.GetResponseAsync(
                    materializedMessages, options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
            Interlocked.Increment(ref _failures);
            scopeCounter?.RecordFailure();
            RecordFailure(callOrdinal, purpose, exception);
            throw;
        }
        finally
        {
            var elapsed = Stopwatch.GetTimestamp() - started;
            Interlocked.Add(
                ref _elapsedTimestampTicks,
                elapsed);
            scopeCounter?.RecordDuration(elapsed);
            RecordCall(callOrdinal, purpose, failure);
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        var started = Stopwatch.GetTimestamp();
        var scopeCounter = CurrentScopeCounter();
        scopeCounter?.RecordCall("streaming");
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
            var elapsed = Stopwatch.GetTimestamp() - started;
            Interlocked.Add(
                ref _elapsedTimestampTicks,
                elapsed);
            scopeCounter?.RecordDuration(elapsed);
        }
    }

    private ScopeCounter? CurrentScopeCounter() =>
        _currentScope.Value is { } scope
            ? _scopeCounters.GetOrAdd(scope, static _ => new ScopeCounter())
            : null;

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

    private void RecordCall(
        long callOrdinal,
        string purpose,
        Exception? exception)
    {
        _callDetails.Enqueue(new LongMemEvalChatCallDetail(
            callOrdinal,
            purpose,
            exception?.GetType().FullName ?? exception?.GetType().Name,
            exception is null ? null : ProviderStatus(exception)));
        while (_callDetails.Count > MaxCallDetails &&
               _callDetails.TryDequeue(out _))
        {
            Interlocked.Increment(ref _droppedCallDetails);
        }
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
        if (systemPrompt.StartsWith(
                "You extract structured long-term memory from multiple independent source sessions.",
                StringComparison.Ordinal))
            return "unified_batch";
        if (systemPrompt.StartsWith(
                "You extract structured long-term memory from a conversation.",
                StringComparison.Ordinal))
            return "unified";
        return "other";
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this)
            ? this
            : inner.GetService(serviceType, serviceKey);

    public void Dispose() => inner.Dispose();


    private sealed class ScopeLease(LongMemEvalChatCallMeter owner, string? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner._currentScope.Value = previous;
        }
    }

    private sealed class ScopeCounter
    {
        private readonly ConcurrentDictionary<string, long> _purposes = new(StringComparer.Ordinal);
        private long _calls;
        private long _failures;
        private long _elapsedTimestampTicks;

        internal void RecordCall(string purpose)
        {
            Interlocked.Increment(ref _calls);
            _purposes.AddOrUpdate(purpose, 1, static (_, count) => count + 1);
        }

        internal void RecordFailure() => Interlocked.Increment(ref _failures);

        internal void RecordDuration(long timestampTicks) =>
            Interlocked.Add(ref _elapsedTimestampTicks, timestampTicks);

        internal LongMemEvalChatCallScopeSnapshot Snapshot() =>
            new(
                Interlocked.Read(ref _calls),
                Interlocked.Read(ref _failures),
                TimeSpan.FromSeconds(
                    (double)Interlocked.Read(ref _elapsedTimestampTicks) /
                    Stopwatch.Frequency),
                _purposes.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal));
    }
}
public sealed record LongMemEvalChatCallSnapshot(
    long Calls,
    long Failures,
    TimeSpan Duration)
{
    public IReadOnlyList<LongMemEvalChatCallFailure> FailureDetails { get; init; } =
        Array.Empty<LongMemEvalChatCallFailure>();


    public long DroppedFailureDetails { get; init; }

    public IReadOnlyList<LongMemEvalChatCallDetail> CallDetails { get; init; } =
        Array.Empty<LongMemEvalChatCallDetail>();

    public long DroppedCallDetails { get; init; }

    public static LongMemEvalChatCallSnapshot Zero { get; } =
        new(0, 0, TimeSpan.Zero);
}

internal sealed record LongMemEvalChatCallScopeSnapshot(
    long Calls,
    long Failures,
    TimeSpan Duration,
    IReadOnlyDictionary<string, long> Purposes)
{
    internal static LongMemEvalChatCallScopeSnapshot Zero { get; } =
        new(0, 0, TimeSpan.Zero, new Dictionary<string, long>(StringComparer.Ordinal));
}

public sealed record LongMemEvalChatCallFailure(
    long CallOrdinal,
    string Purpose,
    string ExceptionType,
    int? ProviderStatus);

public sealed record LongMemEvalChatCallDetail(
    long CallOrdinal,
    string Purpose,
    string? ExceptionType,
    int? ProviderStatus);
