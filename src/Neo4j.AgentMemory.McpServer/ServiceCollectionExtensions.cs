using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Neo4j.AgentMemory.McpServer.Tools;

namespace Neo4j.AgentMemory.McpServer;

/// <summary>
/// Extension methods for adding Agent Memory MCP tools to an MCP server builder.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds all Agent Memory MCP tools to the MCP server.
    /// Call this after <c>AddMcpServer()</c> to register the 14 memory tools.
    /// </summary>
    public static IMcpServerBuilder AddAgentMemoryMcpTools(this IMcpServerBuilder builder)
    {
        return builder
            .WithTools<CoreMemoryTools>()
            .WithTools<ConversationTools>()
            .WithTools<EntityTools>()
            .WithTools<ReasoningTools>()
            .WithTools<GraphQueryTools>();
    }

    /// <summary>
    /// Adds Agent Memory MCP tools with custom options.
    /// </summary>
    public static IMcpServerBuilder AddAgentMemoryMcpTools(
        this IMcpServerBuilder builder,
        Action<McpServerOptions> configure)
    {
        builder.Services.Configure(configure);
        return builder.AddAgentMemoryMcpTools();
    }
}
