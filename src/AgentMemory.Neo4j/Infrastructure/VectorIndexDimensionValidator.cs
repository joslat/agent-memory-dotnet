using AgentMemory.Abstractions.Exceptions;
using Neo4j.Driver;

namespace AgentMemory.Neo4j.Infrastructure;

/// <summary>
/// Pure comparison helpers for vector-index dimensionality validation (see
/// <see cref="EmbeddingDimensionMismatchException"/>). Kept separate from the bootstrapper so the
/// comparison logic is unit-testable without a live Neo4j instance.
/// </summary>
internal static class VectorIndexDimensionValidator
{
    /// <summary>
    /// Returns the indexes whose dimensionality differs from <paramref name="expectedDimensions"/>.
    /// Indexes with an unknown (null) dimensionality are ignored — they carry no comparable value.
    /// </summary>
    public static IReadOnlyList<VectorIndexDimension> FindMismatches(
        int expectedDimensions,
        IEnumerable<VectorIndexDimension> existing)
        => existing.Where(i => i.ActualDimensions != expectedDimensions).ToList();

    /// <summary>
    /// Throws <see cref="EmbeddingDimensionMismatchException"/> if any index's dimensionality differs
    /// from <paramref name="expectedDimensions"/>; otherwise returns normally.
    /// </summary>
    public static void EnsureMatches(
        int expectedDimensions,
        IEnumerable<VectorIndexDimension> existing)
    {
        var mismatches = FindMismatches(expectedDimensions, existing);
        if (mismatches.Count > 0)
            throw new EmbeddingDimensionMismatchException(expectedDimensions, mismatches);
    }

    /// <summary>
    /// Maps the rows returned by <see cref="Queries.SchemaQueries.ShowVectorIndexDimensions"/> into
    /// <see cref="VectorIndexDimension"/> values, skipping rows with a missing name or dimensionality.
    /// </summary>
    public static IReadOnlyList<VectorIndexDimension> MapRows(IEnumerable<IRecord> records)
    {
        var result = new List<VectorIndexDimension>();
        foreach (var record in records)
        {
            // As<T>() is a static extension that tolerates a null value, so no ?. is needed. Use it for
            // the dimension too (consistent with the codebase; the driver materializes it as a boxed long).
            var name = record["name"].As<string>();
            var rawDimensions = record["dimensions"];
            if (string.IsNullOrEmpty(name) || rawDimensions is null)
                continue;

            result.Add(new VectorIndexDimension(name, rawDimensions.As<int>()));
        }

        return result;
    }
}
