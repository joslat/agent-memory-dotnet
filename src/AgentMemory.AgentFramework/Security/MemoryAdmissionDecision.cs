namespace AgentMemory.AgentFramework.Security;

/// <summary>
/// The outcome of <see cref="IMemoryContextAdmissionPolicy.Evaluate"/> (#92 Phase 2).
/// </summary>
public sealed record MemoryAdmissionDecision
{
    /// <summary>
    /// When <see langword="false"/>, this block is excluded entirely from the injected context. When
    /// <see langword="true"/> (the default), the block is included -- always delimited and escaped (#92
    /// Phase 1) regardless of this decision, which only controls whether it appears at all.
    /// </summary>
    public bool Include { get; init; } = true;

    /// <summary>
    /// Whether the content matched the instruction-like-content detector. Surfaced for observability even
    /// when <see cref="Include"/> is <see langword="true"/> (<see cref="MemoryContextSecurityMode.Permissive"/>).
    /// </summary>
    public bool InstructionLikeContentDetected { get; init; }

    /// <summary>A short, stable machine-readable reason when <see cref="Include"/> is <see langword="false"/>.</summary>
    public string? ExclusionReason { get; init; }
}
