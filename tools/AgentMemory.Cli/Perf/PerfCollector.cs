using System.Diagnostics;
using AgentMemory.Abstractions.Diagnostics;

namespace AgentMemory.Cli.Perf;

/// <summary>
/// Listens to the product's <see cref="AgentMemoryDiagnostics.Source"/> and folds every span into the
/// <see cref="TurnRecord"/> for the iteration currently in flight.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole harness-side instrumentation story. The product emits spans on the path every
/// consumer executes; the harness only listens. That is deliberate: a benchmark that installs its own
/// decorator stack measures an object graph nobody runs in production, and every conclusion drawn from
/// it carries an asterisk that cannot be checked.
/// </para>
/// <para>
/// The current turn is held in an <see cref="AsyncLocal{T}"/> so concurrent scenarios attribute to the
/// right record once concurrency scenarios exist. Spans raised by recall's parallel fan-out are stopped
/// inside the turn's execution context, so they see the correct value.
/// </para>
/// </remarks>
public sealed class PerfCollector : IDisposable
{
    private static readonly AsyncLocal<TurnRecord?> CurrentTurn = new();

    /// <summary>Recall categories reported individually, so a silently-empty one is visible.</summary>
    public static readonly string[] RecallCategories =
        ["recent", "relevant", "entities", "facts", "preferences", "traces"];

    /// <summary>Memory kinds the ingestion phase persists, reported individually for the same reason.</summary>
    public static readonly string[] PersistedKinds =
        ["entities", "facts", "preferences", "relationships"];

    private readonly ActivityListener _listener;
    private readonly List<TurnRecord> _records = new();
    private readonly TraceLogWriter? _trace;
    private readonly object _gate = new();

    public PerfCollector(TraceLogWriter? trace = null)
    {
        _trace = trace;
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentMemoryDiagnostics.SourceName,
            // AllDataAndRecorded: we need tags (db.mode) and durations, not just the fact of a span.
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = OnActivityStopped,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>The iteration currently being measured, or null outside a turn.</summary>
    public static TurnRecord? Current => CurrentTurn.Value;

    /// <summary>Every completed iteration, warm-up rows included (they carry <c>Phase == "warmup"</c>).</summary>
    public IReadOnlyList<TurnRecord> Records
    {
        get { lock (_gate) return _records.ToList(); }
    }

    /// <summary>
    /// Opens a measurement scope. Dispose ends it, stamps the wall-clock duration, and files the record.
    /// The scope exposes its <see cref="TurnScope.Record"/> so callers need not reach through
    /// <see cref="Current"/> and assert non-null.
    /// </summary>
    public TurnScope BeginTurn(string scenario, int iteration, string phase)
    {
        var record = new TurnRecord(scenario, iteration, phase);
        CurrentTurn.Value = record;
        _trace?.TurnStart(record);
        return new TurnScope(this, record);
    }

    // DRT-5b, T-10: the read notes a recall queues are written by the queue's own task, under this span. That task's
    // async context is whatever was current when the queue started, so its work is never charged to the current turn:
    // it goes to the turn whose notes are being flushed (BackgroundFor), as background.*, or nowhere.
    private const string BackgroundSpan = "memory.background.access_tracking";
    private TurnRecord? _backgroundTarget;

    /// <summary>
    /// DRT-5b, T-10: while the returned scope is open, the queued background writes (the read notes) count into
    /// <paramref name="record"/> as <c>background.*</c>, never into a measured turn's own counters.
    /// </summary>
    public IDisposable BackgroundFor(TurnRecord record)
    {
        Volatile.Write(ref _backgroundTarget, record);
        return new BackgroundScope(this);
    }

    private sealed class BackgroundScope(PerfCollector owner) : IDisposable
    {
        public void Dispose() => Volatile.Write(ref owner._backgroundTarget, null);
    }

    // Any memory.background.* span: the queued read notes, and (PERF-R07-ORDER) the owner counts refreshed after 30 s.
    private static bool UnderBackgroundSpan(Activity activity)
    {
        for (var current = activity; current is not null; current = current.Parent)
            if (current.OperationName.StartsWith("memory.background.", StringComparison.Ordinal)) return true;
        return false;
    }

    private void OnBackgroundActivityStopped(Activity activity)
    {
        if (Volatile.Read(ref _backgroundTarget) is not { } target) return;
        switch (activity.OperationName)
        {
            case BackgroundSpan:
                target.Add("background.access_tracking.writes");
                if (activity.GetTagItem("memory.background.items") is int items) target.Add("background.access_tracking.items", items);
                if (activity.GetTagItem("memory.background.recalls") is int recalls) target.Add("background.access_tracking.recalls", recalls);
                break;
            case "memory.background.owner_counts":
                target.Add("background.owner_counts.refreshes");
                break;
            case "memory.db.tx":
                target.Add(activity.GetTagItem("db.mode") as string == "write" ? "background.neo4j.tx.write" : "background.neo4j.tx.read");
                break;
            case "memory.db.query":
                target.Add("background.neo4j.queries");
                break;
        }
    }

    private void OnActivityStopped(Activity activity)
    {
        if (UnderBackgroundSpan(activity))
        {
            OnBackgroundActivityStopped(activity);
            return;
        }
        var turn = CurrentTurn.Value;
        if (turn is null) return;

        turn.RecordSpan(activity.OperationName, activity.Duration.TotalMilliseconds);

        switch (activity.OperationName)
        {
            case "memory.db.tx":
                // Read vs write is the distinction that matters most: writes are leader-bound in a
                // cluster and cannot be routed to replicas, so a write on the pre-model read path is a
                // materially worse cost than a read.
                turn.Add(activity.GetTagItem("db.mode") as string == "write"
                    ? "neo4j.tx.write"
                    : "neo4j.tx.read");
                if (activity.GetTagItem("db.records") is long records)
                    turn.Add("neo4j.records", records);
                if (activity.GetTagItem("db.bytes_est") is long bytesEstimate)
                    turn.Add("neo4j.bytes_est", bytesEstimate);
                if (activity.GetTagItem("db.transaction_entry_ms_est") is double entryEstimate)
                    turn.RecordSample("neo4j.transaction_entry_ms_est", entryEstimate);
                break;

            case "memory.db.query":
                turn.Add("neo4j.queries");
                turn.RecordQueryFingerprint(
                    SafeQueryFingerprint(activity.GetTagItem("db.query.fingerprint")));
                break;

            case "memory.recall.total":
                // Sourced from the span rather than the scenario's own return value so the count is the
                // one the memory layer actually assembled, not one the harness re-derived.
                if (activity.GetTagItem("memory.recall.items") is int items)
                    turn.Add("items.retrieved", items);
                if (activity.GetTagItem("memory.recall.chars") is int chars)
                    turn.Add("recall.chars", chars);
                foreach (var category in RecallCategories)
                {
                    if (activity.GetTagItem($"memory.recall.{category}") is int count)
                        turn.Add($"items.{category}", count);
                }
                break;

            case "memory.recall.access_tracking":
                if (activity.GetTagItem("memory.access_tracking.items") is int tracked)
                    turn.Add("access_tracking.items", tracked);
                break;

            case "memory.store.messages":
                if (activity.GetTagItem("memory.store.message_count") is int stored)
                    turn.Add("store.messages", stored);
                break;

            case "memory.extract.resolution":
                if (activity.GetTagItem("memory.extract.candidate_entities") is int candidates)
                    turn.Add("extract.candidate_entities", candidates);
                if (activity.GetTagItem("memory.extract.source_messages") is int sourceMessages)
                    turn.Add("extract.source_messages", sourceMessages);
                break;

            case "memory.persist.total":
                foreach (var kind in PersistedKinds)
                {
                    if (activity.GetTagItem($"memory.persist.{kind}") is int count)
                        turn.Add($"persist.{kind}", count);
                }
                break;
        }

        _trace?.Span(turn, activity);
    }

    private static string SafeQueryFingerprint(object? value)
    {
        if (value is not string fingerprint || string.IsNullOrWhiteSpace(fingerprint))
            return "unknown";

        foreach (var character in fingerprint)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-')
                return "unknown";
        }
        return fingerprint;
    }

    private void EndTurn(TurnRecord record, double elapsedMs)
    {
        record.DurationMs = elapsedMs;
        CurrentTurn.Value = null;
        lock (_gate) _records.Add(record);
        _trace?.TurnEnd(record);
    }

    public void Dispose() => _listener.Dispose();

    /// <summary>An open measurement scope; disposing it closes and files the iteration.</summary>
    public sealed class TurnScope : IDisposable
    {
        private readonly PerfCollector _owner;
        private readonly long _startedAt = Stopwatch.GetTimestamp();
        private bool _disposed;

        internal TurnScope(PerfCollector owner, TurnRecord record)
        {
            _owner = owner;
            Record = record;
        }

        /// <summary>The record this scope is accumulating into.</summary>
        public TurnRecord Record { get; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner.EndTurn(Record, Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds);
        }
    }
}
