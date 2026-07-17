using AgentMemory.Nams.Domain;

namespace AgentMemory.Nams.Client;

/// <summary>
/// The low-level NAMS REST boundary. Application and (future) MAF provider code must never depend on an external
/// NAMS SDK type directly -- everything crossing that boundary is contained inside <c>AgentMemory.Nams</c> behind
/// this interface (engineering plan §7 Phase 2, "Rule").
///
/// Deliberately covers only the 4 operations confirmed against the pinned OpenAPI snapshot
/// (<c>docs/reviews/nams-openapi-snapshot-2026-07-17.json</c>). A 5th method the engineering plan's own example
/// included -- <c>SearchMessagesAsync</c> -- is dropped: the plan's own text already flags it as unconfirmed (no
/// distinct search/query REST operation is documented), and building against a guessed endpoint shape would be
/// worse than not building it. Add it once verified against a live sandbox (<c>strategy/NAMS/Neo4j_Questions.md</c>).
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
}
