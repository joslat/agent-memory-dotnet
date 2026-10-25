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
    /// Whether to list the graph tools (<see cref="McpToolAccess.GraphTools"/>): <c>graph_query</c>, which runs arbitrary
    /// Cypher, and the whole-graph export and duplicate scan. Disabled by default for security; when disabled the tools
    /// are not listed at all.
    /// </summary>
    public bool EnableGraphQuery { get; set; }

    /// <summary>
    /// Whether to list the extraction tools (<see cref="McpToolAccess.ExtractionTools"/>). On by default. A host with no
    /// extraction model sets it false: with only the stub extractors the tools would report success and store nothing.
    /// </summary>
    public bool EnableExtraction { get; set; } = true;

    /// <summary>
    /// Pins every call to one owner. Null (the default) keeps the caller-supplied <c>userId</c>, where an omitted one
    /// means shared memory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When set, <c>userId</c> disappears from the listed tool schemas, every tool that takes it runs as this owner,
    /// a call that names another owner is refused, and the list resources read this owner's memory. The graph tools
    /// are not listed, because none of them can be confined to one owner.
    /// </para>
    /// <para>
    /// Tools that address data by a handle (a trace or step id) or by a session id are not owner-scoped and stay
    /// as they are; give each owner's sessions their own ids.
    /// </para>
    /// </remarks>
    public string? OwnerId { get; set; }

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
