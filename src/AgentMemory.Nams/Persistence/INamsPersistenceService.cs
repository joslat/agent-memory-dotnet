namespace AgentMemory.Nams.Persistence;

/// <summary>
/// Persists one agent turn's messages to NAMS in a single bulk write. See
/// <c>docs/reviews/NAMS_Phase5_PostTurnPersistence_PlanningAndImplementationPlan.md</c> for the delivery
/// guarantee this provides (best-effort, at-most-once from the client's perspective -- no confirmed NAMS
/// idempotency exists to retry against).
/// </summary>
public interface INamsPersistenceService
{
    /// <param name="namsConversationId">The resolved NAMS conversation ID (Phase 3's job, not this one).</param>
    /// <param name="userMessages">Messages to persist with role <c>user</c>.</param>
    /// <param name="assistantMessages">Messages to persist with role <c>assistant</c>, submitted after
    /// <paramref name="userMessages"/> in the same bulk request.</param>
    Task<NamsPersistenceResult> PersistTurnAsync(
        string namsConversationId,
        IReadOnlyList<NamsMessageToPersist> userMessages,
        IReadOnlyList<NamsMessageToPersist> assistantMessages,
        CancellationToken cancellationToken);
}
