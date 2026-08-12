namespace AgentMemory.McpServer;

/// <summary>
/// Configuration options for the Agent Memory MCP Server.
/// </summary>
public sealed class AgentMemoryMcpOptions
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

    /// <summary>
    /// Exposes only the tools that read. Every tool that creates, modifies, invalidates or derives
    /// stored memory is removed from the server's tool list entirely.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Removed, not refused at call time: a tool the client can see is a tool a model will try, and an
    /// error return teaches it nothing about what the server is for. A read-only server should look
    /// like a read-only server in the handshake.
    /// </para>
    /// <para>
    /// The classification is <see cref="McpToolAccess"/>, where <b>writes</b> are the enumerated set,
    /// so a tool added later and left unclassified is withheld rather than silently exposed.
    /// </para>
    /// </remarks>
    public bool ReadOnly { get; set; }
}
