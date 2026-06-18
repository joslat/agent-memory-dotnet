using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using AgentMemory.McpServer;
using McpServerOptions = AgentMemory.McpServer.McpServerOptions;

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class McpServerOptionsTests
{
    [Fact]
    public void AddAgentMemoryMcpTools_ProjectsServerNameAndVersion_IntoMcpServerInfo()
    {
        var services = new ServiceCollection();
        services.AddMcpServer().AddAgentMemoryMcpTools(o =>
        {
            o.ServerName = "custom-mem";
            o.ServerVersion = "9.9.9";
        });

        using var sp = services.BuildServiceProvider();
        var sdkOptions = sp.GetRequiredService<IOptions<ModelContextProtocol.Server.McpServerOptions>>().Value;

        sdkOptions.ServerInfo.Should().NotBeNull("ServerName/ServerVersion must be reported to MCP clients");
        sdkOptions.ServerInfo!.Name.Should().Be("custom-mem");
        sdkOptions.ServerInfo.Version.Should().Be("9.9.9");
    }

    [Fact]
    public void DefaultServerName_IsNeo4jAgentMemory()
    {
        var options = new McpServerOptions();
        options.ServerName.Should().Be("neo4j-agent-memory");
    }

    [Fact]
    public void DefaultServerVersion_Is100()
    {
        var options = new McpServerOptions();
        options.ServerVersion.Should().Be("1.0.0");
    }

    [Fact]
    public void DefaultEnableGraphQuery_IsFalse()
    {
        var options = new McpServerOptions();
        options.EnableGraphQuery.Should().BeFalse();
    }

    [Fact]
    public void DefaultSessionId_IsDefault()
    {
        var options = new McpServerOptions();
        options.DefaultSessionId.Should().Be("default");
    }

    [Fact]
    public void DefaultConfidence_Is09()
    {
        var options = new McpServerOptions();
        options.DefaultConfidence.Should().Be(0.9);
    }

    [Fact]
    public void Properties_CanBeOverridden()
    {
        var options = new McpServerOptions
        {
            ServerName = "custom",
            ServerVersion = "2.0.0",
            EnableGraphQuery = true,
            DefaultSessionId = "my-session",
            DefaultConfidence = 0.5
        };

        options.ServerName.Should().Be("custom");
        options.ServerVersion.Should().Be("2.0.0");
        options.EnableGraphQuery.Should().BeTrue();
        options.DefaultSessionId.Should().Be("my-session");
        options.DefaultConfidence.Should().Be(0.5);
    }
}
