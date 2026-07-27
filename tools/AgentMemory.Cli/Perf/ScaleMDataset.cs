using System.Diagnostics;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Volumes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Neo4j.Driver;
using Testcontainers.Neo4j;

namespace AgentMemory.Cli.Perf;

/// <summary>
/// Builds the persistent scale-M template and clones it into an isolated volume for each run.
/// The template is never exposed to product scenarios, so writes cannot contaminate later runs.
/// </summary>
public static class ScaleMDataset
{
    private const string Image = "neo4j:5.26";
    private const string User = "neo4j";
    private const string Password = "perfpassword";
    private const string SnapshotId = "scale-m-v1";
    private const string ForeignOwner = "scale-m-foreign-owner";
    private const string DataPath = "/data";

    public const int NodesPerLabel = 50_000;
    public const int ChunkSize = 1_000;
    public const int MemoryNodeCount = NodesPerLabel * 5;

    public static IReadOnlyList<string> MemoryLabels { get; } =
        ["Entity", "Fact", "Preference", "Message", "ReasoningTrace"];

    public static string SnapshotVolumeName(int dimensions) =>
        $"agentmemory-perf-m-neo4j-5-26-d{dimensions}-v1";

    internal static async Task<ScaleMRunVolume> PrepareRunVolumeAsync(
        int dimensions,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        var total = Stopwatch.StartNew();
        var snapshotName = SnapshotVolumeName(dimensions);
        var snapshotCreated = await EnsureSnapshotAsync(
            snapshotName, dimensions, log, cancellationToken).ConfigureAwait(false);

        var restore = Stopwatch.StartNew();
        var runVolume = new VolumeBuilder()
            .WithName($"agentmemory-perf-m-run-{Environment.ProcessId}-{Guid.NewGuid():N}")
            .WithCleanUp(true)
            .Build();
        await runVolume.CreateAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await CloneVolumeAsync(snapshotName, runVolume, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await runVolume.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        restore.Stop();
        total.Stop();
        log.WriteLine(
            $"perf: scale-M {(snapshotCreated ? "seeded snapshot and restored" : "restored snapshot")}; " +
            $"clone {restore.Elapsed.TotalSeconds:F1}s.");

        return new ScaleMRunVolume(
            runVolume,
            new ScaleDatasetInfo(
                "M",
                MemoryNodeCount,
                snapshotCreated ? "seeded" : "restored",
                restore.Elapsed.TotalMilliseconds,
                total.Elapsed.TotalMilliseconds,
                snapshotName));
    }

    internal static async Task<long> VerifyRestoredAsync(
        IDriver driver,
        CancellationToken cancellationToken)
    {
        await using var session = driver.AsyncSession();
        var shape = await ReadShapeAsync(session, cancellationToken).ConfigureAwait(false);
        ValidateShape(shape, "restored scale-M run volume");
        return shape.Total;
    }

    private static async Task<bool> EnsureSnapshotAsync(
        string snapshotName,
        int dimensions,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        log.WriteLine($"perf: checking scale-M snapshot {snapshotName}…");
        await using var container = BuildNeo4jContainer(snapshotName).Build();
        await container.StartAsync(cancellationToken).ConfigureAwait(false);

        await using var driver = GraphDatabase.Driver(
            container.GetConnectionString(),
            AuthTokens.Basic(User, Password));
        await driver.VerifyConnectivityAsync().ConfigureAwait(false);

        if (await IsSealedSnapshotAsync(driver, dimensions, cancellationToken).ConfigureAwait(false))
        {
            await using var existingSession = driver.AsyncSession();
            var existingShape = await ReadShapeAsync(
                existingSession,
                cancellationToken).ConfigureAwait(false);
            ValidateShape(existingShape, "scale-M snapshot");
            return false;
        }

        log.WriteLine(
            $"perf: scale-M snapshot absent; seeding {MemoryNodeCount:N0} nodes in " +
            $"{ChunkSize:N0}-node chunks…");
        await SeedAsync(driver, dimensions, log, cancellationToken).ConfigureAwait(false);
        await BootstrapSchemaAsync(
            container.GetConnectionString(), dimensions, cancellationToken).ConfigureAwait(false);

        await using (var session = driver.AsyncSession())
        {
            var cursor = await session.RunAsync("CALL db.awaitIndexes(600)").ConfigureAwait(false);
            await cursor.ConsumeAsync().ConfigureAwait(false);

            var shape = await ReadShapeAsync(session, cancellationToken).ConfigureAwait(false);
            ValidateShape(shape, "new scale-M snapshot");

            var marker = await session.RunAsync(
                """
                MERGE (m:PerfDatasetSnapshot {id: $id})
                SET m.dimensions = $dimensions,
                    m.node_count = $nodeCount,
                    m.version = 1,
                    m.sealed_at = datetime()
                RETURN m.node_count AS nodeCount
                """,
                new
                {
                    id = SnapshotId,
                    dimensions,
                    nodeCount = MemoryNodeCount,
                }).ConfigureAwait(false);
            await marker.ConsumeAsync().ConfigureAwait(false);
        }

        log.WriteLine($"perf: sealed scale-M snapshot with {MemoryNodeCount:N0} verified memory nodes.");
        return true;
    }

    private static Neo4jBuilder BuildNeo4jContainer(string volumeName) =>
        new Neo4jBuilder(Image)
            .WithEnvironment("NEO4J_AUTH", $"{User}/{Password}")
            .WithVolumeMount(volumeName, DataPath);

    private static async Task<bool> IsSealedSnapshotAsync(
        IDriver driver,
        int dimensions,
        CancellationToken cancellationToken)
    {
        await using var session = driver.AsyncSession();
        var cursor = await session.RunAsync(
            """
            MATCH (m:PerfDatasetSnapshot {id: $id})
            RETURN m.dimensions AS dimensions, m.node_count AS nodeCount, m.version AS version
            """,
            new { id = SnapshotId }).ConfigureAwait(false);

        var records = await cursor.ToListAsync(cancellationToken).ConfigureAwait(false);
        if (records.Count == 0) return false;

        var record = records.Single();
        var actualDimensions = record["dimensions"].As<int>();
        var actualCount = record["nodeCount"].As<int>();
        var actualVersion = record["version"].As<int>();
        if (actualDimensions != dimensions || actualCount != MemoryNodeCount || actualVersion != 1)
        {
            throw new InvalidOperationException(
                $"Scale-M snapshot marker is incompatible: dimensions={actualDimensions}, " +
                $"nodeCount={actualCount}, version={actualVersion}.");
        }

        return true;
    }

    private static async Task SeedAsync(
        IDriver driver,
        int dimensions,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        var embedding = new float[dimensions];
        embedding[0] = -1f;

        await using var session = driver.AsyncSession(
            o => o.WithDefaultAccessMode(global::Neo4j.Driver.AccessMode.Write));

        foreach (var label in MemoryLabels)
        {
            for (var start = 0; start < NodesPerLabel; start += ChunkSize)
            {
                var end = Math.Min(start + ChunkSize, NodesPerLabel);
                var cursor = await session.RunAsync(
                    SeedQuery(label),
                    new
                    {
                        start,
                        end,
                        ownerId = ForeignOwner,
                        embedding,
                    }).ConfigureAwait(false);
                await cursor.ConsumeAsync().ConfigureAwait(false);

                if (end % 10_000 == 0)
                    log.WriteLine($"perf: scale-M seeded {label} {end:N0}/{NodesPerLabel:N0}.");
            }
        }
    }

    private static string SeedQuery(string label) => label switch
    {
        "Entity" => """
            UNWIND range($start, $end - 1) AS i
            MERGE (n:Entity {id: 'scale-m-entity-' + toString(i)})
            ON CREATE SET n.owner_id = $ownerId,
                          n.name = 'Scale M entity ' + toString(i),
                          n.canonical_name = 'scale m entity ' + toString(i),
                          n.type = 'SCALE_M',
                          n.description = 'Foreign scale fixture entity',
                          n.confidence = 0.5,
                          n.created_at = datetime('2026-01-01T00:00:00Z'),
                          n.embedding = $embedding
            """,
        "Fact" => """
            UNWIND range($start, $end - 1) AS i
            MERGE (n:Fact {id: 'scale-m-fact-' + toString(i)})
            ON CREATE SET n.owner_id = $ownerId,
                          n.owner_key = $ownerId,
                          n.subject = 'Scale M subject ' + toString(i),
                          n.predicate = 'scale_relation',
                          n.object = 'Scale M object ' + toString(i),
                          n.category = 'scale-m',
                          n.confidence = 0.5,
                          n.created_at = datetime('2026-01-01T00:00:00Z'),
                          n.embedding = $embedding
            """,
        "Preference" => """
            UNWIND range($start, $end - 1) AS i
            MERGE (n:Preference {id: 'scale-m-preference-' + toString(i)})
            ON CREATE SET n.owner_id = $ownerId,
                          n.category = 'scale-m',
                          n.preference = 'Foreign scale fixture preference ' + toString(i),
                          n.confidence = 0.5,
                          n.created_at = datetime('2026-01-01T00:00:00Z'),
                          n.embedding = $embedding
            """,
        "Message" => """
            UNWIND range($start, $end - 1) AS i
            MERGE (n:Message {id: 'scale-m-message-' + toString(i)})
            ON CREATE SET n.session_id = 'scale-m-session-' + toString(i % 1000),
                          n.conversation_id = 'scale-m-conversation-' + toString(i % 1000),
                          n.role = CASE WHEN i % 2 = 0 THEN 'user' ELSE 'assistant' END,
                          n.content = 'Foreign scale fixture message ' + toString(i),
                          n.timestamp = datetime('2026-01-01T00:00:00Z'),
                          n.tool_call_ids = [],
                          n.metadata = '{}',
                          n.embedding = $embedding
            """,
        "ReasoningTrace" => """
            UNWIND range($start, $end - 1) AS i
            MERGE (n:ReasoningTrace {id: 'scale-m-trace-' + toString(i)})
            ON CREATE SET n.session_id = 'scale-m-session-' + toString(i % 1000),
                          n.owner_id = $ownerId,
                          n.task = 'Foreign scale fixture reasoning task ' + toString(i),
                          n.outcome = 'done',
                          n.success = true,
                          n.metadata = '{}',
                          n.started_at = datetime('2026-01-01T00:00:00Z'),
                          n.completed_at = datetime('2026-01-01T00:00:01Z'),
                          n.task_embedding = $embedding
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(label), label, null),
    };

    private static async Task BootstrapSchemaAsync(
        string uri,
        int dimensions,
        CancellationToken cancellationToken)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddNeo4jAgentMemory(
            memory => { },
            neo4j =>
            {
                neo4j.Uri = uri;
                neo4j.Username = User;
                neo4j.Password = Password;
                neo4j.Database = "neo4j";
                neo4j.EmbeddingDimensions = dimensions;
            },
            llm => { });

        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<ISchemaBootstrapper>()
            .BootstrapAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<DatasetShape> ReadShapeAsync(
        IAsyncSession session,
        CancellationToken cancellationToken)
    {
        var cursor = await session.RunAsync(
            """
            CALL { MATCH (n:Entity) WHERE n.id STARTS WITH 'scale-m-' RETURN count(n) AS entities }
            CALL { MATCH (n:Fact) WHERE n.id STARTS WITH 'scale-m-' RETURN count(n) AS facts }
            CALL { MATCH (n:Preference) WHERE n.id STARTS WITH 'scale-m-' RETURN count(n) AS preferences }
            CALL { MATCH (n:Message) WHERE n.id STARTS WITH 'scale-m-' RETURN count(n) AS messages }
            CALL { MATCH (n:ReasoningTrace) WHERE n.id STARTS WITH 'scale-m-' RETURN count(n) AS traces }
            RETURN entities, facts, preferences, messages, traces
            """,
            new { }).ConfigureAwait(false);
        var record = await cursor.SingleAsync(cancellationToken).ConfigureAwait(false);
        return new DatasetShape(
            record["entities"].As<long>(),
            record["facts"].As<long>(),
            record["preferences"].As<long>(),
            record["messages"].As<long>(),
            record["traces"].As<long>());
    }

    private static void ValidateShape(DatasetShape shape, string source)
    {
        if (shape.Values.Any(count => count != NodesPerLabel))
        {
            throw new InvalidOperationException(
                $"{source} has invalid shape: Entity={shape.Entities}, Fact={shape.Facts}, " +
                $"Preference={shape.Preferences}, Message={shape.Messages}, " +
                $"ReasoningTrace={shape.Traces}; expected {NodesPerLabel} each.");
        }
    }

    private static async Task CloneVolumeAsync(
        string snapshotName,
        IVolume runVolume,
        CancellationToken cancellationToken)
    {
        await using var helper = new ContainerBuilder(Image)
            .WithEntrypoint("tail")
            .WithCommand("-f", "/dev/null")
            .WithVolumeMount(snapshotName, "/source", DotNet.Testcontainers.Configurations.AccessMode.ReadOnly)
            .WithVolumeMount(runVolume, "/target")
            .Build();
        await helper.StartAsync(cancellationToken).ConfigureAwait(false);

        var result = await helper.ExecAsync(
            ["/bin/sh", "-c", "cp -a /source/. /target/"],
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to restore scale-M snapshot: {result.Stderr}");
        }
    }

    private sealed record DatasetShape(
        long Entities,
        long Facts,
        long Preferences,
        long Messages,
        long Traces)
    {
        public IReadOnlyList<long> Values => [Entities, Facts, Preferences, Messages, Traces];
        public long Total => Values.Sum();
    }
}

public sealed record ScaleDatasetInfo(
    string Scale,
    long MemoryNodeCount,
    string Source,
    double RestoreMilliseconds,
    double PreparationMilliseconds,
    string SnapshotVolume);

internal sealed record ScaleMRunVolume(IVolume Volume, ScaleDatasetInfo Info);

