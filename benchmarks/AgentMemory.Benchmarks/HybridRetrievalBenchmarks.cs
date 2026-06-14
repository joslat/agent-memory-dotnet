using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Benchmarks.Infrastructure;
using AgentMemory.Core.Stubs;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Services;

namespace AgentMemory.Benchmarks;

/// <summary>
/// Hybrid retrieval latency: <see cref="Neo4jGraphRagContextSource.GetContextAsync"/> in
/// <see cref="GraphRagSearchMode.Hybrid"/> mode (vector + fulltext, score-blended) over a corpus of
/// <see cref="SeedCount"/> <c>:Knowledge</c> nodes. The query is embedded by the deterministic stub
/// generator, so no LLM call is on the hot path. Read-only and repeatable.
/// </summary>
[MemoryDiagnoser]
public class HybridRetrievalBenchmarks
{
    [Params(2000)]
    public int SeedCount { get; set; }

    [Params(10)]
    public int TopK { get; set; }

    private Neo4jGraphRagContextSource _source = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        var db = await BenchmarkNeo4j.GetAsync();
        await db.CleanAsync();
        await db.CreateKnowledgeIndexesAsync();
        await db.SeedKnowledgeAsync(SeedCount);

        var options = Options.Create(new GraphRagOptions
        {
            IndexName = "knowledge_vec_idx",
            FulltextIndexName = "knowledge_ft_idx",
            SearchMode = GraphRagSearchMode.Hybrid,
            TopK = TopK,
            FilterStopWords = false
        });

        var embeddingGenerator = new StubEmbeddingGenerator(
            NullLogger<StubEmbeddingGenerator>.Instance, BenchmarkNeo4j.EmbeddingDimensions);

        _source = new Neo4jGraphRagContextSource(
            db.Driver,
            embeddingGenerator,
            options,
            NullLogger<Neo4jGraphRagContextSource>.Instance);
    }

    [Benchmark]
    public async Task<int> HybridRetrieve()
    {
        var result = await _source.GetContextAsync(new GraphRagContextRequest
        {
            SessionId = "bench",
            Query = "alpha beta gamma topic",
            TopK = TopK
        });
        return result.Items.Count;
    }
}
