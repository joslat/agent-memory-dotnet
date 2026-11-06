using AgentMemory.Abstractions.Domain;

// One source compiled into Core (the in-process adapters' admission) and the MCP server (its recall tools), as
// RecordFrame.cs is shared: one detection rule, one decision, on every surface that hands recalled memory to a model.
#if AGENTMEMORY_CORE
namespace AgentMemory.Core.Security;
#else
namespace AgentMemory.McpServer.Security;
#endif

/// <summary>
/// The shared admission decision (#92 Phases 2/3/6): should this recalled item be included given its trust
/// level and an instruction-like-content check? Factored out so <c>MemoryContextFormatter</c> (Core) and
/// <c>Neo4jTextSearch</c> (Semantic Kernel) share one decision instead of each re-deriving it -- callers
/// that need observability (logging) wrap this boolean decision with their own logging, since what's worth
/// logging differs per call site (e.g. category-block-level vs. per-<c>TextSearchResult</c>-level).
/// </summary>
internal static class RecalledMemoryAdmission
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="content"/> should be included: either
    /// <paramref name="trustLevel"/> meets <paramref name="minimumTrustForAdmissionBypass"/> (bypasses the
    /// check entirely), the content isn't instruction-like, or <paramref name="strict"/> is
    /// <see langword="false"/> (Permissive mode still includes flagged content).
    /// </summary>
    public static bool ShouldAdmit(
        string content, MemoryTrustLevel trustLevel, MemoryTrustLevel minimumTrustForAdmissionBypass, bool strict)
    {
        if (trustLevel >= minimumTrustForAdmissionBypass) return true;
        if (!InstructionLikeContentDetector.IsMatch(content)) return true;
        return !strict;
    }
}
