namespace AgentMemory.Nams.Persistence;

/// <summary>Result of <see cref="INamsPersistenceService.PersistTurnAsync"/>.</summary>
public sealed record NamsPersistenceResult
{
    public required NamsPersistenceOutcome Outcome { get; init; }

    /// <summary>The NAMS-assigned IDs of the persisted messages, in submission order. Empty unless
    /// <see cref="Outcome"/> is <see cref="NamsPersistenceOutcome.Persisted"/>.</summary>
    public IReadOnlyList<string> PersistedMessageIds { get; init; } = [];

    /// <summary>A sanitized, non-fatal description of what went wrong. Populated only for
    /// <see cref="NamsPersistenceOutcome.Failed"/>/<see cref="NamsPersistenceOutcome.UnknownWriteOutcome"/>;
    /// never contains the configured API key or other secrets.</summary>
    public string? FailureReason { get; init; }
}
