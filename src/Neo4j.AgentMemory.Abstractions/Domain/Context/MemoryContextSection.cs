namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Represents a section of memory context with items of a specific type.
/// </summary>
/// <typeparam name="T">Type of items in this section.</typeparam>
public sealed record MemoryContextSection<T>
{
    /// <summary>
    /// Items in this section.
    /// </summary>
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();

    /// <summary>
    /// Section-level metadata (e.g., retrieval method, scores).
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } =
        new Dictionary<string, object>();

    /// <summary>
    /// Empty section singleton.
    /// </summary>
    public static MemoryContextSection<T> Empty { get; } = new();
}
