using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentMemory.Scenarios;

/// <summary>
/// Writes every model call of a run to a JSON-lines file (who asked, what was sent, what came back, the tokens), counts
/// them, and refuses the call past the run's cap. A run that hits the cap fails rather than finishing half-measured.
/// </summary>
internal sealed class RecordingChatClient(IChatClient inner, string purpose, CallLedger ledger) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var sent = messages.ToList();
        var number = ledger.Take(purpose);
        var watch = Stopwatch.StartNew();
        try
        {
            var response = await base.GetResponseAsync(sent, options, cancellationToken).ConfigureAwait(false);
            ledger.Write(new
            {
                call = number, purpose, ms = watch.ElapsedMilliseconds,
                request = sent.Select(m => new { role = m.Role.Value, text = m.Text }),
                response = response.Text,
                tokens = new { input = response.Usage?.InputTokenCount, output = response.Usage?.OutputTokenCount },
            });
            return response;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ledger.Write(new { call = number, purpose, ms = watch.ElapsedMilliseconds, request = sent.Select(m => new { role = m.Role.Value, text = m.Text }), error = exception.Message });
            throw;
        }
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        // Streaming answers are not recorded as a whole; nothing in the scenario streams, so this is refused loudly.
        throw new NotSupportedException("The scenario records whole responses; streaming is not recorded.");
    }
}

/// <summary>Counts a run's model calls by purpose, enforces the cap, and owns the calls file.</summary>
internal sealed class CallLedger(string path, int maxCalls) : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly StreamWriter _file = new(path, append: false) { AutoFlush = true };
    private readonly ConcurrentDictionary<string, int> _byPurpose = new();
    private int _calls;

    public int Calls => _calls;

    /// <summary>Whether a call was refused for the cap. Read by the runner, because the library may catch the refusal.</summary>
    public bool Exhausted => Volatile.Read(ref _exhausted) == 1;

    private int _exhausted;

    public IReadOnlyDictionary<string, int> ByPurpose => _byPurpose;

    public int Take(string purpose)
    {
        var number = Interlocked.Increment(ref _calls);
        if (number > maxCalls)
        {
            Interlocked.Exchange(ref _exhausted, 1);
            throw new InvalidOperationException($"The run reached its cap of {maxCalls} model calls; it stops rather than finishing unmeasured.");
        }

        _byPurpose.AddOrUpdate(purpose, 1, (_, n) => n + 1);
        return number;
    }

    public void Write(object record)
    {
        var line = JsonSerializer.Serialize(record, Json);
        lock (_file) _file.WriteLine(line);
    }

    public void Dispose() => _file.Dispose();
}

/// <summary>
/// Records the library's spans (the "AgentMemory" activity source: recall, its sections, the gate, writes) with their
/// tags, as JSON lines. Recall's diagnostics live in these tags, so a fact that never reached the prompt can be traced.
/// </summary>
internal sealed class SpanRecorder : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly ActivityListener _listener;
    private readonly StreamWriter _file;
    private string _scope = "";

    public SpanRecorder(string path)
    {
        _file = new StreamWriter(path, append: false) { AutoFlush = true };
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name.StartsWith("AgentMemory", StringComparison.OrdinalIgnoreCase),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = Stopped,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>What the spans recorded from now on belong to ("write graph-recommended", "ask similar …").</summary>
    public string Scope { set => Volatile.Write(ref _scope, value); }

    private void Stopped(Activity activity)
    {
        var line = JsonSerializer.Serialize(new
        {
            scope = Volatile.Read(ref _scope),
            name = activity.OperationName,
            ms = Math.Round(activity.Duration.TotalMilliseconds, 1),
            status = activity.Status.ToString(),
            tags = activity.TagObjects.ToDictionary(t => t.Key, t => t.Value?.ToString()),
        }, Json);
        lock (_file) _file.WriteLine(line);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _file.Dispose();
    }
}

/// <summary>
/// The library's own log, at Debug and above, to a file. AgentMemory does not throw when it cannot write or recall
/// something; it logs and carries on, so this file is where a lost memory shows up. Warnings are counted.
/// </summary>
internal sealed class FileLog(string path) : ILoggerProvider
{
    private readonly StreamWriter _file = new(path, append: false) { AutoFlush = true };
    private int _warnings;
    private string _scope = "";

    public int Warnings => _warnings;

    public string Scope { set => Volatile.Write(ref _scope, value); }

    public ILogger CreateLogger(string categoryName) => new Category(this, categoryName);

    public void Dispose() => _file.Dispose();

    private sealed class Category(FileLog log, string name) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            if (logLevel >= LogLevel.Warning) Interlocked.Increment(ref log._warnings);
            var line = $"[{Volatile.Read(ref log._scope)}] {logLevel} {name}: {formatter(state, exception)}";
            lock (log._file)
            {
                log._file.WriteLine(exception is null ? line : $"{line} | {exception.GetType().Name}: {exception.Message}");
            }
        }
    }
}
