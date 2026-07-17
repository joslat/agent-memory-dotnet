namespace AgentMemory.Nams.Identity;

/// <summary>
/// Identifies a NAMS conversation by the host's own (application, user, session, local-conversation) tuple
/// -- engineering plan §7 Phase 3, "Required data". Used both as the resolution request (before
/// <see cref="NamsConversationId"/> is known) and as the persisted mapping record (once it is).
/// </summary>
public sealed record NamsConversationIdentity
{
    public required string ApplicationId { get; init; }
    public required string UserId { get; init; }
    public required string SessionId { get; init; }
    public required string LocalConversationId { get; init; }
    public string? NamsConversationId { get; init; }
}
