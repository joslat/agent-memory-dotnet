namespace AgentMemory.McpServer.Nams;

/// <summary>
/// Describes one NAMS MCP tool -- engineering plan Phase 8's own capability-matrix shape
/// (<c>AgentMemoryToolDescriptor { Name, Func&lt;capabilities, bool&gt; IsSupported }</c>), scoped down since
/// NAMS itself has no partial-capability tiers today (every operation this package's tools use --
/// <c>INamsRecallService.RecallAsync</c>, <c>INamsPersistenceService.PersistTurnAsync</c> -- is unconditionally
/// available once <c>AddNamsAgentMemory()</c> is registered). <see cref="IsSupported"/> is kept as a delegate,
/// not a hardcoded <see langword="true"/>, so a future capability-tiered NAMS deployment (or the eventual
/// Phase 7 backend-neutral convergence) can plug in a real predicate without changing this shape.
/// </summary>
public sealed record NamsMcpToolDescriptor
{
    public required string Name { get; init; }

    /// <summary><see langword="true"/> if this tool is a write operation. Per the plan's own rule, write
    /// tools are never registered by <c>AddNamsAgentMemoryMcpTools</c> alone -- only
    /// <c>AddNamsAgentMemoryMcpWriteTools</c> (a separate, explicit opt-in) registers them.</summary>
    public required bool IsWriteTool { get; init; }

    public required Func<bool> IsSupported { get; init; }
}
