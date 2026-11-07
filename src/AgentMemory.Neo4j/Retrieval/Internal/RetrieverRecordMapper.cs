using Neo4j.Driver;

namespace AgentMemory.Neo4j.Retrieval.Internal;

/// <summary>
/// Shared mapping of Neo4j retriever records to <see cref="RetrieverResultItem"/>. The standard
/// <c>RETURN node, score</c> projection is identical for the vector and fulltext retrievers, so the
/// node→item conversion lives here rather than being copy-pasted into each.
/// </summary>
internal static class RetrieverRecordMapper
{
    /// <summary>
    /// Maps a standard <c>(node, score)</c> record to a result item, taking the display text from the
    /// node's <c>text</c> property, falling back to <c>content</c>, then the node's string form. The item's identity is
    /// the node's <c>id</c> property, else its element id.
    /// </summary>
    public static RetrieverResultItem FromNodeScore(IRecord record)
    {
        var node = record["node"].As<INode>();
        var score = record["score"].As<double>();
        var content = node.Properties.TryGetValue("text", out var text)
            ? text?.ToString() ?? ""
            : node.Properties.TryGetValue("content", out var c)
                ? c?.ToString() ?? ""
                : node.ToString()!;
        var id = node.Properties.TryGetValue("id", out var key) && key is not null ? key.ToString() : node.ElementId;
        return new RetrieverResultItem(content, new Dictionary<string, object?> { ["score"] = score }, id);
    }
}
