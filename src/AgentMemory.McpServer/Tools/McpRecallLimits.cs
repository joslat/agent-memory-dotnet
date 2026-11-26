namespace AgentMemory.McpServer.Tools;

/// <summary>
/// RVW-REST-HOSTS (the review's hosts-5): a recall tool's <c>maxResults</c> is chosen by the model, and each section's
/// vector search asks for several times that many neighbours. Unbounded, 2,000,000,000 asked Neo4j for an exhaustive
/// search on the shared store. A prompt has no use for more than this per section.
/// </summary>
internal static class McpRecallLimits
{
    /// <summary>The most items one recall section returns through MCP.</summary>
    public const int MaxPerSection = 100;

    /// <summary>The asked-for count, held to 1..<see cref="MaxPerSection"/>.</summary>
    public static int Section(int asked) => Math.Clamp(asked, 1, MaxPerSection);
}
