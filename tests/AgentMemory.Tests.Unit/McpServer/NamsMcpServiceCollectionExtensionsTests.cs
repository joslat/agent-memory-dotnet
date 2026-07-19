using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using AgentMemory.McpServer.Nams;

namespace AgentMemory.Tests.Unit.McpServer;

// Regression coverage for a real gap found by review: NamsMcpServiceCollectionExtensions had zero test
// coverage of its actual DI registration -- NamsMcpToolRegistryTests.cs only checks the static descriptor
// list, never that AddNamsAgentMemoryMcpTools()/AddNamsAgentMemoryMcpWriteTools() actually wire those tool
// classes into the builder. A future edit dropping a .WithTools<T>() call (e.g. during a merge) would ship
// undetected without this.
public sealed class NamsMcpServiceCollectionExtensionsTests
{
    [Fact]
    public void AddNamsAgentMemoryMcpTools_NullBuilder_Throws()
    {
        IMcpServerBuilder builder = null!;

        var act = () => builder.AddNamsAgentMemoryMcpTools();

        act.Should().Throw<ArgumentNullException>().WithParameterName("builder");
    }

    [Fact]
    public void AddNamsAgentMemoryMcpWriteTools_NullBuilder_Throws()
    {
        IMcpServerBuilder builder = null!;

        var act = () => builder.AddNamsAgentMemoryMcpWriteTools();

        act.Should().Throw<ArgumentNullException>().WithParameterName("builder");
    }

    [Fact]
    public void AddNamsAgentMemoryMcpTools_RegistersExactlyTheSixReadTools()
    {
        var services = new ServiceCollection();

        services.AddMcpServer().AddNamsAgentMemoryMcpTools();

        using var provider = services.BuildServiceProvider();
        var toolNames = provider.GetServices<McpServerTool>().Select(t => t.ProtocolTool.Name);

        toolNames.Should().BeEquivalentTo(NamsMcpToolRegistry.AllTools.Where(t => !t.IsWriteTool).Select(t => t.Name));
    }

    [Fact]
    public void AddNamsAgentMemoryMcpWriteTools_RegistersExactlyTheFiveWriteTools()
    {
        var services = new ServiceCollection();

        services.AddMcpServer().AddNamsAgentMemoryMcpWriteTools();

        using var provider = services.BuildServiceProvider();
        var toolNames = provider.GetServices<McpServerTool>().Select(t => t.ProtocolTool.Name);

        toolNames.Should().BeEquivalentTo(NamsMcpToolRegistry.AllTools.Where(t => t.IsWriteTool).Select(t => t.Name));
    }

    [Fact]
    public void AddNamsAgentMemoryMcpTools_ThenWriteTools_RegistersAllElevenTools()
    {
        var services = new ServiceCollection();

        services.AddMcpServer().AddNamsAgentMemoryMcpTools().AddNamsAgentMemoryMcpWriteTools();

        using var provider = services.BuildServiceProvider();
        var toolNames = provider.GetServices<McpServerTool>().Select(t => t.ProtocolTool.Name);

        toolNames.Should().BeEquivalentTo(NamsMcpToolRegistry.AllTools.Select(t => t.Name));
    }
}
