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
        // after AddMcpServer()'s defaults, so a configured AgentMemoryMcpOptions wins. (Uses our
        // AgentMemoryMcpOptions, resolved by IOptions, to set the SDK's McpServerOptions.ServerInfo.)
        builder.Services
            .AddOptions<ModelContextProtocol.Server.McpServerOptions>()
            .Configure<IOptions<AgentMemoryMcpOptions>>((sdk, ours) =>
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
        Action<AgentMemoryMcpOptions> configure)
    {
        // Stabilization fix: this registration previously had no validation at all. DefaultConfidence is
        // stamped directly onto every entity/fact/preference created via an MCP write tool; an out-of-range
        // value would corrupt the confidence semantics every ranking/dedup/decay computation elsewhere
        // relies on, with no error until that corruption was already observed downstream.
        builder.Services.AddOptions<AgentMemoryMcpOptions>()
            .Configure(configure)
            .Validate(o => o.DefaultConfidence is >= 0 and <= 1, "AgentMemoryMcpOptions.DefaultConfidence must be between 0 and 1.")
            .ValidateOnStart();
        return builder.AddAgentMemoryMcpTools();
    }
}
