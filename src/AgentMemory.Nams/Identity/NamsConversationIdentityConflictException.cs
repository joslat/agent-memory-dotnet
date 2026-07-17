namespace AgentMemory.Nams.Identity;

/// <summary>
/// Thrown when a NAMS conversation mapping already exists for a session/local-conversation key but is bound
/// to a different user or application -- refusing to let one tenant's identity resolve to another's
/// conversation (engineering plan §7 Phase 3, security invariant: "a mapping created for one user cannot be
/// used by another"). Deliberately does not derive from <c>AgentMemory.Abstractions.Exceptions.MemoryException</c>
/// -- <c>AgentMemory.Nams</c> has zero sibling-package references by design (B9).
/// </summary>
public sealed class NamsConversationIdentityConflictException : Exception
{
    public NamsConversationIdentityConflictException(string message) : base(message)
    {
    }
}
