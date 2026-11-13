using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Optional backend capability for evidence-based identity: the original text of a candidate's own source messages, so
/// a contextual decision sees both mentions instead of one. Reads only messages visible to the decision's owner scope
/// (an owner's own conversations, plus ownerless ones when shared rows are included; with no owner, ownerless ones only),
/// skips invalidated or missing messages, and applies <c>limit</c> in the query, before loading text. It returns complete
/// message text whose only origin is the message itself, never a summary. Called only for an eligible contextual
/// decision with call budget left: identity disabled, no adjudicator or a spent budget means no read. The persisted
/// decision's sources are re-validated by the backend when it is written.
/// </summary>
public interface IIdentitySourceProvider
{
    /// <summary>The visible messages among <paramref name="messageIds"/>, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<IdentitySourceFragment>> GetSourcesAsync(IReadOnlyList<string> messageIds, string? ownerId,
        bool includeShared, int limit, CancellationToken cancellationToken = default);
}
