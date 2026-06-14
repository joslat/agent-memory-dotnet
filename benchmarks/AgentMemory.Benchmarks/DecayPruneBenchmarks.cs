using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Options;
using AgentMemory.Benchmarks.Infrastructure;
using AgentMemory.Core.Stubs;
using AgentMemory.Neo4j.Services;

namespace AgentMemory.Benchmarks;

/// <summary>
/// Decay-prune scan cost: <see cref="Neo4jMemoryDecayService.PruneExpiredMemoriesAsync"/> over
/// <see cref="SeedCount"/> fresh, high-confidence entities. Their retention score (≈ 0.95) is above the
/// default 0.1 threshold, so every pass scans and scores the whole set but prunes nothing — a stable,
/// repeatable measurement of the server-side decay scan rather than a one-shot deletion.
/// </summary>
[MemoryDiagnoser]
public class DecayPruneBenchmarks
{
    [Params(2000)]
    public int SeedCount { get; set; }

    private Neo4jMemoryDecayService _decay = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        var db = await BenchmarkNeo4j.GetAsync();
        await db.CleanAsync();
        await db.SeedFreshEntitiesAsync(SeedCount);
        _decay = new Neo4jMemoryDecayService(
            db.TransactionRunner,
            new SystemClock(),
            Options.Create(new MemoryDecayOptions()),
            NullLogger<Neo4jMemoryDecayService>.Instance);
    }

    [Benchmark]
    public async Task<int> PrunePass() => await _decay.PruneExpiredMemoriesAsync();
}
