using FluentAssertions;
using AgentMemory.McpServer.Nams;

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class NamsMcpToolRegistryTests
{
    private static readonly string[] ReadToolNames =
    [
        "nams_recall", "nams_entity_graph", "nams_expand_graph",
        "nams_list_reasoning_steps", "nams_reasoning_trace", "nams_entity_provenance"
    ];

    private static readonly string[] WriteToolNames =
    [
        "nams_remember", "nams_create_entity", "nams_entity_feedback",
        "nams_record_reasoning_step", "nams_record_tool_call"
    ];

    [Fact]
    public void AllTools_ListsExactlyTheElevenNamsTools()
    {
        NamsMcpToolRegistry.AllTools.Select(t => t.Name).Should().BeEquivalentTo(ReadToolNames.Concat(WriteToolNames));
    }

    [Fact]
    public void AllTools_ClassifiesReadAndWriteToolsCorrectly()
    {
        foreach (var name in ReadToolNames)
            NamsMcpToolRegistry.AllTools.Single(t => t.Name == name).IsWriteTool.Should().BeFalse(because: $"{name} is a read tool");

        foreach (var name in WriteToolNames)
            NamsMcpToolRegistry.AllTools.Single(t => t.Name == name).IsWriteTool.Should().BeTrue(because: $"{name} is a write tool");
    }

    [Fact]
    public void SupportedToolNames_DeterministicallyListsAllElevenTools()
    {
        NamsMcpToolRegistry.SupportedToolNames.Should().BeEquivalentTo(ReadToolNames.Concat(WriteToolNames));
    }
}
