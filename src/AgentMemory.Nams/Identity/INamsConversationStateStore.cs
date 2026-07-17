namespace AgentMemory.Nams.Identity;

/// <summary>
/// Durable mapping store from a (session, local-conversation) key to its NAMS conversation identity (which
/// also carries the application/user the mapping is bound to -- see <see cref="TryGetAsync"/>). This is the
/// host/Phase-6 extension point the engineering plan calls for: the default
/// registered implementation (<see cref="InMemoryNamsConversationStateStore"/>) is single-process only. A
/// horizontally-scaled host MUST supply its own durable implementation with real atomic compare-and-set
/// semantics -- register it in DI *before* calling <c>AddNamsAgentMemory</c>, since that method registers the
/// default via <c>TryAddSingleton</c> and will not override an existing registration.
/// </summary>
public interface INamsConversationStateStore
{
    /// <summary>Looked up by (session, local-conversation) only -- NOT application or user. A
    /// session/local-conversation slot is a single logical binding; its stored application and user are data
    /// the resolver checks against the requester, not independent lookup keys (otherwise a different
    /// application or user could never collide with an existing slot, and "changed application/user rejects
    /// stale mapping" -- both explicit engineering-plan test requirements -- would have nothing to reject).
    /// This assumes <c>sessionId</c> is unique across applications sharing one store instance; if two
    /// unrelated applications could coincidentally reuse the same session/local-conversation pair, an
    /// application-scoped key would be needed instead -- deferred, since the plan's own test list wants a
    /// same-session/different-application collision to be a rejectable conflict, not something the key
    /// silently partitions away.</summary>
    Task<NamsConversationIdentity?> TryGetAsync(
        string sessionId, string localConversationId,
        CancellationToken cancellationToken);

    /// <summary>Atomically records <paramref name="identity"/> (with <see cref="NamsConversationIdentity.NamsConversationId"/>
    /// populated) iff no mapping already exists for the same (session, local-conversation) key. Returns
    /// <c>false</c> if a concurrent writer won the race -- the caller must then re-read via
    /// <see cref="TryGetAsync"/> and reconcile rather than treat its own value as authoritative.</summary>
    Task<bool> TryCreateAsync(NamsConversationIdentity identity, CancellationToken cancellationToken);
}
