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
        new NamsMcpToolDescriptor { Name = "nams_remember", IsWriteTool = true, IsSupported = () => true },
        new NamsMcpToolDescriptor { Name = "nams_entity_graph", IsWriteTool = false, IsSupported = () => true },
        new NamsMcpToolDescriptor { Name = "nams_expand_graph", IsWriteTool = false, IsSupported = () => true },
        new NamsMcpToolDescriptor { Name = "nams_create_entity", IsWriteTool = true, IsSupported = () => true },
        new NamsMcpToolDescriptor { Name = "nams_entity_feedback", IsWriteTool = true, IsSupported = () => true },
        new NamsMcpToolDescriptor { Name = "nams_list_reasoning_steps", IsWriteTool = false, IsSupported = () => true },
        new NamsMcpToolDescriptor { Name = "nams_reasoning_trace", IsWriteTool = false, IsSupported = () => true },
        new NamsMcpToolDescriptor { Name = "nams_entity_provenance", IsWriteTool = false, IsSupported = () => true },
        new NamsMcpToolDescriptor { Name = "nams_record_reasoning_step", IsWriteTool = true, IsSupported = () => true },
        new NamsMcpToolDescriptor { Name = "nams_record_tool_call", IsWriteTool = true, IsSupported = () => true }
    ];

    /// <summary>Names of every tool this registry currently reports as supported.</summary>
    public static IReadOnlyList<string> SupportedToolNames =>
        AllTools.Where(t => t.IsSupported()).Select(t => t.Name).ToList();
}
