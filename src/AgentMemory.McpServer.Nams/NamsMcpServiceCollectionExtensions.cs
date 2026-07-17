using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using AgentMemory.McpServer.Nams.Tools;

namespace AgentMemory.McpServer.Nams;

/// <summary>
/// MCP registration for the NAMS backend's tools. Call after <c>AddMcpServer()</c> (and after
/// <c>AddNamsAgentMemory()</c>/<c>AddNamsAgentMemoryFramework()</c>, which register the
/// <see cref="AgentMemory.Nams.Recall.INamsRecallService"/>/<see cref="AgentMemory.Nams.Persistence.INamsPersistenceService"/>
/// these tools depend on). Deliberately two separate methods, not one: the plan's own rule is that write
/// tools are never automatically enabled just by enabling read tools.
/// </summary>
public static class NamsMcpServiceCollectionExtensions
{
    /// <summary>Registers the read-only <c>nams_recall</c> tool. Routine automatic recall/persistence
    /// (<c>NamsMemoryContextProvider</c>) works identically whether or not this is ever called.</summary>
    public static IMcpServerBuilder AddNamsAgentMemoryMcpTools(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithTools<NamsRecallTools>();
    }

    /// <summary>Additionally registers the write <c>nams_remember</c> tool -- a separate, explicit opt-in
    /// from <see cref="AddNamsAgentMemoryMcpTools"/>, per the plan's "write tools never automatic" rule.
    /// Calling this without first calling <see cref="AddNamsAgentMemoryMcpTools"/> registers only the write
    /// tool; call both if you want the full NAMS MCP surface.</summary>
    public static IMcpServerBuilder AddNamsAgentMemoryMcpWriteTools(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithTools<NamsPersistenceTools>();
    }
}
