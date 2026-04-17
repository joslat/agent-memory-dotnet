namespace Neo4j.AgentMemory.Neo4j.Retrieval;

/// <summary>
/// A single item from a retriever search result.
/// </summary>
/// <param name="Content">The text content of the result.</param>
/// <param name="Metadata">Additional metadata fields (score, etc.).</param>
public record RetrieverResultItem(string Content, Dictionary<string, object?>? Metadata = null);

/// <summary>
/// The result of a retriever search operation.
/// </summary>
/// <param name="Items">The list of result items.</param>
public record RetrieverResult(IReadOnlyList<RetrieverResultItem> Items);
