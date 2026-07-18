using AgentMemory.Nams.Domain;

namespace AgentMemory.Nams.Client;

/// <summary>
/// The low-level NAMS REST boundary. Application and (future) MAF provider code must never depend on an external
/// NAMS SDK type directly -- everything crossing that boundary is contained inside <c>AgentMemory.Nams</c> behind
/// this interface (engineering plan §7 Phase 2, "Rule").
///
/// Started (Phase 2) with only the 4 operations confirmed against the pinned OpenAPI snapshot
/// (<c>docs/reviews/nams-openapi-snapshot-2026-07-17.json</c>); a 5th method the plan's own example included --
/// <c>SearchMessagesAsync</c> -- was deliberately dropped at the time as unconfirmed. Both that method and
/// <see cref="ListEntitiesAsync"/> (Phase 9) have since been confirmed against the live NAMS SaaS and added --
/// see each method's own doc comment for when/why.
/// </summary>
internal interface INamsClient
{
    Task<NamsConversation> CreateConversationAsync(
        string? userId,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken);

    Task<NamsContext> GetContextAsync(
        string conversationId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<NamsMessage>> AddMessagesAsync(
        string conversationId,
        IReadOnlyList<NamsMessageInput> messages,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<NamsEntity>> SearchEntitiesAsync(
        string query,
        string? type,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists entities in the workspace with no query -- confirmed against the live NAMS REST API
    /// (<c>GET /v1/entities</c>) as part of Phase 9. Unlike <see cref="SearchEntitiesAsync"/> (which requires
    /// a non-empty query -- NAMS returns 400 for an empty one), this needs no precondition and no existing
    /// conversation, making it the one safe, side-effect-free read this client can use for a connectivity
    /// health probe.
    /// </summary>
    Task<IReadOnlyList<NamsEntity>> ListEntitiesAsync(int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Searches a conversation's own message history (<c>POST /v1/conversations/{id}/search</c>) --
    /// confirmed live as part of the Phase 10 scenario-matrix expansion. Phase 2's original design dropped
    /// this (as <c>SearchMessagesAsync</c>) for lack of confirmation at the time; it's added now, standalone,
    /// deliberately NOT wired into <c>INamsRecallService.RecallAsync</c>'s already-shipped, tested Phase 4-6
    /// behavior -- that would be a real behavior change to the automatic recall pipeline, a separate decision
    /// from adding the client capability itself.
    /// </summary>
    Task<IReadOnlyList<NamsMessage>> SearchMessagesAsync(
        string conversationId, string query, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a conversation (<c>DELETE /v1/conversations/{id}</c>) -- confirmed live as part of the Phase
    /// 10 data-lifecycle scenario. Idempotent: confirmed live that deleting an already-deleted conversation
    /// still returns success rather than a not-found error. After deletion, <see cref="GetContextAsync"/>
    /// keeps returning 200 with empty tiers (not 404) -- only fetching the conversation record itself, or
    /// adding messages to it (single or bulk), 404s. Deliberately not wired into
    /// <c>INamsPersistenceService</c>/<c>INamsConversationResolver</c> or any MCP tool -- data-lifecycle
    /// operations (deletion, export) are explicitly called out in the plan's own Phase 9 text as needing
    /// live testing and/or an organizational decision before being exposed as a routine capability; this is
    /// the low-level client operation only, added so a host that has already made that decision doesn't have
    /// to reach around this package to call the REST endpoint directly.
    /// </summary>
    Task DeleteConversationAsync(string conversationId, CancellationToken cancellationToken);

    /// <summary>
    /// Lists conversations in the workspace, newest first (<c>GET /v1/conversations</c>) -- confirmed live as
    /// part of the Phase 10e TCK Platinum probe. Returns <see cref="NamsConversationSummary"/>, a distinct shape
    /// from <see cref="CreateConversationAsync"/>'s response (no <c>workspaceId</c>; adds title/snippet/message
    /// count). Deliberately not wired into any higher-level service or MCP tool yet -- low-level client
    /// capability only, same tier as <see cref="SearchMessagesAsync"/>/<see cref="DeleteConversationAsync"/>.
    /// </summary>
    Task<IReadOnlyList<NamsConversationSummary>> ListConversationsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Lists compressed observations for a conversation (<c>GET /v1/conversations/{id}/observations</c>) --
    /// confirmed live as part of the Phase 10e TCK Platinum probe. Reuses <see cref="NamsObservation"/>, the same
    /// shape already returned inside <see cref="GetContextAsync"/>'s <c>Observations</c> tier. Observations are
    /// generated asynchronously by a server-side worker after sufficient messages accumulate -- one live probe
    /// saw a ~20-message window produce observations within ~100s, but a second attempt at the same margin
    /// (see the Phase 10f planning doc) did NOT reproduce within 150s, so this timing is NOT a reliable
    /// guarantee. Callers must not assume a call shortly after adding messages will see fresh observations, and
    /// should not build a bounded wait on any specific timing figure from this comment. Deliberately not wired
    /// into any higher-level service or MCP tool yet -- low-level client capability only.
    /// </summary>
    Task<IReadOnlyList<NamsObservation>> GetObservationsAsync(
        string conversationId, int limit, CancellationToken cancellationToken);
}
