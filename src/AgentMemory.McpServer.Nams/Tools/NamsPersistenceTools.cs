using System.ComponentModel;
using ModelContextProtocol.Server;
using AgentMemory.Nams.Persistence;

namespace AgentMemory.McpServer.Nams.Tools;

/// <summary>
/// The one write tool this package exposes. Registered only by <c>AddNamsAgentMemoryMcpWriteTools</c> --
/// a separate, explicit opt-in from <c>AddNamsAgentMemoryMcpTools</c> (the read-only registration), matching
/// the plan's own rule that write tools are never automatically enabled.
/// </summary>
[McpServerToolType]
internal sealed class NamsPersistenceTools
{
    [McpServerTool(Name = "nams_remember"),
     Description("Persist one message to NAMS-hosted memory for a specific, already-resolved NAMS conversation.")]
    public static async Task<string> NamsRemember(
        INamsPersistenceService persistenceService,
        [Description("The resolved NAMS conversation ID -- see nams_recall's identical parameter for the same identity-boundary rationale.")]
        string namsConversationId,
        [Description("The message content to persist.")]
        string content,
        [Description("Either \"user\" or \"assistant\" -- no other role can be persisted through this tool.")]
        string role,
        CancellationToken cancellationToken = default)
    {
        var normalizedRole = role.Trim().ToLowerInvariant();
        if (normalizedRole is not ("user" or "assistant"))
        {
            return NamsMcpToolJson.Serialize(new
            {
                persisted = false,
                error = $"role must be \"user\" or \"assistant\", got \"{role}\"."
            });
        }

        var message = new NamsMessageToPersist(content);
        var result = normalizedRole == "user"
            ? await persistenceService.PersistTurnAsync(namsConversationId, [message], [], cancellationToken).ConfigureAwait(false)
            : await persistenceService.PersistTurnAsync(namsConversationId, [], [message], cancellationToken).ConfigureAwait(false);

        return NamsMcpToolJson.Serialize(new
        {
            persisted = result.Outcome == NamsPersistenceOutcome.Persisted,
            outcome = result.Outcome.ToString(),
            messageIds = result.PersistedMessageIds,
            result.FailureReason
        });
    }
}
