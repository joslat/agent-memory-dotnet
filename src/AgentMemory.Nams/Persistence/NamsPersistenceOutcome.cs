namespace AgentMemory.Nams.Persistence;

/// <summary>
/// Deliberately three-way, not a boolean success/failure -- the engineering plan §7 Phase 5 requires
/// distinguishing a definitive rejection from a genuinely ambiguous outcome: "the integration must report
/// <c>UnknownWriteOutcome</c>, not blindly retry" after a network/timeout failure, since without confirmed
/// NAMS-side idempotency (<c>strategy/NAMS/Neo4j_Questions.md</c> #15) there is no way to know whether the
/// server processed the write before the response was lost.
/// </summary>
public enum NamsPersistenceOutcome
{
    Persisted,

    /// <summary>A definitive rejection (validation/not-found/rate-limited/server-error) -- the server
    /// responded, telling us the write did not succeed.</summary>
    Failed,

    /// <summary>A network failure or timeout with no response received -- whether the server processed the
    /// write is genuinely unknown. Never automatically retried (no confirmed idempotency mechanism exists).</summary>
    UnknownWriteOutcome
}
