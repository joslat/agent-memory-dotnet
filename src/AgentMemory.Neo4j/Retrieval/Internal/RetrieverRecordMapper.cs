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
    /// node's <c>text</c> property, falling back to <c>content</c>, then the node's string form.
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
        return new RetrieverResultItem(content, new Dictionary<string, object?> { ["score"] = score });
    }
}
