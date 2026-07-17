namespace AgentMemory.Nams.Identity;

/// <summary>
/// Resolves the NAMS conversation ID for a given application/user/session/local-conversation identity,
/// creating a new NAMS conversation on first use and reusing it afterward (engineering plan §7 Phase 3).
/// </summary>
public interface INamsConversationResolver
{
    Task<NamsConversationResolutionResult> ResolveAsync(
        NamsConversationIdentity identity, CancellationToken cancellationToken);
}
