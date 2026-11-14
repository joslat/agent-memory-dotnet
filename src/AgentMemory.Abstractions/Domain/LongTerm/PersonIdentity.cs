namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// A person and the names it is known by: its own name, its aliases and the names it was mentioned by in facts bound to
/// it. Recall uses these to find the people a question names (identity-aware recall for people). Every live person is
/// listed: one without facts bound through a decided identity (an open proposal's observation, or a person written
/// outside identity) is unconfirmed, so its name makes a namesake ambiguous, but it is never expanded.
/// </summary>
public sealed record PersonIdentity
{
    /// <summary>The identity's entity id.</summary>
    public required string EntityId { get; init; }

    /// <summary>The names the identity is known by, at most 32.</summary>
    public required IReadOnlyList<string> Surfaces { get; init; }

    /// <summary>
    /// True when facts are bound to this person through a decided identity; false for an open proposal's observation (it is
    /// not yet known who this is) and for a person with no bound facts.
    /// </summary>
    public bool Confirmed { get; init; } = true;

    /// <summary>
    /// False when the person has more names than were read (<see cref="Surfaces"/> is bounded): any name could then be
    /// theirs too, so recall names nobody rather than guess.
    /// </summary>
    public bool Complete { get; init; } = true;
}
