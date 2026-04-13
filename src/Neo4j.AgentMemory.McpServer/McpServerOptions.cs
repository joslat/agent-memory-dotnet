namespace Neo4j.AgentMemory.McpServer;

/// <summary>
/// Configuration options for the Agent Memory MCP Server.
/// </summary>
public sealed class McpServerOptions
{
    /// <summary>
    /// Server name reported to MCP clients.
    /// </summary>
    public string ServerName { get; set; } = "neo4j-agent-memory";

    /// <summary>
    /// Server version reported to MCP clients.
    /// </summary>
    public string ServerVersion { get; set; } = "1.0.0";

    /// <summary>
    /// Whether to enable the graph_query tool (allows arbitrary Cypher execution).
    /// Disabled by default for security.
    /// </summary>
    public bool EnableGraphQuery { get; set; }

    /// <summary>
    /// Default session ID to use when none is provided by the client.
    /// </summary>
    public string DefaultSessionId { get; set; } = "default";

    /// <summary>
    /// Default confidence score for entities, facts, and preferences added via MCP tools.
    /// </summary>
    public double DefaultConfidence { get; set; } = 0.9;
}
