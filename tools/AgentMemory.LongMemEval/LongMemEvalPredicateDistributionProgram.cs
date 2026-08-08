using System.Text.Json;
using Neo4j.Driver;
using Testcontainers.Neo4j;

namespace AgentMemory.LongMemEval;

/// <summary>
/// J1.2. Reports the predicate distribution of an existing prepared volume.
/// </summary>
/// <remarks>
/// Read-only, and deliberately built without the AgentMemory service graph: it needs no embedding
/// generator, no chat client, and therefore <b>no Azure credentials</b>, which keeps a diagnostic that
/// only counts relation names from requiring the keys of a run that costs money.
/// <para>
/// The output is the objective anchor for the vocabulary's completeness axis. Without a measured
/// figure to cap it, "complete" is whatever a judge is willing to assert.
/// </para>
/// </remarks>
internal static class LongMemEvalPredicateDistributionProgram
{
    private const string Image = "neo4j:5.26";
    private const string User = "neo4j";
    private const string Password = "longmemeval-password";

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var volume = Value(args, "--volume")
                ?? throw new ArgumentException("--volume <prepared volume name> is required.");
            var heldOutFraction = double.TryParse(Value(args, "--held-out-fraction"), out var parsed)
                ? parsed
                : 0.2d;
            var seed = int.TryParse(Value(args, "--seed"), out var parsedSeed) ? parsedSeed : 42;
            var destination = Path.GetFullPath(Value(args, "--output")
                ?? Path.Combine("artifacts", "evaluation", "predicate-distribution.json"));

            Console.WriteLine($"longmemeval: reading predicate distribution from {volume} (read-only).");
            var container = new Neo4jBuilder(Image)
                .WithEnvironment("NEO4J_AUTH", $"{User}/{Password}")
                .WithVolumeMount(volume, "/data")
                .Build();
            await container.StartAsync().ConfigureAwait(false);
            try
            {
                await using var driver = GraphDatabase.Driver(
                    container.GetConnectionString(), AuthTokens.Basic(User, Password));
                var summary = await ReadAsync(driver).ConfigureAwait(false);
                var split = LongMemEvalPredicateDistribution.Split(
                    summary.Predicates, heldOutFraction, seed);

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    generatedAtUtc = DateTimeOffset.UtcNow,
                    sourceVolume = volume,
                    seed,
                    heldOutFraction,
                    // Recorded so a reader knows this table is relation names only, by construction.
                    contentPolicy = "predicate-keys-only-no-subjects-or-objects",
                    summary.RawPredicateCount,
                    summary.CanonicalPredicateCount,
                    summary.TotalFactCount,
                    summary.OwnerCount,
                    // Near 1.00 by design: the canonicalizer folds case and separators only, because
                    // folding synonyms at write time would merge bought onto sold irreversibly.
                    canonicalizerFoldRatio = summary.ConsolidationRatio,
                    perOwner = new
                    {
                        minPredicates = summary.MinPredicatesPerOwner,
                        maxPredicates = summary.MaxPredicatesPerOwner,
                        averagePredicates = summary.AveragePredicatesPerOwner
                    },
                    buildSlice = new
                    {
                        predicateCount = split.Build.Count,
                        factCount = split.BuildFactCount,
                        predicates = split.Build
                    },
                    heldOutSlice = new
                    {
                        predicateCount = split.HeldOut.Count,
                        factCount = split.HeldOutFactCount,
                        predicates = split.HeldOut
                    }
                }, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine)
                    .ConfigureAwait(false);

                Console.WriteLine(
                    $"longmemeval: {summary.TotalFactCount} facts over {summary.OwnerCount} owners; " +
                    $"{summary.RawPredicateCount} raw predicates, {summary.CanonicalPredicateCount} " +
                    $"canonical globally (canonicalizer fold {summary.ConsolidationRatio:F2}x, " +
                    "expected near 1.00 - it folds case and separators only).");
                Console.WriteLine(
                    $"longmemeval: per owner {summary.MinPredicatesPerOwner}-" +
                    $"{summary.MaxPredicatesPerOwner} canonical predicates " +
                    $"(mean {summary.AveragePredicatesPerOwner:F1}) - this is the figure comparable to " +
                    "the recorded pre/post-vocabulary baseline.");
                Console.WriteLine(
                    $"longmemeval: build slice {split.Build.Count} predicates / {split.BuildFactCount} facts; " +
                    $"held-out {split.HeldOut.Count} predicates / {split.HeldOutFactCount} facts.");
                Console.WriteLine($"longmemeval: report {destination}");
                return 0;
            }
            finally
            {
                await container.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"longmemeval: predicate distribution failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task<LongMemEvalPredicateDistributionSummary> ReadAsync(IDriver driver)
    {
        await using var session = driver.AsyncSession();

        // coalesce, because a fact written before canonical identity shipped has no predicate_key and
        // must still be counted rather than silently dropped from its own distribution.
        const string PerPredicate = """
            MATCH (f:Fact)
            WITH coalesce(f.predicate_key, toLower(f.predicate)) AS predicate, f
            RETURN predicate,
                   count(f) AS factCount,
                   count(DISTINCT f.owner_key) AS ownerCount
            ORDER BY factCount DESC, predicate ASC
            """;
        const string Totals = """
            MATCH (f:Fact)
            RETURN count(f) AS totalFacts,
                   count(DISTINCT f.predicate) AS rawPredicates,
                   count(DISTINCT coalesce(f.predicate_key, toLower(f.predicate))) AS canonicalPredicates,
                   count(DISTINCT f.owner_key) AS owners
            """;

        // Per owner as well as globally. The recorded pre/post-vocabulary baseline (421 -> 79-107) is a
        // PER-OWNER figure, and comparing a global distinct count against it would look like a
        // catastrophic regression while measuring an entirely different quantity.
        const string PerOwner = """
            MATCH (f:Fact)
            WITH f.owner_key AS owner,
                 count(DISTINCT coalesce(f.predicate_key, toLower(f.predicate))) AS predicates
            RETURN min(predicates) AS minPerOwner,
                   max(predicates) AS maxPerOwner,
                   avg(predicates) AS avgPerOwner
            """;

        var totals = await session.ExecuteReadAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(Totals).ConfigureAwait(false);
            return await cursor.SingleAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);

        var perOwner = await session.ExecuteReadAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(PerOwner).ConfigureAwait(false);
            return await cursor.SingleAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);

        var predicates = await session.ExecuteReadAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(PerPredicate).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records
                .Select(record => new LongMemEvalPredicateCount(
                    record["predicate"].As<string>(),
                    record["factCount"].As<int>(),
                    record["ownerCount"].As<int>()))
                .ToArray();
        }).ConfigureAwait(false);

        return new LongMemEvalPredicateDistributionSummary(
            totals["rawPredicates"].As<int>(),
            totals["canonicalPredicates"].As<int>(),
            totals["totalFacts"].As<int>(),
            totals["owners"].As<int>(),
            predicates)
        {
            MinPredicatesPerOwner = perOwner["minPerOwner"].As<int>(),
            MaxPredicatesPerOwner = perOwner["maxPerOwner"].As<int>(),
            AveragePredicatesPerOwner = perOwner["avgPerOwner"].As<double>()
        };
    }

    private static string? Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0) return null;
        if (index + 1 >= args.Length)
            throw new ArgumentException($"{name} requires a value.");
        return args[index + 1];
    }
}
