namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// A confirmed person identity and the names it is known by: its own name, its aliases and the names it was mentioned
/// by in facts bound to it. Recall uses these to find the people a question names (identity-aware recall for people).
/// </summary>
public sealed record PersonIdentity
{
    /// <summary>The identity's entity id.</summary>
    public required string EntityId { get; init; }

    /// <summary>The names the identity is known by, at most 32.</summary>
    public required IReadOnlyList<string> Surfaces { get; init; }
}
