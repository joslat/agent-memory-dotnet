using System.Security.Cryptography;
using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.Inference;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.RouterArena.Data;
using AgentMemory.Validation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Neo4j.Driver;
using Testcontainers.Neo4j;

namespace AgentMemory.RouterArena.Record;

/// <summary>
/// <c>store-jar</c> (root PLAN 40.97): the storage side, measured on the library's real write path. For each turn, the world
/// is loaded fresh (its own prefix, one throwaway Neo4j for all), then the turn (after its earlier messages) is written
/// through the library's model extraction (the configured provider: GLM on Bitdeer here), at a fixed instant; everything
/// the write created or invalidated at that instant is read back from the graph: facts, entities, relationships,
/// preferences, and the memories it closed. Scoring against the turn's <c>stores</c> labels is `storage_jar.py`.
/// </summary>
public sealed class StoreJar(TextWriter output)
{
    private static readonly DateTimeOffset WrittenAt = DateTimeOffset.Parse("2026-10-03T11:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    public async Task<int> RunAsync(string worldPath, IReadOnlyList<MatrixItem> turns, string outPath, bool dryRun, string owner = "marta",
        CancellationToken cancellationToken = default)
    {
        var resolution = InferenceProviderEnvironment.Resolve();
        if (resolution.Settings is not { } settings)
        {
            output.WriteLine("error: store-jar: no inference provider configured (AI_INFERENCE_PROVIDER, BITDEER_API_KEY)");
            return 1;
        }
        if (!InferenceClientFactory.TryCreateChatClient(settings, "extraction", out var chat, out var why))
        {
            output.WriteLine($"error: store-jar: {why}");
            return 1;
        }
        output.WriteLine($"store-jar: {turns.Count} turns, extraction by {settings.Provider} ({settings.Model}); written at {WrittenAt:O}");
        if (dryRun)
        {
            output.WriteLine($"store-jar: dry run: about {turns.Count} extraction calls (one per turn) and {turns.Count} world loads. Nothing was sent.");
            return 0;
        }

        var world = ValidationPackReader.ReadFile(worldPath);
        var request = new RecordRequest(worldPath, "", outPath, []);
        using var ollama = new OllamaEmbeddingGenerator(request.Ollama.TrimEnd('/'), request.Model);
        ollama.Dimensions = (await ollama.GenerateAsync(["ping"], cancellationToken: cancellationToken).ConfigureAwait(false))[0].Vector.Length;
        // The world is loaded fresh every turn: its texts are embedded once, then served from the cache.
        var embeddings = new CachingEmbeddingGenerator(ollama);
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        await using var container = new Neo4jBuilder(Recorder.Image)
            .WithEnvironment("NEO4J_AUTH", $"neo4j/{password}")
            .WithEnvironment("NEO4J_server_memory_heap_max__size", "768m")
            .WithEnvironment("NEO4J_server_memory_pagecache_size", "128m")
            .Build();
        await container.StartAsync(cancellationToken).ConfigureAwait(false);
        void Neo4j(Neo4jOptions o)
        {
            o.Uri = container.GetConnectionString();
            o.Username = "neo4j";
            o.Password = password;
            o.Database = "neo4j";
            o.EmbeddingDimensions = ollama.Dimensions ?? 1024;
            o.Extensions.Add("arithmetic");
        }
        var runner = new ValidationPackRunner(Neo4j, _ => embeddings, ArenaLogging.Console);

        var results = new List<object>();
        var n = 0;
        foreach (var turn in turns)
        {
            var prefix = $"sj{++n:000}";
            await using var store = await runner.LoadAsync(world, prefix, cancellationToken).ConfigureAwait(false);
            var services = new ServiceCollection();
            services.AddLogging(ArenaLogging.Console);
            services.AddSingleton<IClock>(new FixedClock(WrittenAt));
            services.AddNeo4jAgentMemory(store.Options, Neo4j, _ => { });
            services.AddSingleton(chat);
            services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(embeddings);
            await using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var shortTerm = scope.ServiceProvider.GetRequiredService<IShortTermMemoryService>();
            var pipeline = scope.ServiceProvider.GetRequiredService<IMemoryExtractionPipeline>();
            var ownerId = store.Owner(owner);
            var sessionId = $"{prefix}-{owner}-now";
            var k = 0;
            Message? said = null;
            string? error = null;
            try
            {
                foreach (var (role, text) in turn.Prior.Select(p => (p.Role, p.Text)).Append(("user", turn.Text)))
                    said = await shortTerm.AddMessageAsync(new Message
                    {
                        MessageId = $"{sessionId}-jar{k++}", ConversationId = sessionId, SessionId = sessionId, Role = role, Content = text,
                        TimestampUtc = WrittenAt,
                    }, cancellationToken).ConfigureAwait(false);
                await pipeline.ExtractAsync(new ExtractionRequest { SessionId = sessionId, UserId = ownerId, Messages = [said!] }, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
            }
            var written = await WrittenAsync(scope.ServiceProvider.GetRequiredService<INeo4jTransactionRunner>(), ownerId, cancellationToken).ConfigureAwait(false);
            results.Add(new { turn = turn.Id, text = turn.Text, category = turn.Category, written, error });
            output.WriteLine($"  {n}/{turns.Count} {turn.Id}: {written.Count} written{(error is null ? "" : $" ({error[..Math.Min(80, error.Length)]})")} "
                + $"[embeddings: {embeddings.Hits} cached, {embeddings.Misses} made]");
            await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(new { format = "store-jar/1", writtenAt = WrittenAt, model = settings.Model, results },
                new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);
        }
        output.WriteLine($"store-jar: {results.Count} turns to {outPath}");
        return 0;
    }

    /// <summary>Every node or relationship of the owner created or closed at the write's instant, without vectors.</summary>
    private static Task<List<Dictionary<string, object?>>> WrittenAsync(INeo4jTransactionRunner tx, string ownerId, CancellationToken cancellationToken) =>
        tx.ReadAsync(async runner =>
        {
            var rows = new List<Dictionary<string, object?>>();
            foreach (var cypher in new[]
            {
                "MATCH (x) WHERE x.owner_id = $owner AND (x.created_at = datetime($at) OR x.invalidated_at = datetime($at)) RETURN labels(x) AS kind, properties(x) AS p, null AS s, null AS t",
                "MATCH (s)-[x]->(t) WHERE x.owner_id = $owner AND (x.created_at = datetime($at) OR x.invalidated_at = datetime($at)) RETURN [type(x)] AS kind, properties(x) AS p, s.name AS s, t.name AS t",
            })
            {
                var cursor = await runner.RunAsync(cypher, new Dictionary<string, object?> { ["owner"] = ownerId, ["at"] = WrittenAt.ToString("O") }).ConfigureAwait(false);
                foreach (var r in await cursor.ToListAsync().ConfigureAwait(false))
                {
                    var p = r["p"].As<IDictionary<string, object>>()
                        .Where(kv => !kv.Key.Contains("embedding", StringComparison.Ordinal))
                        .ToDictionary(kv => kv.Key, kv => (object?)kv.Value?.ToString());
                    rows.Add(new Dictionary<string, object?>
                    {
                        ["kind"] = string.Join(",", r["kind"].As<List<object>>()), ["source"] = r["s"].As<string?>(), ["target"] = r["t"].As<string?>(), ["props"] = p,
                    });
                }
            }
            return rows;
        }, cancellationToken);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
