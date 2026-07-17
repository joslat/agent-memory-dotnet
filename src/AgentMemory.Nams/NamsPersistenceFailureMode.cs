namespace AgentMemory.Nams;

/// <summary>
/// How a NAMS-backed persistence call behaves when it fails (introduced now so
/// <see cref="NamsOptions"/>'s shape is stable; not yet read by any code -- the persistence path itself
/// is a later phase of the NAMS engineering plan, gated on Neo4j confirming idempotency semantics first).
/// </summary>
public enum NamsPersistenceFailureMode
{
    /// <summary>
    /// The model's response is still returned; the persistence failure is only observable via
    /// logs/metrics. Matches the existing direct-Neo4j providers' default failure behavior.
    /// </summary>
    BestEffort,

    /// <summary>
    /// The persistence exception propagates to the caller after the model response has already been
    /// produced -- callers must understand the model may already have had side effects.
    /// </summary>
    FailInvocation
}
