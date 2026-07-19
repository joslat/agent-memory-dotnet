using System.ComponentModel;
using ModelContextProtocol.Server;
using AgentMemory.Nams.Client;

namespace AgentMemory.McpServer.Nams.Tools;

/// <summary>
/// Read-only NAMS entity-graph tools. Registered by <c>AddNamsAgentMemoryMcpTools</c>. Unlike
/// <see cref="NamsRecallTools"/>, these resolve <see cref="INamsClient"/> directly rather than through a
/// dedicated public service -- see
/// docs/reviews/NAMS_McpEntityReasoningTools_PlanningAndImplementationPlan.md for why a service layer isn't
/// warranted for this operation tier.
/// </summary>
[McpServerToolType]
internal sealed class NamsEntityReadTools
{
    [McpServerTool(Name = "nams_entity_graph"),
     Description("Get the full workspace entity graph: every extracted entity and the relationships between " +
                  "them. Workspace-wide, not scoped to a single conversation -- matches how NAMS's own " +
                  "entity search/list operations are already workspace-scoped.")]
    public static async Task<string> NamsEntityGraph(
        INamsClient client,
        CancellationToken cancellationToken = default)
    {
        var graph = await client.GetEntityGraphAsync(cancellationToken).ConfigureAwait(false);
        return NamsMcpToolJson.Serialize(new
        {
            nodes = graph.Nodes.Select(n => new { n.Id, n.Name, n.Type, n.Description, n.Confidence }),
            edges = graph.Edges.Select(e => new { e.Id, e.SourceId, e.TargetId, e.Type, e.Confidence })
        });
    }

    [McpServerTool(Name = "nams_expand_graph"),
     Description("Expand a single graph node's 1-hop neighborhood. Can surface non-entity nodes (e.g. a " +
                  "Message node), which is why nodes here carry generic labels rather than fixed entity " +
                  "fields; properties are omitted for any node labeled \"Message\", \"Observation\", or " +
                  "\"Reflection\" since those can carry raw, unvetted conversation content that must not flow " +
                  "back to a model unescaped.")]
    public static async Task<string> NamsExpandGraph(
        INamsClient client,
        [Description("The node id to expand from.")] string nodeId,
        [Description("Optional comma-separated node ids the caller already has, to elide them from the response.")]
        string? loadedIds,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
            return NamsMcpToolJson.Serialize(new { error = "nodeId is required." });

        var loadedIdList = string.IsNullOrWhiteSpace(loadedIds)
            ? []
            : loadedIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var expansion = await client.ExpandGraphAsync(nodeId, loadedIdList, cancellationToken).ConfigureAwait(false);
        // SECURITY: `ExpandGraphAsync` can surface non-Entity nodes -- confirmed live (Phase 10e) that a
        // "Message" node's properties can carry raw, unescaped conversation content, the same class of
        // untrusted data NamsRecalledItem's own SECURITY doc comment (src/AgentMemory.Nams/Recall/
        // NamsRecalledItem.cs) says must never reach a model without admission/delimiting. This tool has no
        // access to that gating machinery (AgentMemory.McpServer.Nams doesn't reference Core/AgentFramework),
        // so it elides properties for any node labeled "Message", "Observation", or "Reflection" -- the same
        // raw-content risk class NamsRecallCategory enumerates for the recall pipeline (its RecentMessage/
        // RelevantMessage/Observation/Reflection members), even though the graph's own node-label strings
        // don't literally match those C# enum member names -- rather than passing them through unfiltered --
        // id/labels alone are harmless metadata. Only "Message" has been live-confirmed as an actual
        // expand-graph label so far; "Observation"/"Reflection" are included defensively (same content-risk
        // class, not yet observed on this specific endpoint) -- this is deliberately a denylist, not an
        // allowlist, since NAMS's exact "Entity" label string was never live-confirmed either, so allowlisting
        // on it risked over-eliding legitimate entity data.
        var messageLikeLabels = new[] { "Message", "Observation", "Reflection" };
        return NamsMcpToolJson.Serialize(new
        {
            nodes = expansion.Nodes.Select(n => new
            {
                n.Id,
                n.Labels,
                properties = n.Labels.Any(messageLikeLabels.Contains) ? null : n.Properties
            }),
            edges = expansion.Edges.Select(e => new { e.Id, e.SourceId, e.TargetId, e.Type, e.Confidence }),
            truncated = expansion.Truncated is null
                ? null
                : new { expansion.Truncated.NodeId, expansion.Truncated.Shown, expansion.Truncated.Total }
        });
    }
}
