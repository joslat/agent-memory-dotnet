using System.Diagnostics;
using System.Globalization;

namespace AgentMemory.LongMemEval;

internal static class LongMemEvalPreparationWatchdog
{
    internal static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        LongMemEvalChatCallMeter meter,
        long expectedProviderCalls,
        TimeSpan overallTimeout,
        TimeSpan noProviderProgressTimeout,
        string phase,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedProviderCalls);
        if (overallTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(overallTimeout));
        if (noProviderProgressTimeout <= TimeSpan.Zero ||
            noProviderProgressTimeout > overallTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(noProviderProgressTimeout));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        ArgumentNullException.ThrowIfNull(output);

        var initialCompleted = meter.Snapshot().CompletedCalls;
        var targetCompleted = checked(initialCompleted + expectedProviderCalls);
        using var overallCancellation = new CancellationTokenSource(overallTimeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            overallCancellation.Token);
        using var executionFinished = new CancellationTokenSource();
        var watchdogReason = 0;
        var monitor = MonitorAsync();

        try
        {
            return await operation(linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            Volatile.Read(ref watchdogReason) != 0 ||
            overallCancellation.IsCancellationRequested)
        {
            var reason = Volatile.Read(ref watchdogReason) == 2
                ? "no-provider-progress"
                : "overall-timeout";
            throw new TimeoutException(
                Diagnostic(phase, reason, meter.Snapshot()),
                exception);
        }
        finally
        {
            executionFinished.Cancel();
            try
            {
                await monitor.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (executionFinished.IsCancellationRequested)
            {
            }
        }

        async Task MonitorAsync()
        {
            var lastCompleted = initialCompleted;
            var lastProgress = Stopwatch.StartNew();
            var poll = TimeSpan.FromSeconds(Math.Min(
                5d,
                Math.Max(0.01d, noProviderProgressTimeout.TotalSeconds / 4d)));
            while (!executionFinished.IsCancellationRequested)
            {
                await Task.Delay(poll, executionFinished.Token).ConfigureAwait(false);
                if (overallCancellation.IsCancellationRequested)
                {
                    Interlocked.CompareExchange(ref watchdogReason, 1, 0);
                    linkedCancellation.Cancel();
                    return;
                }

                var snapshot = meter.Snapshot();
                if (snapshot.CompletedCalls > lastCompleted)
                {
                    lastCompleted = snapshot.CompletedCalls;
                    lastProgress.Restart();
                    output.WriteLine(
                        $"longmemeval: {phase} provider progress " +
                        $"{lastCompleted - initialCompleted}/{expectedProviderCalls}; " +
                        $"maximum concurrency {snapshot.MaximumConcurrency}.");
                }
                else if (snapshot.CompletedCalls < targetCompleted &&
                         lastProgress.Elapsed >= noProviderProgressTimeout)
                {
                    Interlocked.CompareExchange(ref watchdogReason, 2, 0);
                    linkedCancellation.Cancel();
                    return;
                }
            }
        }
    }

    private static string Diagnostic(
        string phase,
        string reason,
        LongMemEvalChatCallSnapshot snapshot)
    {
        var firstFailure = snapshot.FailureDetails.FirstOrDefault();
        var slowest = snapshot.CallDetails
            .OrderByDescending(detail => detail.DurationMilliseconds)
            .FirstOrDefault();
        return $"LongMemEval {phase} watchdog fired ({reason}); provider calls " +
               $"started/completed={snapshot.Calls}/{snapshot.CompletedCalls}, " +
               $"failures={snapshot.Failures}, retries={snapshot.RetryCalls}, " +
               $"aggregate_provider_ms={snapshot.Duration.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture)}, " +
               $"maximum_provider_concurrency={snapshot.MaximumConcurrency}, " +
               $"first_failure_type={firstFailure?.ExceptionType ?? "none"}, " +
               $"first_failure_status={firstFailure?.ProviderStatus?.ToString(CultureInfo.InvariantCulture) ?? "none"}, " +
               $"slowest_call={slowest?.CallOrdinal.ToString(CultureInfo.InvariantCulture) ?? "none"}, " +
               $"slowest_provider_ms={slowest?.DurationMilliseconds.ToString("F2", CultureInfo.InvariantCulture) ?? "none"}, " +
               $"slowest_input={slowest?.EstimatedInputTokens?.ToString(CultureInfo.InvariantCulture) ?? "none"}.";
    }
}
