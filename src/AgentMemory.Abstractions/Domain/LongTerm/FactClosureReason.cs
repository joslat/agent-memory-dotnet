namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// Why a fact was closed by the one that replaced it, which decides the clock the closing is recorded on.
/// </summary>
public enum FactClosureReason
{
    /// <summary>
    /// The world changed ("I moved to Madrid"): the old value ends on the valid-time clock when the new one began, and
    /// stays believed for the time it held.
    /// </summary>
    Change = 0,

    /// <summary>
    /// The old value was never right ("I said Bilbao, I meant Madrid"): belief in it is withdrawn on the transaction
    /// clock, and its valid time is left alone.
    /// </summary>
    Correction = 1,
}
