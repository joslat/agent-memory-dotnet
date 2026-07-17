namespace AgentMemory.Nams.Recall;

/// <summary>
/// Result of <see cref="INamsRecallService.RecallAsync"/> -- engineering plan §7 Phase 4's "neutral recall
/// result". See <see cref="NamsRecalledItem"/> for the security warning that applies to every item here.
/// </summary>
public sealed record NamsRecallResult
{
    public IReadOnlyList<NamsRecalledItem> Items { get; init; } = [];

    /// <summary><c>true</c> if any part of the recall degraded (a retrieval failed, or the character budget
    /// truncated results) -- the caller still gets whatever succeeded, but should know it's incomplete.</summary>
    public bool IsPartial { get; init; }

    public IReadOnlyList<NamsRecallWarning> Warnings { get; init; } = [];
}
