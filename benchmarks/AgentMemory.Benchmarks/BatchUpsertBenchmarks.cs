using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Benchmarks.Infrastructure;
using AgentMemory.Neo4j.Repositories;

namespace AgentMemory.Benchmarks;

/// <summary>
/// Batch <c>UNWIND</c> upsert throughput: <see cref="Neo4jEntityRepository.UpsertBatchAsync"/> over a
/// fixed set of <see cref="BatchSize"/> entities (no embeddings — this isolates the UNWIND/MERGE path).
/// Re-running upserts the same ids, so the working set stays bounded (first call inserts, later calls
/// match-update); both exercise the full batch statement.
/// </summary>
[MemoryDiagnoser]
public class BatchUpsertBenchmarks
{
    [Params(100, 1000)]
    public int BatchSize { get; set; }

    private Neo4jEntityRepository _entities = null!;
    private IReadOnlyList<Entity> _batch = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        var db = await BenchmarkNeo4j.GetAsync();
        await db.CleanAsync();
        _entities = new Neo4jEntityRepository(db.TransactionRunner, NullLogger<Neo4jEntityRepository>.Instance);
        _batch = BenchmarkNeo4j.BuildEntities(BatchSize);
    }

    [Benchmark]
    public async Task UpsertBatch() => await _entities.UpsertBatchAsync(_batch);
}
