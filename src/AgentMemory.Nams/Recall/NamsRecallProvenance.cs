namespace AgentMemory.Nams.Recall;

/// <summary>
/// Deliberately mirrors <c>AgentMemory.Abstractions.Domain.MemoryTrustLevel</c> name-for-name and
/// value-for-value, defined locally so a future Phase 6 (<c>AgentMemory.AgentFramework.Nams</c>, which is
/// allowed to reference <c>AgentMemory.Abstractions</c>) can map this onto the real enum with a trivial 1:1
/// cast/switch. <c>AgentMemory.Nams</c> cannot reference <c>AgentMemory.Abstractions</c> itself (B9 -- zero
/// sibling-package references), so this is the only way to carry a trust signal out of this package.
///
/// This phase's own mapping logic (<see cref="NamsRecallService"/>) only ever emits
/// <see cref="Untrusted"/>/<see cref="UserProvided"/>/<see cref="ModelGenerated"/>/<see cref="ToolDerived"/>
/// -- never <see cref="VerifiedExternal"/> (engineering plan: "verified external data only") or
/// <see cref="ApplicationTrusted"/> (engineering plan: "no NAMS content to ApplicationTrusted without an
/// application-side verification step" -- that step is a host/Phase 6 decision, never automatic).
/// </summary>
public enum NamsRecallProvenance
{
    Untrusted = 0,
    UserProvided = 1,
    ModelGenerated = 2,
    ToolDerived = 3,
    VerifiedExternal = 4,
    ApplicationTrusted = 5
}
