namespace AgentMemory.Core.Services;

/// <summary>
/// Threat TT-09: the length every embedding must have, as the store is configured (Neo4j registers it from
/// <c>Neo4jOptions.EmbeddingDimensions</c>). Null or 0: the length is not checked, only that every value is finite.
/// </summary>
internal sealed record EmbeddingShape(int? Dimensions)
{
    /// <summary>Why <paramref name="vector"/> may not be stored, or null when it may.</summary>
    public string? Problem(float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        if (vector.Length == 0) return null; // "no embedding": the existing failure value, handled by its callers
        if (Dimensions is > 0 and var expected && vector.Length != expected)
            return $"it has {vector.Length} dimensions; the store is configured for {expected}";
        for (var i = 0; i < vector.Length; i++)
            if (!float.IsFinite(vector[i]))
                return $"value {i} is {vector[i]}";
        return null;
    }

    /// <summary>No length known: only finite values are checked.</summary>
    public static EmbeddingShape Unknown { get; } = new((int?)null);
}
