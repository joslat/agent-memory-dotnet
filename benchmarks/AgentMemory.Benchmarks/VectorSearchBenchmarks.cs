using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Benchmarks.Infrastructure;
using AgentMemory.Neo4j.Repositories;

namespace AgentMemory.Benchmarks;

/// <summary>
/// Entity vector search latency: <see cref="Neo4jEntityRepository.SearchByVectorAsync"/> against a corpus
/// of <see cref="SeedCount"/> embedded entities, returning the <see cref="TopK"/> nearest. Read-only, so it
/// is naturally repeatable.
/// </summary>
[MemoryDiagnoser]
public class VectorSearchBenchmarks
{
    [Params(2000)]
    public int SeedCount { get; set; }

    [Params(10, 50)]
    public int TopK { get; set; }

    private Neo4jEntityRepository _entities = null!;
    private float[] _query = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        var db = await BenchmarkNeo4j.GetAsync();
        await db.CleanAsync();
        await db.SeedEntitiesWithEmbeddingsAsync(SeedCount);
        _entities = new Neo4jEntityRepository(db.TransactionRunner, NullLogger<Neo4jEntityRepository>.Instance);
        _query = DeterministicEmbedding.Create(seed: 424242, BenchmarkNeo4j.EmbeddingDimensions);
    }

    [Benchmark]
    public async Task<int> SearchByVector()
    {
        var results = await _entities.SearchByVectorAsync(_query, limit: TopK);
        return results.Count;
    }
}
