namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// How an extracted entity was resolved against the existing entities.
/// </summary>
public enum EntityMatchType
{
    /// <summary>Case-insensitive exact match on name / canonical name / alias.</summary>
    Exact = 0,

    /// <summary>Fuzzy (approximate string) match above the configured threshold.</summary>
    Fuzzy = 1,

    /// <summary>Semantic (embedding-similarity) match above the configured threshold.</summary>
    Semantic = 2,

    /// <summary>No match — a new entity was created.</summary>
    New = 3
}
