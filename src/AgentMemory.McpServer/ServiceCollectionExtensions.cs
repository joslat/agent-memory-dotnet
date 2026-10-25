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

        builder
            .WithTools<CoreMemoryTools>()
            .WithTools<ConversationTools>()
            .WithTools<EntityTools>()
            .WithTools<ReasoningTools>()
            .WithTools<GraphQueryTools>()
            .WithTools<AdvancedMemoryTools>()
            .WithTools<MaintenanceTools>()
            .WithTools<HistoryTools>()
            .WithTools<ObservationTools>()
            // Inert unless AgentMemoryMcpOptions.OwnerId is set; read at request time from the resolved options.
            .WithRequestFilters(filters => filters
                .AddListToolsFilter(McpOwnerPin.ListTools)
                .AddCallToolFilter(McpOwnerPin.CallTool));

        ApplyToolFilter(builder.Services);
        return builder;
    }

    /// <summary>
    /// Removes the tools this configuration would refuse: every write tool when
    /// <see cref="AgentMemoryMcpOptions.ReadOnly"/> is set, the graph tools unless graph query is enabled and no
    /// owner is pinned, and the extraction tools when no extraction model is available.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Applied to the DI descriptors rather than enforced inside each tool, so a server does not
    /// <i>advertise</i> what it will refuse. A visible tool is one a model will call, and a runtime
    /// error teaches it nothing about the server's purpose.
    /// </para>
    /// <para>
    /// Filtering must happen while the collection is still mutable — after the host is built the tool
    /// list is fixed — so the settings are read here rather than at start.
    /// </para>
    /// </remarks>
    private static void ApplyToolFilter(IServiceCollection services)
    {
        var options = ConfiguredOptions(services);
        if (!options.ReadOnly && options.EnableGraphQuery && options.EnableExtraction
            && string.IsNullOrWhiteSpace(options.OwnerId)) return;

        // The SDK registers each tool as a singleton FACTORY, not an instance, so the name can only be
        // read by invoking it. It builds the McpServerTool from the method's attributes and resolves
        // the declaring type lazily at call time, so an empty provider is enough here and nothing that
        // touches a database or a model is constructed.
        using var empty = new ServiceCollection().BuildServiceProvider();

        var withheld = services
            .Where(descriptor =>
                descriptor.ServiceType == typeof(McpServerTool)
                && IsWithheld(ToolNameOf(descriptor, empty), options))
            .ToList();

        foreach (var descriptor in withheld) services.Remove(descriptor);
    }

    /// <summary>Whether a tool of this name is left out of a server with these options.</summary>
    /// <remarks>An unreadable (null) name is withheld only from a read-only server, as before.</remarks>
    internal static bool IsWithheld(string? toolName, AgentMemoryMcpOptions options) =>
        (options.ReadOnly && !McpToolAccess.IsReadOnly(toolName))
        || (toolName is not null && McpToolAccess.GraphTools.Contains(toolName)
            && (!options.EnableGraphQuery || !string.IsNullOrWhiteSpace(options.OwnerId)))
        || (toolName is not null && !options.EnableExtraction && McpToolAccess.ExtractionTools.Contains(toolName));

    /// <summary>
    /// The configured options, without running the options validators.
    /// </summary>
    /// <remarks>
    /// Applies the registered configure callbacks to a fresh instance rather than resolving
    /// <c>IOptions</c>, deliberately. Resolving would run <c>ValidateOnStart</c>'s validators <b>here</b>,
    /// during registration, so an out-of-range <c>DefaultConfidence</c> would surface from
    /// <c>AddAgentMemoryMcpTools</c> instead of from host startup — moving where an unrelated
    /// misconfiguration is reported, and coupling tool filtering to the validity of a setting it does
    /// not use.
    /// </remarks>
    private static AgentMemoryMcpOptions ConfiguredOptions(IServiceCollection services)
    {
        var options = new AgentMemoryMcpOptions();
        using var empty = new ServiceCollection().BuildServiceProvider();

        foreach (var descriptor in services.Where(d =>
                     d.ServiceType == typeof(IConfigureOptions<AgentMemoryMcpOptions>)))
        {
            try
            {
                var configure = descriptor.ImplementationInstance as IConfigureOptions<AgentMemoryMcpOptions>
                    ?? descriptor.ImplementationFactory?.Invoke(empty) as IConfigureOptions<AgentMemoryMcpOptions>;
                configure?.Configure(options);
            }
            catch (Exception)
            {
                // A configuration source this method cannot evaluate here -- one bound to services that
                // only exist in the real provider, say -- must not break registration for every host.
                // Skipping it means a setting might not be seen from that source; the flag on the tool
                // and the environment variable both reach this instance directly, and the alternative
                // is a host that cannot start at all.
            }
        }
        return options;
    }

    /// <summary>
    /// The advertised name of an already-registered tool descriptor.
    /// </summary>
    /// <remarks>
    /// A descriptor whose name cannot be read returns null, which
    /// <see cref="McpToolAccess.IsReadOnly"/> treats as a write — an unreadable tool is an
    /// unclassified one, and unclassified must fail closed. That matters here because the name comes
    /// from invoking a factory: if a future SDK version needed real services to build a tool, this
    /// would start throwing, and the correct response is to withhold the tool rather than to expose it.
    /// </remarks>
    private static string? ToolNameOf(ServiceDescriptor descriptor, IServiceProvider provider)
    {
        try
        {
            return (descriptor.ImplementationInstance as McpServerTool)?.ProtocolTool.Name
                ?? (descriptor.ImplementationFactory?.Invoke(provider) as McpServerTool)?.ProtocolTool.Name;
        }
        catch (Exception)
        {
            return null;
        }
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
