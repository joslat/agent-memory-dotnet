using System.Text.Json;
using Neo4j.Driver;
using Testcontainers.Neo4j;

namespace AgentMemory.LongMemEval;

/// <summary>
/// K2. Reports whether the reasoning-trace and GraphRAG surfaces have anything to return.
/// </summary>
/// <remarks>
/// Read-only, and needs no Azure credentials. It exists because both surfaces have carried a recall
/// budget of zero in every quality measurement, so "they return nothing" has never been distinguished
/// from "they were never asked".
/// <para>
/// K1 fixed the order of questions deliberately: a FAILED or missing vector index produces the exact
/// same symptom as an empty corpus, and this repository has already shipped a fix for indexes left in
/// the FAILED state. Index health is therefore checked before any count is interpreted.
/// </para>
/// </remarks>
internal static class LongMemEvalSurfaceProbeProgram
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
            var destination = Path.GetFullPath(Value(args, "--output")
                ?? Path.Combine("artifacts", "evaluation", "surface-probe.json"));

            var container = new Neo4jBuilder(Image)
                .WithEnvironment("NEO4J_AUTH", $"{User}/{Password}")
                .WithVolumeMount(volume, "/data")
                .Build();
            await container.StartAsync().ConfigureAwait(false);
            try
            {
                await using var driver = GraphDatabase.Driver(
                    container.GetConnectionString(), AuthTokens.Basic(User, Password));
                await using var session = driver.AsyncSession();

                var indexes = await ReadAsync(session, """
                    SHOW INDEXES YIELD name, type, state, entityType, labelsOrTypes, properties
                    RETURN name, type, state, entityType, labelsOrTypes, properties
                    """).ConfigureAwait(false);
                var counts = await ReadAsync(session, """
                    MATCH (t:ReasoningTrace) WITH count(t) AS traces
                    OPTIONAL MATCH (s:ReasoningStep) WITH traces, count(s) AS steps
                    OPTIONAL MATCH (e:Entity) WITH traces, steps, count(e) AS entities
                    OPTIONAL MATCH (m:Message) RETURN traces, steps, entities, count(m) AS messages
                    """).ConfigureAwait(false);
                var traceShape = await ReadAsync(session, """
                    MATCH (t:ReasoningTrace)
                    RETURN count(t) AS total,
                           count(t.task_embedding) AS withEmbedding,
                           sum(CASE WHEN t.success = true THEN 1 ELSE 0 END) AS successful,
                           sum(CASE WHEN t.success = false THEN 1 ELSE 0 END) AS failed
                    """).ConfigureAwait(false);

                var report = new
                {
                    schemaVersion = 1,
                    generatedAtUtc = DateTimeOffset.UtcNow,
                    sourceVolume = volume,
                    // K1: a FAILED or absent index looks exactly like an empty corpus from outside.
                    indexes,
                    counts,
                    traceShape,
                };
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await File.WriteAllTextAsync(
                    destination,
                    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })
                        + Environment.NewLine).ConfigureAwait(false);

                var c = counts.FirstOrDefault();
                var traces = c is not null && c.TryGetValue("traces", out var t) ? t : 0;
                var vectorIndex = indexes.FirstOrDefault(i =>
                    i.TryGetValue("name", out var n) &&
                    string.Equals(n?.ToString(), "task_embedding_idx", StringComparison.Ordinal));
                Console.WriteLine(
                    $"longmemeval: task_embedding_idx {(vectorIndex is null ? "ABSENT" : "state=" + vectorIndex["state"])}");
                Console.WriteLine($"longmemeval: ReasoningTrace nodes = {traces}");
                if (Equals(traces, 0L) || Equals(traces, 0))
                {
                    // A real result, and it is about the corpus rather than the code: nothing in the
                    // LongMemEval ingestion path writes traces.
                    Console.WriteLine(
                        "longmemeval: the trace surface cannot be measured on this graph - it holds no " +
                        "traces at all. That is a property of the fixture, not of the surface.");
                }

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
            Console.Error.WriteLine($"longmemeval: surface probe failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task<IReadOnlyList<Dictionary<string, object?>>> ReadAsync(
        IAsyncSession session, string cypher) =>
        await session.ExecuteReadAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(cypher).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return (IReadOnlyList<Dictionary<string, object?>>)records
                .Select(record => record.Keys.ToDictionary(
                    key => key,
                    key => record[key] is null ? null : (object?)record[key].ToString()))
                .ToList();
        }).ConfigureAwait(false);

    private static string? Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0) return null;
        if (index + 1 >= args.Length)
            throw new ArgumentException($"{name} requires a value.");
        return args[index + 1];
    }
}
