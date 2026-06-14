namespace AgentMemory.Benchmarks.Infrastructure;

/// <summary>
/// A fast, statistics-free self-test: runs each benchmark's GlobalSetup + body once against the shared
/// Testcontainer (with small corpora) to prove the whole pipeline wires up and executes. Invoked via
/// <c>--smoke</c>.
/// </summary>
internal static class SmokeTest
{
    public static async Task RunAsync()
    {
        Console.WriteLine("[smoke] starting Neo4j container + schema bootstrap (first run pulls neo4j:5.26)...");

        var upsert = new BatchUpsertBenchmarks { BatchSize = 100 };
        await upsert.SetupAsync();
        await upsert.UpsertBatch();
        Console.WriteLine("[smoke] BatchUpsert OK (100 entities)");

        var vector = new VectorSearchBenchmarks { SeedCount = 200, TopK = 10 };
        await vector.SetupAsync();
        var hits = await vector.SearchByVector();
        Console.WriteLine($"[smoke] VectorSearch OK ({hits} hits)");

        var decay = new DecayPruneBenchmarks { SeedCount = 200 };
        await decay.SetupAsync();
        var pruned = await decay.PrunePass();
        Console.WriteLine($"[smoke] DecayPrune OK ({pruned} pruned, expected 0)");

        var hybrid = new HybridRetrievalBenchmarks { SeedCount = 200, TopK = 10 };
        await hybrid.SetupAsync();
        var items = await hybrid.HybridRetrieve();
        Console.WriteLine($"[smoke] HybridRetrieve OK ({items} items)");

        Console.WriteLine("[smoke] all four benchmarks executed successfully.");
    }
}
