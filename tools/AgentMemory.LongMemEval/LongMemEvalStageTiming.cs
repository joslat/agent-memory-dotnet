using System.Diagnostics;

namespace AgentMemory.LongMemEval;

public sealed record LongMemEvalStageTimings(
    double StorageMs,
    double ExtractionPersistenceMs,
    double GraphReadBackMs,
    double RetrievalMs,
    double AnswerMs)
{
    public static LongMemEvalStageTimings Zero { get; } = new(0, 0, 0, 0, 0);
}

internal sealed class LongMemEvalStageTimingCollector
{
    private readonly object _lock = new();
    private TimeSpan _storage;
    private TimeSpan _extractionPersistence;
    private TimeSpan _graphReadBack;
    private TimeSpan _retrieval;
    private TimeSpan _answer;

    public async Task<T> MeasureAsync<T>(
        LongMemEvalStage stage,
        Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            stopwatch.Stop();
            Add(stage, stopwatch.Elapsed);
        }
    }

    public LongMemEvalStageTimings Snapshot()
    {
        lock (_lock)
        {
            return new LongMemEvalStageTimings(
                _storage.TotalMilliseconds,
                _extractionPersistence.TotalMilliseconds,
                _graphReadBack.TotalMilliseconds,
                _retrieval.TotalMilliseconds,
                _answer.TotalMilliseconds);
        }
    }

    private void Add(LongMemEvalStage stage, TimeSpan elapsed)
    {
        lock (_lock)
        {
            switch (stage)
            {
                case LongMemEvalStage.Storage:
                    _storage += elapsed;
                    break;
                case LongMemEvalStage.ExtractionPersistence:
                    _extractionPersistence += elapsed;
                    break;
                case LongMemEvalStage.GraphReadBack:
                    _graphReadBack += elapsed;
                    break;
                case LongMemEvalStage.Retrieval:
                    _retrieval += elapsed;
                    break;
                case LongMemEvalStage.Answer:
                    _answer += elapsed;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(stage), stage, null);
            }
        }
    }
}

internal enum LongMemEvalStage
{
    Storage,
    ExtractionPersistence,
    GraphReadBack,
    Retrieval,
    Answer
}
