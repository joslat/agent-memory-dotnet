using System.Collections.Concurrent;

namespace AgentMemory.Extraction.Llm;

internal sealed class LlmExtractionBatchDiagnostics
{
    private const int MaximumDetails = 32;
    private readonly ConcurrentQueue<LlmExtractionBatchSplitDetail> _details = new();
    private long _splits;
    private long _droppedDetails;
    private long _contentRejections;
    private long _sessionsRefused;

    internal void RecordSplit(Exception exception, int sourceSessions)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceSessions);
        Interlocked.Increment(ref _splits);
        _details.Enqueue(new LlmExtractionBatchSplitDetail(
            Classify(exception),
            sourceSessions,
            exception.GetType().FullName ?? exception.GetType().Name));
        while (_details.Count > MaximumDetails && _details.TryDequeue(out _))
            Interlocked.Increment(ref _droppedDetails);
    }

    /// <summary>
    /// Records a batch the provider refused on content grounds, and the sessions lost with it.
    /// </summary>
    /// <remarks>
    /// Counted separately from splits because it is a different event with a different remedy: a
    /// split is a recoverable shape problem, a refusal is terminal for that text. The session count
    /// is what a reader needs — "2 refusals" says nothing about whether the corpus is usable, "2
    /// refusals costing 6 sessions of 2,418" does.
    /// </remarks>
    internal void RecordContentRejection(Exception exception, int sourceSessions)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceSessions);
        Interlocked.Increment(ref _contentRejections);
        Interlocked.Add(ref _sessionsRefused, sourceSessions);
        _details.Enqueue(new LlmExtractionBatchSplitDetail(
            "content-rejected",
            sourceSessions,
            exception.GetType().FullName ?? exception.GetType().Name));
        while (_details.Count > MaximumDetails && _details.TryDequeue(out _))
            Interlocked.Increment(ref _droppedDetails);
    }

    internal LlmExtractionBatchDiagnosticsSnapshot Snapshot() =>
        new(
            Interlocked.Read(ref _splits),
            _details.ToArray(),
            Interlocked.Read(ref _droppedDetails),
            Interlocked.Read(ref _contentRejections),
            Interlocked.Read(ref _sessionsRefused));

    private static string Classify(Exception exception) =>
        exception.Message switch
        {
            "Processed-session acknowledgement is incomplete or invalid." =>
                "acknowledgement",
            "A learned item has a missing or unknown source-session key." =>
                "source-session-key",
            "Batch exceeds the configured input-token budget." =>
                "token-budget",
            _ when exception is FormatException => "parse-or-format",
            _ when exception is OperationCanceledException => "cancellation",
            _ => "other"
        };
}

internal sealed record LlmExtractionBatchDiagnosticsSnapshot(
    long Splits,
    IReadOnlyList<LlmExtractionBatchSplitDetail> Details,
    long DroppedDetails,
    long ContentRejections = 0,
    long SessionsRefused = 0)
{
    internal LlmExtractionBatchDiagnosticsSnapshot Delta(
        LlmExtractionBatchDiagnosticsSnapshot baseline) =>
        new(
            Splits - baseline.Splits,
            Details.Skip(Math.Min(Details.Count, baseline.Details.Count)).ToArray(),
            DroppedDetails - baseline.DroppedDetails,
            ContentRejections - baseline.ContentRejections,
            SessionsRefused - baseline.SessionsRefused);
}

internal sealed record LlmExtractionBatchSplitDetail(
    string Reason,
    int SourceSessions,
    string ExceptionType);
