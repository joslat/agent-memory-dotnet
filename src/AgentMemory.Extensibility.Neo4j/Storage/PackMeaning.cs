using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Neo4j.Infrastructure;
using Microsoft.Extensions.AI;
using Neo4j.Driver;
using static AgentMemory.Extensibility.Neo4j.Storage.ModuleRecordTransaction;

namespace AgentMemory.Extensibility.Neo4j.Storage;

/// <summary>How a host searches packs by meaning (feature 19).</summary>
[Experimental("AMEXT001")]
public sealed record PackMeaningOptions
{
    /// <summary>The nearest records a search asks the vector index for, before scope, time and filters.</summary>
    public int Candidates { get; init; } = 32;
    /// <summary>The lowest score a record found by meaning keeps: Neo4j's cosine score, (1 + cosine) / 2, so 0.5 is unrelated.</summary>
    public double MinimumScore { get; init; } = 0.75;
    /// <summary>The model the vectors are tagged with; null takes the embedder's own model id.</summary>
    public string? Model { get; init; }
    /// <summary>Whether a search keeps the full-text hits beside the meaning hits (merged by rank); false searches by meaning alone.</summary>
    public bool KeepWords { get; init; } = true;
}

/// <summary>
/// Packs searched by meaning (feature 19), opt-in. Each live shared record of a type a search view reads gets a vector node: the
/// text the view's full-text index holds, embedded by the host's embedder, tagged with the model and linked to the record. A
/// search embeds the question and merges the nearest records with the full-text hits by reciprocal rank. The record nodes are
/// not changed, so digests, exports, walks and erasure see what they saw before; an erased record's vector goes with it.
/// </summary>
[Experimental("AMEXT001")]
public sealed class PackMeaning
{
    internal const string Means = "AMPACK_MEANS";
    private const int Batch = 64;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly ConcurrentDictionary<int, bool> _indexes = new();

    /// <summary>Binds the host's embedder.</summary>
    public PackMeaning(IEmbeddingGenerator<string, Embedding<float>> embedder, PackMeaningOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(embedder);
        _embedder = embedder;
        Options = options ?? new();
        if (Options.Candidates is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(options), "Candidates must be 1 to 1,000.");
        Model = Options.Model ?? embedder.GetService<EmbeddingGeneratorMetadata>()?.DefaultModelId ?? "unnamed";
    }

    /// <summary>The options.</summary>
    public PackMeaningOptions Options { get; }

    /// <summary>The model the vectors are tagged with; a vector of another model is never compared.</summary>
    public string Model { get; }

    // One label and index per dimension: Neo4j's vector index holds one dimension.
    internal static string Label(int dimension) => "AmpackMeaning" + dimension.ToString(CultureInfo.InvariantCulture);
    internal static string IndexName(int dimension) => "ampack_meaning_" + dimension.ToString(CultureInfo.InvariantCulture);

    internal async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken) =>
        (await _embedder.GenerateAsync([text], cancellationToken: cancellationToken).ConfigureAwait(false))[0].Vector.ToArray();

    // The text a search view's full-text index holds of a record: its searched properties, in the view's order.
    private static string TextOf(IReadOnlyList<object?> values) =>
        string.Join(". ", values.Select(v => v?.ToString()).Where(v => !string.IsNullOrWhiteSpace(v)));

    /// <summary>
    /// Gives each live shared record of the resolution's searched types that has no vector of this model one. Idempotent: a
    /// record already embedded by this model is skipped; a changed record is a new version, so it is embedded again.
    /// </summary>
    public async Task<int> IndexAsync(INeo4jTransactionRunner runner, ModuleResolution resolution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(resolution);
        var made = 0;
        foreach (var module in resolution.Modules)
        foreach (var searched in (module.Views ?? []).Where(v => v.Search is not null).GroupBy(v => v.Type, StringComparer.Ordinal))
        {
            var properties = searched.SelectMany(v => v.Search!.Properties ?? []).Distinct(StringComparer.Ordinal).ToList();
            var rows = await runner.ReadAsync(transaction => RowsAsync(transaction,
                "MATCH (n:`" + searched.Key + "`) WHERE n.owner_id IS NULL AND n.invalidated_at IS NULL "
                + "AND NOT EXISTS { MATCH (v)-[:" + Means + "]->(n) WHERE v.model = $model } "
                + "RETURN n.id AS id, [p IN $properties | n[p]] AS texts ORDER BY n.id",
                new { model = Model, properties }, cancellationToken), cancellationToken).ConfigureAwait(false);
            var texts = rows.Select(r => (Id: r["id"].As<string>(), Text: TextOf(r["texts"].As<List<object?>>()))).Where(r => r.Text.Length > 0).ToList();
            for (var i = 0; i < texts.Count; i += Batch)
            {
                var part = texts.Skip(i).Take(Batch).ToList();
                var vectors = await _embedder.GenerateAsync(part.Select(p => p.Text), cancellationToken: cancellationToken).ConfigureAwait(false);
                var dimension = vectors[0].Vector.Length;
                await EnsureIndexAsync(runner, dimension, cancellationToken).ConfigureAwait(false);
                var batch = part.Select((p, j) => new Dictionary<string, object> { ["id"] = p.Id, ["vector"] = vectors[j].Vector.ToArray() }).ToList();
                await runner.WriteAsync(transaction => RowsAsync(transaction,
                    "UNWIND $rows AS r MATCH (n:`" + searched.Key + "` {id: r.id}) "
                    + "CREATE (v:`" + Label(dimension) + "` {model: $model, vector: r.vector})-[:" + Means + "]->(n)",
                    new { rows = batch, model = Model }, cancellationToken), cancellationToken).ConfigureAwait(false);
                made += part.Count;
            }
        }
        // A vector index fills in the background: wait, so the first search after an import finds what it imported.
        if (made > 0) await runner.WriteAsync(transaction => RowsAsync(transaction, "CALL db.awaitIndexes(300)", new { }, cancellationToken), cancellationToken).ConfigureAwait(false);
        return made;
    }

    /// <summary>Removes the vectors whose record is gone (an erasure deletes the record's versions; feature 08).</summary>
    public static Task ForgetOrphansAsync(INeo4jTransactionRunner runner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        return runner.WriteAsync(transaction => RowsAsync(transaction,
            "MATCH (v) WHERE any(l IN labels(v) WHERE l STARTS WITH 'AmpackMeaning') AND NOT (v)-[:" + Means + "]->() DETACH DELETE v",
            new { }, cancellationToken), cancellationToken);
    }

    private async Task EnsureIndexAsync(INeo4jTransactionRunner runner, int dimension, CancellationToken cancellationToken)
    {
        if (_indexes.ContainsKey(dimension)) return;
        await runner.WriteAsync(transaction => RowsAsync(transaction,
            "CREATE VECTOR INDEX " + IndexName(dimension) + " IF NOT EXISTS FOR (v:`" + Label(dimension) + "`) ON (v.vector) "
            + "OPTIONS {indexConfig: {`vector.dimensions`: " + dimension.ToString(CultureInfo.InvariantCulture) + ", `vector.similarity_function`: 'cosine'}}",
            new { }, cancellationToken), cancellationToken).ConfigureAwait(false);
        _indexes[dimension] = true;
    }

    // Whether the index of a dimension exists, asked once it does: a search before any import has no index to query.
    internal async Task<bool> HasIndexAsync(INeo4jTransactionRunner runner, int dimension, CancellationToken cancellationToken)
    {
        if (_indexes.ContainsKey(dimension)) return true;
        var rows = await runner.ReadAsync(transaction => RowsAsync(transaction, "SHOW INDEXES YIELD name WHERE name = $name RETURN name",
            new { name = IndexName(dimension) }, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0) return false;
        _indexes[dimension] = true;
        return true;
    }

    /// <summary>Reciprocal rank fusion of the full-text and the meaning rows, by record id: each list adds 1 / (60 + rank).</summary>
    internal static List<IRecord> Fuse(IReadOnlyList<IRecord> words, IReadOnlyList<IRecord> near)
    {
        static string Id(IRecord row) => row["row"].As<Dictionary<string, object>>()["id"].As<string>();
        var score = new Dictionary<string, (double Score, IRecord Row)>(StringComparer.Ordinal);
        foreach (var list in new[] { words, near })
            for (var rank = 0; rank < list.Count; rank++)
            {
                var id = Id(list[rank]);
                var add = 1.0 / (60 + rank + 1);
                score[id] = score.TryGetValue(id, out var held) ? (held.Score + add, held.Row) : (add, list[rank]);
            }
        return [.. score.OrderByDescending(p => p.Value.Score).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value.Row)];
    }
}
