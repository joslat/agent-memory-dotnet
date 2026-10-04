using System.Security.Cryptography;
using System.Text.Json;
using AgentMemory.RouterArena.Data;
using AgentMemory.Validation;
using Testcontainers.Neo4j;

namespace AgentMemory.RouterArena.Record;

/// <summary>
/// The matrix world in a Neo4j of its own (Testcontainers, removed after) with real embeddings from a local Ollama, the
/// decay pass already run: what <c>record</c> recalls from and <c>time</c> times against. No store of anyone's is touched.
/// </summary>
public sealed class MatrixStore : IAsyncDisposable
{
    private MatrixStore(OllamaEmbeddingGenerator embeddings, Neo4jContainer container, PackStore store, int dimensions, int faded, string password)
    {
        Password = password;
        Embeddings = embeddings;
        Container = container;
        Store = store;
        Dimensions = dimensions;
        Faded = faded;
    }

    public OllamaEmbeddingGenerator Embeddings { get; }
    public Neo4jContainer Container { get; }
    public PackStore Store { get; }
    public int Dimensions { get; }

    /// <summary>The throwaway store's password (random, for this run only): for probes that read the graph directly.</summary>
    public string Password { get; }

    /// <summary>How many memories the decay pass faded.</summary>
    public int Faded { get; }

    /// <summary>Starts the store and loads the world; null (with the reason written) when the embedder cannot be reached.</summary>
    public static async Task<MatrixStore?> StartAsync(ValidationPack world, RecordRequest request, TextWriter output, string verb,
        CancellationToken cancellationToken = default)
    {
        var embeddings = new OllamaEmbeddingGenerator(request.Ollama.TrimEnd('/'), request.Model);
        int dimensions;
        try
        {
            dimensions = (await embeddings.GenerateAsync(["ping"], cancellationToken: cancellationToken).ConfigureAwait(false))[0].Vector.Length;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            output.WriteLine($"error: {verb}: no embeddings from {request.Ollama} ({request.Model}): {ex.Message}");
            embeddings.Dispose();
            return null;
        }
        embeddings.Dimensions = dimensions;

        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        var container = new Neo4jBuilder(Recorder.Image)
            .WithEnvironment("NEO4J_AUTH", $"neo4j/{password}")
            // Small: it holds one person's memory, and the machine running it is often short of memory.
            .WithEnvironment("NEO4J_server_memory_heap_max__size", "512m")
            .WithEnvironment("NEO4J_server_memory_pagecache_size", "128m")
            .Build();
        output.WriteLine($"{verb}: starting {Recorder.Image} (Testcontainers)…");
        await container.StartAsync(cancellationToken).ConfigureAwait(false);
        var runner = new ValidationPackRunner(o =>
        {
            o.Uri = container.GetConnectionString();
            o.Username = "neo4j";
            o.Password = password;
            o.Database = "neo4j";
            o.EmbeddingDimensions = dimensions;
            // The derived door: the accountant's derived facts need the arithmetic extension's indexes.
            o.Extensions.Add("arithmetic");
        }, _ => embeddings, ArenaLogging.Console);
        var store = await runner.LoadAsync(world, "matrix", cancellationToken).ConfigureAwait(false);
        // The forgetting switch: a decay pass run as of August 2023, when the 2023 memory was months old and nothing else had
        // been said yet, so it is the only memory that fades; every turn is still asked in October 2026.
        var faded = await store.DecayAsync(request.Owner, request.DecayAt, cancellationToken).ConfigureAwait(false);
        output.WriteLine($"{verb}: world {world.Id} loaded, embeddings {request.Model} ({dimensions}); the decay pass as of {request.DecayAt:yyyy-MM-dd} faded {faded}");
        return new MatrixStore(embeddings, container, store, dimensions, faded, password);
    }

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync().ConfigureAwait(false);
        await Container.DisposeAsync().ConfigureAwait(false);
        Embeddings.Dispose();
    }
}
