namespace AgentMemory.Nams.Client;

/// <summary>
/// Whether a NAMS operation is safe for <see cref="NamsRetryPolicy"/> to retry automatically on a transient
/// failure (429/5xx). Mirrors the engineering plan's own retry matrix: idempotent reads -- including read-only
/// operations that happen to use a POST verb -- retry with backoff; non-idempotent writes make a single
/// attempt, since <c>strategy/NAMS/Neo4j_Questions.md</c> #12-15 (does NAMS support idempotency keys?) are
/// unanswered and retrying risks a duplicate side effect.
///
/// Introduced (Phase 10-followup) to replace a raw <c>bool isIdempotent</c> threaded through 18
/// <see cref="Neo4jNamsClientAdapter"/> call sites -- each carried its own prose comment re-deriving this same
/// general rule alongside its endpoint-specific fact. The general rule now lives here once; each call site's
/// own comment states only the fact specific to that endpoint (e.g. "PUT full-value replace" or "server-
/// enforced read-only, confirmed live").
/// </summary>
internal enum NamsRetryEligibility
{
    /// <summary>Resending this request is safe: either it's a pure read, or a full-value-replace/read-only-
    /// enforced operation where retrying reproduces the same end state.</summary>
    Idempotent,

    /// <summary>Resending this request risks a duplicate side effect (e.g. a second row created) -- never
    /// retried automatically, regardless of transient-failure classification.</summary>
    NonIdempotent
}
