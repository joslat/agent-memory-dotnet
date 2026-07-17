namespace AgentMemory.McpServer.Nams;

/// <summary>
/// The deterministic, backend-capability-aware list of NAMS MCP tools (engineering plan Phase 8 exit
/// criterion: "tool list deterministic per backend"). <see cref="SupportedToolNames"/> is what a host would
/// consult before exposing a tool to an MCP client -- an unsupported tool is omitted here, never registered
/// and later failed at call time.
/// </summary>
public static class NamsMcpToolRegistry
{
    public static IReadOnlyList<NamsMcpToolDescriptor> AllTools { get; } =
    [
        new NamsMcpToolDescriptor { Name = "nams_recall", IsWriteTool = false, IsSupported = () => true },
        new NamsMcpToolDescriptor { Name = "nams_remember", IsWriteTool = true, IsSupported = () => true }
    ];

    /// <summary>Names of every tool this registry currently reports as supported.</summary>
    public static IReadOnlyList<string> SupportedToolNames =>
        AllTools.Where(t => t.IsSupported()).Select(t => t.Name).ToList();
}
