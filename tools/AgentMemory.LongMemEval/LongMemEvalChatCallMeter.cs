using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
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
    private readonly ConditionalWeakTable<Activity, ActivityCallCounter> _activityCalls = new();
    private long _completedCalls;
    private long _retryCalls;
    private int _activeCalls;
    private int _maximumConcurrency;
    private long _failures;
    private long _elapsedTimestampTicks;
    private long _failureDetailSlots;
    private long _droppedFailureDetails;
    private long _droppedCallDetails;

    /// <summary>
    /// Backend build ids observed on responses, and how many calls each one served.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A model pinned by name is not pinned by build.</b> This deployment rejects
    /// <c>temperature: 0</c>, which is why extraction here is nondeterministic and why three cold builds
    /// of an identical configuration shared 7.5% of their triples. The provider only offers determinism
    /// while its backend build is unchanged — so the build id is the one datum that can tell a reader
    /// that two runs were never comparable in the first place, rather than leaving every difference
    /// attributable to the change under test.
    /// </para>
    /// <para>
    /// More than one distinct value in a <i>single</i> run is the sharper finding: the run itself
    /// straddled a backend change, so even its internal arm-to-arm comparison is suspect.
    /// </para>
    /// </remarks>
    private readonly ConcurrentDictionary<string, long> _providerBuilds = new(StringComparer.Ordinal);

    private long _callsWithoutProviderBuild;
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
            CompletedCalls = Interlocked.Read(ref _completedCalls),
            RetryCalls = Interlocked.Read(ref _retryCalls),
            MaximumConcurrency = Volatile.Read(ref _maximumConcurrency),
            FailureDetails = _failureDetails.ToArray(),
            DroppedFailureDetails = Interlocked.Read(ref _droppedFailureDetails),
            CallDetails = _callDetails.OrderBy(detail => detail.CallOrdinal).ToArray(),
            DroppedCallDetails = Interlocked.Read(ref _droppedCallDetails),
            ProviderBuilds = _providerBuilds
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            CallsWithoutProviderBuild = Interlocked.Read(ref _callsWithoutProviderBuild)
        };
    }

    /// <summary>
    /// Records the backend build a response came from, when the provider reported one.
    /// </summary>
    /// <remarks>
    /// Absence is counted, never substituted. "The provider did not report a build" and "the build was X"
    /// are different facts, and a sentinel would let a report claim a comparability it cannot support.
    /// </remarks>
    private void RecordProviderBuild(ChatResponse response)
    {
        var build = ProviderBuildId.FromChatResponse(response);
        if (string.IsNullOrEmpty(build))
        {
            Interlocked.Increment(ref _callsWithoutProviderBuild);
            return;
        }

        _providerBuilds.AddOrUpdate(build, 1, static (_, count) => count + 1);
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var materializedMessages =
            messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();
        var purpose = ClassifyPurpose(materializedMessages);
        var activity = Activity.Current;
        var estimatedInputTokens = EstimatedInputTokens(activity) ??
            EstimateInputTokens(materializedMessages, purpose);
        var retry = RecordActivityCall(activity, purpose) || IsParseRetry(materializedMessages, purpose);
        if (retry)
            Interlocked.Increment(ref _retryCalls);
        var nowActive = Interlocked.Increment(ref _activeCalls);
        UpdateMaximum(ref _maximumConcurrency, nowActive);
        var callOrdinal = Interlocked.Increment(ref _calls);
        var started = Stopwatch.GetTimestamp();
        var scopeCounter = CurrentScopeCounter();
        scopeCounter?.RecordCall(purpose, retry);
        Exception? failure = null;
        try
        {
            var response = await inner.GetResponseAsync(
                    materializedMessages, options, cancellationToken)
                .ConfigureAwait(false);
            RecordProviderBuild(response);
            return response;
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
            Interlocked.Increment(ref _completedCalls);
            Interlocked.Decrement(ref _activeCalls);
            scopeCounter?.RecordCompleted(elapsed);
            RecordCall(callOrdinal, purpose, failure, elapsed, estimatedInputTokens, retry);
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        var nowActive = Interlocked.Increment(ref _activeCalls);
        UpdateMaximum(ref _maximumConcurrency, nowActive);
        var started = Stopwatch.GetTimestamp();
        var scopeCounter = CurrentScopeCounter();
        scopeCounter?.RecordCall("streaming", retry: false);
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
            Interlocked.Increment(ref _completedCalls);
            Interlocked.Decrement(ref _activeCalls);
            scopeCounter?.RecordCompleted(elapsed);
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
        Exception? exception,
        long elapsedTimestampTicks,
        int? estimatedInputTokens,
        bool retry)
    {
        _callDetails.Enqueue(new LongMemEvalChatCallDetail(
            callOrdinal,
            purpose,
            exception?.GetType().FullName ?? exception?.GetType().Name,
            exception is null ? null : ProviderStatus(exception),
            1_000d * elapsedTimestampTicks / Stopwatch.Frequency,
            estimatedInputTokens,
            retry));
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

    private bool RecordActivityCall(Activity? activity, string purpose)
    {
        if (activity is null ||
            !string.Equals(purpose, "unified_batch", StringComparison.Ordinal))
            return false;
        var counter = _activityCalls.GetValue(
            activity,
            static _ => new ActivityCallCounter());
        return Interlocked.Increment(ref counter.Calls) > 1;
    }

    private static int? EstimatedInputTokens(Activity? activity) =>
        activity?.GetTagItem("memory.extract.estimated_input_tokens") switch
        {
            int value => value,
            long value when value is >= 0 and <= int.MaxValue => (int)value,
            _ => null
        };

    private static int? EstimateInputTokens(
        IReadOnlyList<ChatMessage> messages,
        string purpose)
    {
        if (!string.Equals(purpose, "unified_batch", StringComparison.Ordinal))
            return null;
        return checked(
            messages.Sum(message => Encoding.UTF8.GetByteCount(message.Text ?? string.Empty)) +
            33);
    }

    private static bool IsParseRetry(
        IReadOnlyList<ChatMessage> messages,
        string purpose) =>
        string.Equals(purpose, "unified_batch", StringComparison.Ordinal) &&
        messages.Count >= 4 &&
        messages[^1].Role == ChatRole.User &&
        string.Equals(
            messages[^1].Text,
            "That response was not valid JSON. Reply with ONLY the JSON object — " +
            "no markdown fences, no prose.",
            StringComparison.Ordinal);

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        var observed = Volatile.Read(ref maximum);
        while (candidate > observed)
        {
            var previous = Interlocked.CompareExchange(ref maximum, candidate, observed);
            if (previous == observed)
                return;
            observed = previous;
        }
    }

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

    private sealed class ActivityCallCounter
    {
        internal long Calls;
    }

    private sealed class ScopeCounter
    {
        private readonly ConcurrentDictionary<string, long> _purposes = new(StringComparer.Ordinal);
        private long _calls;
        private long _failures;
        private long _elapsedTimestampTicks;
        private long _completedCalls;
        private long _retryCalls;
        private int _activeCalls;
        private int _maximumConcurrency;

        internal void RecordCall(string purpose, bool retry)
        {
            Interlocked.Increment(ref _calls);
            if (retry)
                Interlocked.Increment(ref _retryCalls);
            var nowActive = Interlocked.Increment(ref _activeCalls);
            UpdateMaximum(ref _maximumConcurrency, nowActive);
            _purposes.AddOrUpdate(purpose, 1, static (_, count) => count + 1);
        }

        internal void RecordFailure() => Interlocked.Increment(ref _failures);

        internal void RecordCompleted(long timestampTicks)
        {
            Interlocked.Add(ref _elapsedTimestampTicks, timestampTicks);
            Interlocked.Increment(ref _completedCalls);
            Interlocked.Decrement(ref _activeCalls);

        }
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
                    StringComparer.Ordinal))
            {
                CompletedCalls = Interlocked.Read(ref _completedCalls),
                RetryCalls = Interlocked.Read(ref _retryCalls),
                MaximumConcurrency = Volatile.Read(ref _maximumConcurrency)
            };
    }
}
public sealed record LongMemEvalChatCallSnapshot(
    long Calls,
    long Failures,
    TimeSpan Duration)
{
    public long CompletedCalls { get; init; }

    public long RetryCalls { get; init; }

    public int MaximumConcurrency { get; init; }

    public IReadOnlyList<LongMemEvalChatCallFailure> FailureDetails { get; init; } =
        Array.Empty<LongMemEvalChatCallFailure>();


    public long DroppedFailureDetails { get; init; }

    public IReadOnlyList<LongMemEvalChatCallDetail> CallDetails { get; init; } =
        Array.Empty<LongMemEvalChatCallDetail>();

    public long DroppedCallDetails { get; init; }

    /// <summary>Backend build ids the provider reported, and the calls each served.</summary>
    public IReadOnlyDictionary<string, long> ProviderBuilds { get; init; } =
        new Dictionary<string, long>(StringComparer.Ordinal);

    /// <summary>Calls whose response carried no build id. Counted, never substituted.</summary>
    public long CallsWithoutProviderBuild { get; init; }

    /// <summary>
    /// Whether this run straddled a backend build change, which makes even its own internal
    /// comparisons suspect.
    /// </summary>
    public bool ProviderBuildChangedDuringRun => ProviderBuilds.Count > 1;

    public static LongMemEvalChatCallSnapshot Zero { get; } =
        new(0, 0, TimeSpan.Zero);
}

internal sealed record LongMemEvalChatCallScopeSnapshot(
    long Calls,
    long Failures,
    TimeSpan Duration,
    IReadOnlyDictionary<string, long> Purposes)
{
    internal long CompletedCalls { get; init; }

    internal long RetryCalls { get; init; }

    internal int MaximumConcurrency { get; init; }

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
    int? ProviderStatus,
    double DurationMilliseconds,
    int? EstimatedInputTokens,
    bool Retry);
