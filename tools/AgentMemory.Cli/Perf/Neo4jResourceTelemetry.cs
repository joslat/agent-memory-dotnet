using System.Diagnostics;
using Neo4j.Driver;

namespace AgentMemory.Cli.Perf;

/// <summary>
/// Samples numeric, content-free Neo4j container and JVM resource evidence for explicit capacity labs.
/// The raw driver keeps monitoring queries out of product query/transaction counters.
/// </summary>
internal sealed class Neo4jResourceTelemetry : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ColdStaticProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DockerCommandTimeout = TimeSpan.FromSeconds(10);

    private readonly HermeticProfile _profile;
    private readonly TurnRecord _turn;
    private readonly double _effectiveCpuCount;
    private readonly CancellationTokenSource _stop;
    private readonly Task _dockerLoop;
    private readonly Task _neo4jLoop;
    private bool _disposed;

    private Neo4jResourceTelemetry(
        HermeticProfile profile,
        TurnRecord turn,
        double effectiveCpuCount,
        CancellationToken cancellationToken)
    {
        _profile = profile;
        _turn = turn;
        _effectiveCpuCount = effectiveCpuCount;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _dockerLoop = SampleDockerStatsAsync(_stop.Token);
        _neo4jLoop = SampleNeo4jAsync(_stop.Token);
    }

    public static async Task<Neo4jResourceTelemetry> StartAsync(
        HermeticProfile profile,
        TurnRecord turn,
        CancellationToken cancellationToken)
    {
        var cpuCount = await ReadDockerCpuCountAsync(cancellationToken).ConfigureAwait(false);
        turn.RecordSample("neo4j.container.effective_cpu_count", cpuCount);
        turn.Add("neo4j.telemetry.page_cache_global_supported", 0);

        var telemetry = new Neo4jResourceTelemetry(profile, turn, cpuCount, cancellationToken);
        await telemetry.RecordStaticSettingsAsync(cancellationToken).ConfigureAwait(false);
        return telemetry;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();

        await ObserveAsync(_dockerLoop).ConfigureAwait(false);
        await ObserveAsync(_neo4jLoop).ConfigureAwait(false);
        _stop.Dispose();
    }


    private async Task SampleDockerStatsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var dockerStats = StartDockerStats(_profile.ContainerId);
                var outputTask = dockerStats.StandardOutput.ReadToEndAsync(cancellationToken);
                var errorTask = dockerStats.StandardError.ReadToEndAsync(cancellationToken);
                var line = await outputTask
                    .WaitAsync(DockerCommandTimeout, cancellationToken)
                    .ConfigureAwait(false);
                await errorTask.WaitAsync(DockerCommandTimeout, cancellationToken).ConfigureAwait(false);
                await dockerStats.WaitForExitAsync(cancellationToken)
                    .WaitAsync(DockerCommandTimeout, cancellationToken)
                    .ConfigureAwait(false);
                if (dockerStats.ExitCode != 0)
                {
                    _turn.Add("neo4j.telemetry.docker_errors");
                }
                else if (!Neo4jContainerStatsParser.TryParse(line, _effectiveCpuCount, out var sample))
                {
                    _turn.Add("neo4j.telemetry.docker_parse_errors");
                }
                else
                {
                    _turn.Add("neo4j.telemetry.docker_samples");
                    _turn.RecordSample("neo4j.container.cpu_raw_percent", sample.CpuRawPercent);
                    _turn.RecordSample("neo4j.container.cpu_capacity_percent", sample.CpuCapacityPercent);
                    _turn.RecordSample("neo4j.container.memory_used_bytes", sample.MemoryUsedBytes);
                    _turn.RecordSample("neo4j.container.memory_limit_bytes", sample.MemoryLimitBytes);
                    _turn.RecordSample("neo4j.container.memory_percent", sample.MemoryPercent);
                    _turn.RecordSample("neo4j.container.block_read_bytes", sample.BlockReadBytes);
                    _turn.RecordSample("neo4j.container.block_write_bytes", sample.BlockWriteBytes);
                    _turn.RecordSample("neo4j.container.pids", sample.ProcessCount);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                _turn.Add("neo4j.telemetry.docker_errors");
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SampleNeo4jAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RecordJvmSampleAsync(cancellationToken).ConfigureAwait(false);
                await RecordTransactionSampleAsync(cancellationToken).ConfigureAwait(false);
                _turn.Add("neo4j.telemetry.neo4j_samples");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                _turn.Add("neo4j.telemetry.neo4j_errors");
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RecordStaticSettingsAsync(CancellationToken cancellationToken)
    {
        const string cypher = """
            SHOW SETTINGS YIELD name, value
            WHERE name IN ['server.memory.pagecache.size',
                           'server.memory.heap.initial_size',
                           'server.memory.heap.max_size',
                           'server.bolt.thread_pool_min_size',
                           'server.bolt.thread_pool_max_size',
                           'db.memory.transaction.total.max']
            RETURN name, value
            """;

        var records = await QueryAsync(cypher, cancellationToken, ColdStaticProbeTimeout)
            .ConfigureAwait(false);
        foreach (var record in records)
        {
            var name = record["name"].As<string>();
            var value = record["value"].As<string?>();
            switch (name)
            {
                case "server.memory.pagecache.size" when
                    Neo4jContainerStatsParser.TryParseBytes(value ?? string.Empty, out var bytes):
                    _turn.RecordSample("neo4j.page_cache.configured_bytes", bytes);
                    break;
                case "server.memory.heap.initial_size" when
                    Neo4jContainerStatsParser.TryParseBytes(value ?? string.Empty, out var bytes):
                    _turn.RecordSample("neo4j.heap.configured_initial_bytes", bytes);
                    break;
                case "server.memory.heap.max_size" when
                    Neo4jContainerStatsParser.TryParseBytes(value ?? string.Empty, out var bytes):
                    _turn.RecordSample("neo4j.heap.configured_max_bytes", bytes);
                    break;
                case "server.bolt.thread_pool_min_size" when long.TryParse(value, out var count):
                    _turn.RecordSample("neo4j.bolt.thread_pool_min", count);
                    break;
                case "server.bolt.thread_pool_max_size" when long.TryParse(value, out var count):
                    _turn.RecordSample("neo4j.bolt.thread_pool_max", count);
                    break;
                case "db.memory.transaction.total.max" when
                    Neo4jContainerStatsParser.TryParseBytes(value ?? string.Empty, out var bytes):
                    _turn.RecordSample("neo4j.transaction_memory.configured_max_bytes", bytes);
                    break;
            }
        }
    }

    private async Task RecordJvmSampleAsync(CancellationToken cancellationToken)
    {
        const string cypher = """
            CALL dbms.queryJmx('java.lang:type=Memory') YIELD attributes
            RETURN attributes.HeapMemoryUsage.value.properties.used AS heapUsed,
                   attributes.HeapMemoryUsage.value.properties.committed AS heapCommitted,
                   attributes.HeapMemoryUsage.value.properties.max AS heapMax,
                   attributes.NonHeapMemoryUsage.value.properties.used AS nonHeapUsed
            """;
        var record = (await QueryAsync(cypher, cancellationToken).ConfigureAwait(false)).Single();
        _turn.RecordSample("neo4j.jvm.heap_used_bytes", record["heapUsed"].As<long>());
        _turn.RecordSample("neo4j.jvm.heap_committed_bytes", record["heapCommitted"].As<long>());
        _turn.RecordSample("neo4j.jvm.heap_max_bytes", record["heapMax"].As<long>());
        _turn.RecordSample("neo4j.jvm.non_heap_used_bytes", record["nonHeapUsed"].As<long>());
    }

    private async Task RecordTransactionSampleAsync(CancellationToken cancellationToken)
    {
        const string cypher = """
            SHOW TRANSACTIONS YIELD currentQuery, currentQueryWaitTime, currentQueryCpuTime,
                                    currentQueryAllocatedBytes, currentQueryPageHits,
                                    currentQueryPageFaults, currentQueryActiveLockCount
            WHERE currentQuery IS NOT NULL
              AND NOT currentQuery STARTS WITH 'SHOW TRANSACTIONS'
            RETURN currentQueryWaitTime AS waitTime,
                   currentQueryCpuTime AS cpuTime,
                   currentQueryAllocatedBytes AS allocatedBytes,
                   currentQueryPageHits AS pageHits,
                   currentQueryPageFaults AS pageFaults,
                   currentQueryActiveLockCount AS activeLockCount
            """;
        var records = await QueryAsync(cypher, cancellationToken).ConfigureAwait(false);
        _turn.RecordSample("neo4j.transactions.active", records.Count);
        foreach (var record in records)
        {
            if (record["waitTime"] is Duration wait)
                _turn.RecordSample("neo4j.transaction.wait_ms", Milliseconds(wait));
            if (record["cpuTime"] is Duration cpu)
                _turn.RecordSample("neo4j.transaction.cpu_ms", Milliseconds(cpu));
            if (record["allocatedBytes"] is long allocated)
                _turn.RecordSample("neo4j.transaction.allocated_bytes", allocated);
            if (record["pageHits"] is long pageHits)
                _turn.RecordSample("neo4j.transaction.page_hits", pageHits);
            if (record["pageFaults"] is long pageFaults)
                _turn.RecordSample("neo4j.transaction.page_faults", pageFaults);
            if (record["activeLockCount"] is long activeLocks)
                _turn.RecordSample("neo4j.transaction.active_locks", activeLocks);
        }
    }

    private async Task<IReadOnlyList<IRecord>> QueryAsync(
        string cypher,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? ProbeTimeout;
        await using var session = _profile.Driver.AsyncSession();
        var cursor = await session.RunAsync(cypher)
            .WaitAsync(effectiveTimeout, cancellationToken)
            .ConfigureAwait(false);
        return await cursor.ToListAsync()
            .WaitAsync(effectiveTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    private static double Milliseconds(object value)
    {
        var duration = value.As<Duration>();
        return duration.Days * TimeSpan.FromDays(1).TotalMilliseconds +
               duration.Seconds * 1_000d + duration.Nanos / 1_000_000d;
    }

    private static Process StartDockerStats(string containerId)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("stats");
        startInfo.ArgumentList.Add("--no-stream");
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add("{{json .}}");
        startInfo.ArgumentList.Add("--no-trunc");
        startInfo.ArgumentList.Add(containerId);
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Docker resource sampler.");
    }

    private static async Task<double> ReadDockerCpuCountAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("info");
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add("{{.NCPU}}");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to inspect Docker CPU capacity.");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken)
            .WaitAsync(DockerCommandTimeout, cancellationToken)
            .ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken)
            .WaitAsync(DockerCommandTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (process.ExitCode != 0 ||
            !double.TryParse(
                output.Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var cpuCount) ||
            cpuCount <= 0)
        {
            throw new InvalidOperationException("Docker did not report a positive CPU capacity.");
        }

        return cpuCount;
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
