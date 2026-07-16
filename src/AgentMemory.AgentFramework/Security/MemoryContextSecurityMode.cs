namespace AgentMemory.AgentFramework.Security;

/// <summary>
/// Governs how <see cref="IMemoryContextAdmissionPolicy"/> treats recalled memory content that an
/// instruction-like-content detector flags (#92 Phase 2).
/// </summary>
public enum MemoryContextSecurityMode
{
    /// <summary>
    /// Instruction-like content is still included -- delimited and escaped like every other recalled block
    /// (#92 Phase 1) -- but flagged for observability. This is the default: it never drops legitimate
    /// memory content on a false positive (detection is necessarily heuristic and imprecise; see the
    /// deployment-runbook example in issue #92 -- content that merely *resembles* an instruction while
    /// being genuinely useful stored information must not be silently discarded).
    /// </summary>
    Permissive = 0,

    /// <summary>
    /// Instruction-like content is excluded entirely from the injected context rather than merely quoted.
    /// A stronger posture for hosts willing to accept the risk of dropping some legitimate content that
    /// happens to resemble an instruction.
    /// </summary>
    Strict = 1
}
