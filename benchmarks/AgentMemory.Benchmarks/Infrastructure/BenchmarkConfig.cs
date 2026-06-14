using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace AgentMemory.Benchmarks.Infrastructure;

/// <summary>
/// Runs every benchmark <b>in-process</b> (no per-benchmark child process), which lets all benchmarks
/// share the single <see cref="BenchmarkNeo4j"/> Testcontainer started on first use. Iteration counts are
/// kept modest because each operation is a real network round-trip to Neo4j (milliseconds), not a
/// nanosecond CPU kernel — the goal is comparative latency/throughput, not sub-microsecond precision.
/// </summary>
public sealed class BenchmarkConfig : ManualConfig
{
    public BenchmarkConfig()
    {
        AddJob(Job.Default
            .WithToolchain(InProcessEmitToolchain.Instance)
            .WithWarmupCount(3)
            .WithIterationCount(8)
            .WithInvocationCount(16)
            .WithUnrollFactor(1));

        AddLogger(ConsoleLogger.Default);
        AddColumnProvider(DefaultColumnProviders.Instance);
        WithOptions(ConfigOptions.DisableOptimizationsValidator); // tolerate IDE/dev launches; CI never runs this
    }
}
