namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// Review status of a <c>SAME_AS</c> duplicate relationship between two entities.
/// </summary>
public enum DuplicateStatus
{
    /// <summary>Flagged as a potential duplicate, awaiting review.</summary>
    Pending = 0,

    /// <summary>Reviewed and confirmed as a genuine duplicate.</summary>
    Confirmed = 1,

    /// <summary>Reviewed and rejected — the two entities are distinct.</summary>
    Rejected = 2,

    /// <summary>The duplicate has been merged into its canonical entity.</summary>
    Merged = 3
}
