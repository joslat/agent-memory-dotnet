using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentMemory.RouterArena.Record;

/// <summary>
/// Every stack the arena builds logs here: warnings from everything, and what the gate and the update judge decide. A
/// failure inside the library (an embedding that could not be made, a judge that did not answer) reaches the output; it was
/// once only a warning no logger printed, and a run recorded without embeddings for hours (41.08).
/// </summary>
internal static class ArenaLogging
{
    public static void Console(ILoggingBuilder builder) =>
        builder.AddSimpleConsole(c => c.SingleLine = true)
            .AddProvider(new LostWrites.Provider())
            .SetMinimumLevel(LogLevel.Warning)
            .AddFilter("AgentMemory.Gate", LogLevel.Information)
            .AddFilter("AgentMemory.Core.Extraction.PersistenceStage", LogLevel.Information)
            // ARENA_WRITER_LOG=debug: what the writer was shown and what it answered, turn by turn (a diagnosis, not a run).
            .AddFilter("AgentMemory.Extraction.Llm.LlmMemoryWriter",
                Environment.GetEnvironmentVariable("ARENA_WRITER_LOG") == "debug" ? LogLevel.Debug : LogLevel.Warning);
}

/// <summary>
/// The writes the library lost without throwing (2026-10-10): a network outage made the writer fail on 534 of HaluMem's 1,403
/// turns and 610 of LongMemEval's 1,686, the library caught each failure (and fell back to the extractors, which failed too),
/// and the runners, which count only what reaches them as an exception, said "0 failed". Counted here from the library's own
/// error lines, per question or session: <see cref="Begin"/> starts a count for the async flow that calls it (each of
/// several questions run at once gets its own), and every error line logged in that flow is counted in it.
/// </summary>
internal static class LostWrites
{
    public sealed class Count
    {
        private int _writerThrew;
        private int _errors;

        /// <summary>Turns the store-aware writer failed ("nothing is stored for this turn"): not written by the writer.</summary>
        public int WriterThrew => Volatile.Read(ref _writerThrew);

        /// <summary>Every error line the library logged in this flow (the writer's included).</summary>
        public int Errors => Volatile.Read(ref _errors);

        internal void Add(bool writerThrew)
        {
            Interlocked.Increment(ref _errors);
            if (writerThrew) Interlocked.Increment(ref _writerThrew);
        }
    }

    private static readonly AsyncLocal<Count?> Current = new();
    private static readonly Count All = new();

    /// <summary>A new count for the calling flow (and what it awaits).</summary>
    public static Count Begin() => Current.Value = new Count();

    /// <summary>Every error line logged by the library in this process.</summary>
    public static Count Total => All;

    /// <summary>Whether a run should stop: more than <paramref name="percent"/>% of its turns so far lost, past a few.</summary>
    public static bool TooMany(int lost, int turns, double percent = 2.0) => lost > Math.Max(3, percent / 100.0 * turns);

    internal sealed class Provider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) =>
            categoryName.StartsWith("AgentMemory.", StringComparison.Ordinal) ? new Counter() : NullLogger.Instance;

        public void Dispose()
        {
        }
    }

    private sealed class Counter : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Error) return;
            var writerThrew = formatter(state, exception).Contains("nothing is stored for this turn", StringComparison.Ordinal);
            All.Add(writerThrew);
            Current.Value?.Add(writerThrew);
        }
    }
}
