using Neo4j.AgentMemory.Abstractions.Domain;

namespace Neo4j.AgentMemory.Neo4j.Infrastructure;

/// <summary>
/// Configuration options for <see cref="Services.Neo4jGraphRagContextSource"/>.
/// </summary>
public sealed class GraphRagOptions
{
    /// <summary>
    /// Name of the vector index. Required.
    /// </summary>
    public required string IndexName { get; set; }

    /// <summary>
    /// Search mode. Defaults to <see cref="GraphRagSearchMode.Hybrid"/>.
    /// </summary>
    public GraphRagSearchMode SearchMode { get; set; } = GraphRagSearchMode.Hybrid;

    /// <summary>
    /// Fulltext index name. Required for <see cref="GraphRagSearchMode.Hybrid"/> and
    /// used as the primary index for <see cref="GraphRagSearchMode.Fulltext"/>.
    /// </summary>
    public string? FulltextIndexName { get; set; }

    /// <summary>
    /// Optional Cypher clause appended after index search for graph enrichment.
    /// </summary>
    public string? RetrievalQuery { get; set; }

    /// <summary>
    /// Default maximum results. Overridden per-request by <see cref="GraphRagContextRequest.TopK"/>.
    /// </summary>
    public int TopK { get; set; } = 5;

    /// <summary>
    /// Whether to filter English stop words from fulltext queries.
    /// </summary>
    public bool FilterStopWords { get; set; } = true;
}
