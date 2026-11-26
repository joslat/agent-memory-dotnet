namespace AgentMemory.Abstractions.Exceptions;

/// <summary>
/// Threat TT-13: thrown by <c>ILongTermMemoryService.SupersedeFactAsync</c> and <c>SupersedePreferenceAsync</c> when the
/// winner does not meet the bar to replace the loser (see <c>LongTermMemoryOptions.SupersessionConfidenceFloor</c> and
/// <c>RequireSourcedWinnerForSourcedLoser</c>). Nothing was changed. A host or operator that means it can lower the bar or
/// supersede through the repository.
/// </summary>
public sealed class MemorySupersessionRefusedException : MemoryException
{
    /// <summary>The memory that would have been closed.</summary>
    public string LoserId { get; }

    /// <summary>The memory that would have replaced it.</summary>
    public string WinnerId { get; }

    /// <summary>Why the winner may not replace the loser, in a sentence.</summary>
    public string Reason { get; }

    /// <summary>Initializes a new instance.</summary>
    public MemorySupersessionRefusedException(string loserId, string winnerId, string reason)
        : base($"'{winnerId}' may not supersede '{loserId}': {reason}")
    {
        LoserId = loserId;
        WinnerId = winnerId;
        Reason = reason;
    }
}
