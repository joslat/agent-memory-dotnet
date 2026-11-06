namespace AgentMemory.McpServer;

/// <summary>
/// How the MCP recall tools treat recalled text that reads like an instruction (#92, the MCP surface).
/// </summary>
public enum McpRecallSecurityMode
{
    /// <summary>
    /// The default, as in the Agent Framework and Semantic Kernel adapters: instruction-like text is returned,
    /// escaped and flagged <c>instructionLike</c>, so the client decides.
    /// </summary>
    Permissive = 0,

    /// <summary>
    /// Instruction-like text below <see cref="AgentMemoryMcpOptions.MinimumTrustForAdmissionBypass"/> is withheld:
    /// the item keeps its id and flags and is returned without its text.
    /// </summary>
    Strict = 1,
}
