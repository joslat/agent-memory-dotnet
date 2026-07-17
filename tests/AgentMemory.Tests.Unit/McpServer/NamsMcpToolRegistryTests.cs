using FluentAssertions;
using AgentMemory.McpServer.Nams;

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class NamsMcpToolRegistryTests
{
    [Fact]
    public void AllTools_ListsExactlyTheTwoNamsTools()
    {
        NamsMcpToolRegistry.AllTools.Select(t => t.Name).Should().BeEquivalentTo("nams_recall", "nams_remember");
    }

    [Fact]
    public void AllTools_OnlyNamsRememberIsAWriteTool()
    {
        NamsMcpToolRegistry.AllTools.Single(t => t.Name == "nams_recall").IsWriteTool.Should().BeFalse();
        NamsMcpToolRegistry.AllTools.Single(t => t.Name == "nams_remember").IsWriteTool.Should().BeTrue();
    }

    [Fact]
    public void SupportedToolNames_DeterministicallyListsBothTools()
    {
        NamsMcpToolRegistry.SupportedToolNames.Should().BeEquivalentTo("nams_recall", "nams_remember");
    }
}
