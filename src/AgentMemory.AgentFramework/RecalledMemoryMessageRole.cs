namespace AgentMemory.AgentFramework;

/// <summary>
/// The chat-message role used to render a recalled-memory block into context (#92 Phase 4). Controls how
/// much authority the model affords the content -- most <c>IChatClient</c> implementations treat
/// <see cref="System"/> messages as higher-authority instructions than <see cref="User"/> messages, even
/// though both are still delimited/escaped (#92 Phase 1) and subject to admission (#92 Phase 2).
/// </summary>
public enum RecalledMemoryMessageRole
{
    /// <summary>Render as <c>ChatRole.System</c> -- the default, preserving pre-Phase-4 behavior.</summary>
    System,

    /// <summary>
    /// Render as <c>ChatRole.User</c> -- a lower-authority role for content that does not meet
    /// <see cref="ContextFormatOptions.MinimumTrustForSystemRole"/>.
    /// </summary>
    User
}
