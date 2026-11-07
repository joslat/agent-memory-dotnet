namespace AgentMemory.Neo4j.Retrieval;

/// <summary>
/// A single item from a retriever search result.
/// </summary>
/// <param name="Content">The text content of the result.</param>
/// <param name="Metadata">Additional metadata fields (score, etc.).</param>
/// <param name="Id">
/// The node the result came from: its application key (<c>id</c>) when it has one, else its element id; for a custom
/// retrieval query, the <c>id</c> column when it returns one. Null when nothing identifies it. Hybrid fusion keys on it, so
/// two nodes with the same text stay two results (#248).
/// </param>
internal record RetrieverResultItem(string Content, Dictionary<string, object?>? Metadata = null, string? Id = null);

/// <summary>
/// The result of a retriever search operation.
/// </summary>
/// <param name="Items">The list of result items.</param>
internal record RetrieverResult(IReadOnlyList<RetrieverResultItem> Items);
