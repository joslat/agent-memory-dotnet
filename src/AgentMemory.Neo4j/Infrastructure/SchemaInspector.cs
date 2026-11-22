using AgentMemory.Neo4j.Queries;
using Microsoft.Extensions.Options;
using Neo4j.Driver;

namespace AgentMemory.Neo4j.Infrastructure;

/// <summary>
/// What a store holds of AgentMemory's schema now: the constraints and indexes <see cref="ISchemaBootstrapper"/> creates
/// that are present and that are missing, and the dimensions of every vector index the store has.
/// </summary>
/// <param name="Expected">How many objects the bootstrap creates for the configured dimensions.</param>
/// <param name="Missing">The expected objects the store does not have, in bootstrap order.</param>
/// <param name="VectorDimensions">Each vector index of the store, by name, with its dimensions.</param>
/// <param name="ConfiguredDimensions">The dimensions this library was configured with (<see cref="Neo4jOptions.EmbeddingDimensions"/>).</param>
public sealed record SchemaInspection(
    int Expected,
    IReadOnlyList<string> Missing,
    IReadOnlyDictionary<string, int> VectorDimensions,
    int ConfiguredDimensions)
{
    /// <summary>Every object the bootstrap creates is there.</summary>
    public bool Complete => Missing.Count == 0;

    /// <summary>Nothing of the schema is there: a new store.</summary>
    public bool Empty => Missing.Count == Expected;

    /// <summary>Every vector index the store has holds the configured dimensions.</summary>
    public bool DimensionsMatch => VectorDimensions.Values.All(d => d == ConfiguredDimensions);
}

/// <summary>
/// Reads, without changing anything, whether a store holds AgentMemory's schema: the same comparison as the CLI's
/// <c>schema-check</c>, for a host's health check or a binding that has no .NET (the native library's <c>admin</c>
/// profile).
/// </summary>
public interface ISchemaInspector
{
    Task<SchemaInspection> InspectAsync(CancellationToken cancellationToken = default);
}

internal sealed class SchemaInspector(INeo4jTransactionRunner tx, IOptions<Neo4jOptions> options) : ISchemaInspector
{
    private const string ShowVectorIndexes =
        "SHOW INDEXES YIELD name, type, options WHERE type = 'VECTOR' RETURN name, options";

    public async Task<SchemaInspection> InspectAsync(CancellationToken cancellationToken = default)
    {
        var dimensions = options.Value.EmbeddingDimensions;
        var expected = SchemaConformance.ExpectedObjectNames(dimensions);
        var (names, vectors) = await tx.ReadAsync(async runner =>
        {
            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (var query in new[] { SchemaQueries.ShowConstraintNames, SchemaQueries.ShowIndexNames })
            {
                var cursor = await runner.RunAsync(query).ConfigureAwait(false);
                foreach (var record in await cursor.ToListAsync().ConfigureAwait(false))
                    if (record["name"].As<string>() is { Length: > 0 } name) found.Add(name);
            }
            var sizes = new Dictionary<string, int>(StringComparer.Ordinal);
            var vectorCursor = await runner.RunAsync(ShowVectorIndexes).ConfigureAwait(false);
            foreach (var record in await vectorCursor.ToListAsync().ConfigureAwait(false))
                if (VectorDimensionsOf(record["options"]) is { } size) sizes[record["name"].As<string>()] = size;
            return (found, sizes);
        }, cancellationToken).ConfigureAwait(false);
        return new SchemaInspection(expected.Count, SchemaConformance.MissingObjects(expected, names), vectors, dimensions);
    }

    /// <summary>The <c>vector.dimensions</c> of an index's <c>indexConfig</c>, if it states one.</summary>
    internal static int? VectorDimensionsOf(object? indexOptions)
    {
        if (indexOptions is not IReadOnlyDictionary<string, object> all ||
            !all.TryGetValue("indexConfig", out var config) || config is not IReadOnlyDictionary<string, object> settings ||
            !settings.TryGetValue("vector.dimensions", out var value))
            return null;
        return value switch
        {
            long n => (int)n,
            int n => n,
            double d => (int)d,
            _ => null,
        };
    }
}
