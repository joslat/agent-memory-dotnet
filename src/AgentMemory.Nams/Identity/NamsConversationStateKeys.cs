namespace AgentMemory.Nams.Identity;

/// <summary>
/// Versioned, namespaced key names for persisting a NAMS conversation mapping into a generic host-provided
/// key-value session/state bag (e.g. a future MAF <c>AgentSession</c>-backed
/// <see cref="INamsConversationStateStore"/> implementation, Phase 6). Never use a generic key like
/// <c>"ConversationId"</c> -- it risks colliding with unrelated state the host already stores (engineering
/// plan §7 Phase 3, "State key").
/// </summary>
public static class NamsConversationStateKeys
{
    public const string ConversationId = "AgentMemory.Nams.v1.ConversationId";
}
