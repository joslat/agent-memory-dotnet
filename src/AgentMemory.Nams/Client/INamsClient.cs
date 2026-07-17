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
}
