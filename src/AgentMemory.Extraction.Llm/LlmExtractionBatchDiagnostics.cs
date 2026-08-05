using System.Collections.Concurrent;

namespace AgentMemory.Extraction.Llm;

internal sealed class LlmExtractionBatchDiagnostics
{
    private const int MaximumDetails = 32;
    private readonly ConcurrentQueue<LlmExtractionBatchSplitDetail> _details = new();
    private long _splits;
    private long _droppedDetails;

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

    internal LlmExtractionBatchDiagnosticsSnapshot Snapshot() =>
        new(
            Interlocked.Read(ref _splits),
            _details.ToArray(),
            Interlocked.Read(ref _droppedDetails));

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
    long DroppedDetails)
{
    internal LlmExtractionBatchDiagnosticsSnapshot Delta(
        LlmExtractionBatchDiagnosticsSnapshot baseline) =>
        new(
            Splits - baseline.Splits,
            Details.Skip(Math.Min(Details.Count, baseline.Details.Count)).ToArray(),
            DroppedDetails - baseline.DroppedDetails);
}

internal sealed record LlmExtractionBatchSplitDetail(
    string Reason,
    int SourceSessions,
    string ExceptionType);
