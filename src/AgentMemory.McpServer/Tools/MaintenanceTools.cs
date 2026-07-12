using System.ComponentModel;
using ModelContextProtocol.Server;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.McpServer.Tools;

/// <summary>
/// Memory-hygiene maintenance tools (D5 / D7): soft-invalidate and supersede long-term memory nodes
/// <b>non-destructively</b>. Nothing is deleted — invalidated/superseded nodes leave live recall but are
/// kept and stay visible to as-of recall before the change. Owner-scoped via <c>userId</c> (R1): a scoped
/// call never touches another owner's data; a null/blank <c>userId</c> is an unscoped (admin) operation.
/// </summary>
[McpServerToolType]
internal sealed class MaintenanceTools
{
    [McpServerTool(Name = "memory_invalidate"), Description("Soft-invalidate a long-term memory node (fact, entity, or preference) by id. It drops from live recall but is kept and remains visible to as-of recall for times before invalidation (non-destructive, reversible). Owner-scoped when userId is set.")]
    public static async Task<string> MemoryInvalidate(
        ILongTermMemoryService longTerm,
        [Description("Node kind: fact, entity, or preference")] string type,
        [Description("The node id to invalidate")] string id,
        [Description("User identifier for owner scoping (optional; null/blank = unscoped/admin)")] string? userId = null,
        CancellationToken cancellationToken = default)
    {
        var scope = string.IsNullOrEmpty(userId) ? null : MemoryScope.For(userId);
        var kind = (type ?? string.Empty).Trim().ToLowerInvariant();

        bool? invalidated = kind switch
        {
            "fact" => await longTerm.InvalidateFactAsync(id, scope, cancellationToken).ConfigureAwait(false),
            "entity" => await longTerm.InvalidateEntityAsync(id, scope, cancellationToken).ConfigureAwait(false),
            "preference" or "pref" => await longTerm.InvalidatePreferenceAsync(id, scope, cancellationToken).ConfigureAwait(false),
            _ => null,
        };

        return invalidated is null
            ? ToolJsonContext.Serialize(new { error = $"unknown type '{type}' (expected fact, entity, or preference)" })
            : ToolJsonContext.Serialize(new { type = kind, id, invalidated = invalidated.Value });
    }

    [McpServerTool(Name = "memory_supersede"), Description("Supersede a loser long-term node with a winner (fact or preference): closes the loser non-destructively (it leaves live recall) and links it to the winner. Owner-scoped when userId is set — both nodes must belong to the owner.")]
    public static async Task<string> MemorySupersede(
        ILongTermMemoryService longTerm,
        [Description("Node kind: fact or preference")] string type,
        [Description("The losing node id (will be invalidated)")] string loserId,
        [Description("The winning node id (kept live)")] string winnerId,
        [Description("User identifier for owner scoping (optional; null/blank = unscoped/admin)")] string? userId = null,
        CancellationToken cancellationToken = default)
    {
        var scope = string.IsNullOrEmpty(userId) ? null : MemoryScope.For(userId);
        var kind = (type ?? string.Empty).Trim().ToLowerInvariant();

        bool? superseded = kind switch
        {
            "fact" => await longTerm.SupersedeFactAsync(loserId, winnerId, scope, cancellationToken).ConfigureAwait(false),
            "preference" or "pref" => await longTerm.SupersedePreferenceAsync(loserId, winnerId, scope, cancellationToken).ConfigureAwait(false),
            _ => null,
        };

        return superseded is null
            ? ToolJsonContext.Serialize(new { error = $"unknown type '{type}' (expected fact or preference)" })
            : ToolJsonContext.Serialize(new { type = kind, loserId, winnerId, superseded = superseded.Value });
    }
}
