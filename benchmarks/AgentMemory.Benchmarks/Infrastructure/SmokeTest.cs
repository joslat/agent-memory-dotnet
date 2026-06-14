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

        // The read paths must return real results — otherwise the self-test is meaningless. Asserting the
        // counts (not just printing them) is what catches a silently-broken path: e.g. the GraphRAG source
        // swallows any retrieval exception and returns an EMPTY result, so without this check a broken
        // hybrid query would still print "OK" and exit 0.
        var vector = new VectorSearchBenchmarks { SeedCount = 200, TopK = 10 };
        await vector.SetupAsync();
        var hits = await vector.SearchByVector();
        Expect(hits > 0, $"VectorSearch returned {hits} hits over a 200-entity corpus — expected > 0 (vector index empty or query broken?)");
        Console.WriteLine($"[smoke] VectorSearch OK ({hits} hits)");

        var decay = new DecayPruneBenchmarks { SeedCount = 200 };
        await decay.SetupAsync();
        var pruned = await decay.PrunePass();
        Expect(pruned == 0, $"DecayPrune removed {pruned} fresh high-confidence nodes — expected 0 (steady-state assumption violated)");
        Console.WriteLine($"[smoke] DecayPrune OK ({pruned} pruned, expected 0)");

        var hybrid = new HybridRetrievalBenchmarks { SeedCount = 200, TopK = 10 };
        await hybrid.SetupAsync();
        var items = await hybrid.HybridRetrieve();
        Expect(items > 0, $"HybridRetrieve returned {items} items over a 200-node corpus — expected > 0 (index mismatch or swallowed retrieval error?)");
        Console.WriteLine($"[smoke] HybridRetrieve OK ({items} items)");

        Console.WriteLine("[smoke] all four benchmarks executed and asserted successfully.");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"[smoke] FAILED: {message}");
    }
}
