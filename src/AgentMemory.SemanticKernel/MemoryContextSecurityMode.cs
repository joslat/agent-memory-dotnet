namespace AgentMemory.SemanticKernel;

/// <summary>
/// Governs how <see cref="Neo4jMemoryPlugin"/>'s <c>recall</c> function treats recalled memory content
/// flagged as instruction-like (#92 Phase 6). Mirrors <c>AgentMemory.AgentFramework.Security.MemoryContextSecurityMode</c>'s
/// semantics for the Agent Framework adapter; kept as a separate type (not shared across adapter packages)
/// since neither adapter references the other.
/// </summary>
public enum MemoryContextSecurityMode
{
    /// <summary>
    /// Instruction-like content is still included -- delimited like every other recalled block -- but is
    /// not otherwise treated specially. This is the default: it never drops legitimate memory content on a
    /// false positive (detection is necessarily heuristic and imprecise).
    /// </summary>
    Permissive = 0,

    /// <summary>
    /// Instruction-like content is excluded entirely from the formatted recall output rather than merely
    /// included. A stronger posture for hosts willing to accept the risk of dropping some legitimate
    /// content that happens to resemble an instruction.
    /// </summary>
    Strict = 1
}
