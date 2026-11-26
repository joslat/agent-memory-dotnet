namespace AgentMemory.AgentFramework;

/// <summary>
/// The one rule both Agent Framework providers use to turn a session's memory identity into the ids a turn reads and
/// writes under (RVW-REST-HOSTS, the review's hosts-2 and hosts-4).
/// </summary>
internal static class MemoryIdentityResolution
{
    /// <summary>
    /// The owner: the session's user id, else the ambient owner the host set around the run
    /// (<c>ownerContext.BeginOwnerScope(userId)</c>, the documented pattern). Before, a session without a user id replaced
    /// that owner with none, so recall read every owner's memory and the turn's memories were stored as shared. Inside
    /// <see cref="MemoryOwnerScopingAgent"/> the session is the authority: it clears the owner for a session that names
    /// none (a stale owner must not carry over), so there is no ambient owner to fall back to.
    /// The session: the session's id, else the agent's id qualified by the owner, else a fresh one. Before, the agent's id
    /// alone, so every user of one agent instance shared one memory session and its recent turns were replayed into each
    /// other's requests.
    /// </summary>
    public static (string SessionId, string ConversationId, string? UserId, string? ApplicationId) Resolve(
        MemoryIdentity identity, string? agentId, string? ambientOwner)
    {
        var userId = identity.UserId ?? ambientOwner;
        var sessionId = identity.SessionId
            ?? (agentId is null ? null : userId is null ? agentId : $"{agentId}:{userId}")
            ?? Guid.NewGuid().ToString("N");
        // Fall back to sessionId (not a new GUID) to preserve cross-turn correlation.
        var conversationId = identity.ConversationId ?? sessionId;
        return (sessionId, conversationId, userId, identity.ApplicationId);
    }
}
