namespace AgentMemory.Nams.Recall;

/// <summary>
/// Retrieves NAMS-hosted memory for an already-resolved conversation (see
/// <c>AgentMemory.Nams.Identity.INamsConversationResolver</c> -- resolution is Phase 3's job, not this one)
/// and maps it into a neutral shape. See <see cref="NamsRecalledItem"/> for the security warning that
/// applies to every returned item.
/// </summary>
public interface INamsRecallService
{
    /// <param name="namsConversationId">The resolved NAMS conversation ID.</param>
    /// <param name="queryText">The current user turn's text, if any. Drives entity search; <c>null</c> or
    /// blank skips it entirely, regardless of <see cref="NamsRecallOptions.IncludeEntitySearch"/>.</param>
    Task<NamsRecallResult> RecallAsync(string namsConversationId, string? queryText, CancellationToken cancellationToken);
}
