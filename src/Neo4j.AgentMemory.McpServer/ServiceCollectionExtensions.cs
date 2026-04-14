using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Neo4j.AgentMemory.McpServer.Prompts;
using Neo4j.AgentMemory.McpServer.Tools;

namespace Neo4j.AgentMemory.McpServer;

/// <summary>
/// Extension methods for adding Agent Memory MCP tools and prompts to an MCP server builder.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds all Agent Memory MCP tools to the MCP server.
    /// Call this after <c>AddMcpServer()</c> to register the 18 memory tools.
    /// </summary>
    public static IMcpServerBuilder AddAgentMemoryMcpTools(this IMcpServerBuilder builder)
    {
        return builder
            .WithTools<CoreMemoryTools>()
            .WithTools<ConversationTools>()
            .WithTools<EntityTools>()
            .WithTools<ReasoningTools>()
            .WithTools<GraphQueryTools>()
            .WithTools<AdvancedMemoryTools>();
    }

    /// <summary>
    /// Adds the three Agent Memory MCP prompts (memory-conversation, memory-reasoning, memory-review).
    /// Call this after <c>AddMcpServer()</c>.
    /// </summary>
    public static IMcpServerBuilder AddAgentMemoryMcpPrompts(this IMcpServerBuilder builder)
    {
        return builder
            .WithPrompts<MemoryConversationPrompt>()
            .WithPrompts<MemoryReasoningPrompt>()
            .WithPrompts<MemoryReviewPrompt>();
    }

    /// <summary>
    /// Adds Agent Memory MCP tools and prompts with custom options.
    /// </summary>
    public static IMcpServerBuilder AddAgentMemoryMcpTools(
        this IMcpServerBuilder builder,
        Action<McpServerOptions> configure)
    {
        builder.Services.Configure(configure);
        return builder.AddAgentMemoryMcpTools();
    }
}
