using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using AgentMemory.McpServer.Prompts;
using AgentMemory.McpServer.Resources;
using AgentMemory.McpServer.Tools;

namespace AgentMemory.McpServer;

/// <summary>
/// Extension methods for adding Agent Memory MCP tools, prompts, and resources to an MCP server builder.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds all Agent Memory MCP tools to the MCP server.
    /// Call this after <c>AddMcpServer()</c> to register all memory tools.
    /// </summary>
    public static IMcpServerBuilder AddAgentMemoryMcpTools(this IMcpServerBuilder builder)
    {
        // Project the configured ServerName/ServerVersion into the MCP handshake's server identity. Runs
        // after AddMcpServer()'s defaults, so a configured McpServerOptions wins. (Uses our McpServerOptions,
        // resolved by IOptions, to set the SDK's McpServerOptions.ServerInfo.)
        builder.Services
            .AddOptions<ModelContextProtocol.Server.McpServerOptions>()
            .Configure<IOptions<McpServerOptions>>((sdk, ours) =>
            {
                var o = ours.Value;
                sdk.ServerInfo = new Implementation { Name = o.ServerName, Version = o.ServerVersion };
            });

        return builder
            .WithTools<CoreMemoryTools>()
            .WithTools<ConversationTools>()
            .WithTools<EntityTools>()
            .WithTools<ReasoningTools>()
            .WithTools<GraphQueryTools>()
            .WithTools<AdvancedMemoryTools>()
            .WithTools<MaintenanceTools>()
            .WithTools<ObservationTools>();
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
    /// Adds the four Agent Memory MCP resources (status, entities, conversations, schema).
    /// Call this after <c>AddMcpServer()</c>.
    /// </summary>
    public static IMcpServerBuilder AddAgentMemoryMcpResources(this IMcpServerBuilder builder)
    {
        return builder
            .WithResources<MemoryStatusResource>()
            .WithResources<EntityListResource>()
            .WithResources<ConversationListResource>()
            .WithResources<SchemaInfoResource>()
            .WithResources<PreferenceListResource>()
            .WithResources<ContextResource>();
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
