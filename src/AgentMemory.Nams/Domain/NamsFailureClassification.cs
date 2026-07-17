namespace AgentMemory.Nams.Domain;

/// <summary>
/// Shared classification for <see cref="NamsOperationException.FailureKind"/>, used by every service that
/// must decide "propagate as an identity/security violation" vs. "degrade or classify the outcome instead"
/// (<c>Recall.NamsRecallService</c>, <c>Persistence.NamsPersistenceService</c>). Extracted once a third
/// duplicate of the same predicate appeared (rule of three) -- the surrounding catch-clause bodies still
/// differ per caller, only this one repeated condition is shared.
/// </summary>
internal static class NamsFailureClassification
{
    public static bool IsIdentitySecurityFailure(NamsOperationException exception) =>
        exception.FailureKind is NamsFailureKind.Authentication or NamsFailureKind.Authorization;
}
