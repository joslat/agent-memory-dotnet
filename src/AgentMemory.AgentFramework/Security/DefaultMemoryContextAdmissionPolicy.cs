namespace AgentMemory.AgentFramework.Security;

/// <summary>
/// The default <see cref="IMemoryContextAdmissionPolicy"/> (#92 Phase 2): runs
/// <see cref="InstructionLikeContentDetector"/> against the candidate block's content.
/// <see cref="MemoryContextSecurityMode.Permissive"/> (the default) still includes flagged content --
/// every admitted block is delimited and escaped regardless (#92 Phase 1) -- while
/// <see cref="MemoryContextSecurityMode.Strict"/> excludes it entirely. Registered by
/// <c>AddAgentMemoryFramework</c>; a host replaces it by registering its own
/// <see cref="IMemoryContextAdmissionPolicy"/>.
/// </summary>
public sealed class DefaultMemoryContextAdmissionPolicy : IMemoryContextAdmissionPolicy
{
    /// <inheritdoc/>
    public MemoryAdmissionDecision Evaluate(MemoryAdmissionContext context)
    {
        if (!InstructionLikeContentDetector.IsMatch(context.Content))
            return new MemoryAdmissionDecision { Include = true };

        if (context.Mode == MemoryContextSecurityMode.Strict)
            return new MemoryAdmissionDecision
            {
                Include = false,
                InstructionLikeContentDetected = true,
                ExclusionReason = "instruction_like_content"
            };

        return new MemoryAdmissionDecision { Include = true, InstructionLikeContentDetected = true };
    }
}
