namespace AgentMemory.Cli.Perf;

internal sealed record BoundedWorkResult<T>(
    IReadOnlyList<T> Results,
    int MaxConcurrency);

/// <summary>
/// Executes a fixed ordered cohort through a bounded number of workers.
/// Admission stops on cancellation or the first failure; already admitted work is cancelled and awaited.
/// </summary>
internal static class BoundedWorkScheduler
{
    public static async Task<BoundedWorkResult<T>> RunAsync<T>(
        IReadOnlyList<Func<CancellationToken, Task<T>>> work,
        int maxConcurrency,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrency);

        if (work.Count == 0)
            return new BoundedWorkResult<T>([], 0);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var results = new T[work.Count];
        var nextIndex = -1;
        var active = 0;
        var observedMaximum = 0;
        var workerCount = Math.Min(maxConcurrency, work.Count);

        async Task WorkerAsync()
        {
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                var index = Interlocked.Increment(ref nextIndex);
                if (index >= work.Count)
                    return;

                var current = Interlocked.Increment(ref active);
                UpdateMaximum(ref observedMaximum, current);
                try
                {
                    results[index] = await work[index](linked.Token).ConfigureAwait(false);
                }
                catch
                {
                    await linked.CancelAsync().ConfigureAwait(false);
                    throw;
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            }
        }

        var workers = Enumerable.Range(0, workerCount)
            .Select(_ => WorkerAsync())
            .ToArray();

        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return new BoundedWorkResult<T>(results, observedMaximum);
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (candidate <= current)
                return;
            if (Interlocked.CompareExchange(ref target, candidate, current) == current)
                return;
        }
    }
}
