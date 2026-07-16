using AgentMemory.Core.Security;

namespace AgentMemory.AgentFramework.Security;

/// <summary>
/// The default <see cref="IMemoryContextAdmissionPolicy"/> (#92 Phase 2): runs
/// <see cref="InstructionLikeContentDetector"/> (relocated to <c>AgentMemory.Core.Security</c> in Phase 6
/// so the Semantic Kernel adapter can share it) against the candidate block's content.
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
        // #92 Phase 3: an item trusted at or above the configured threshold bypasses instruction-like
        // detection entirely -- this is the mechanism for "applications can explicitly mark controlled
        // sources as trusted." Untouched by default: MinimumTrustForAdmissionBypass defaults to
        // ApplicationTrusted (the highest level), so nothing bypasses unless a host both raises an item's
        // trust level AND explicitly marks it that trusted -- Phase 2's behavior is unchanged by default.
        if (context.TrustLevel >= context.MinimumTrustForAdmissionBypass)
            return new MemoryAdmissionDecision { Include = true };

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
