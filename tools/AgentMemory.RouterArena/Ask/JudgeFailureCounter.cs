using AgentMemory.Gate;
using Microsoft.Extensions.Logging;

namespace AgentMemory.RouterArena.Ask;

internal sealed class JudgeFailureCounter : ILogger<SystemOneMemoryGate>
{
    private long _count;
    public long Count => Interlocked.Read(ref _count);
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        // Count failures without formatting or persisting private provider messages.
        if (IsEnabled(logLevel)) Interlocked.Increment(ref _count);
    }
}
