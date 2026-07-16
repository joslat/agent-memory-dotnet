using AgentMemory.Core.Services;

namespace AgentMemory.SemanticKernel;

/// <summary>
/// Maps the public, host-facing <see cref="MemoryRecallSecurityOptions"/> to the internal
/// <see cref="MemoryContextFormatterOptions"/> Core's formatter/gating logic consumes. Shared by
/// <see cref="Neo4jMemoryPlugin"/> and <see cref="Neo4jTextSearch"/> so the two adapters can't drift apart
/// on which properties get carried across -- the exact kind of wiring gap #92 Phase 7 found and fixed for
/// <see cref="Neo4jTextSearch.SearchAsync"/>.
/// </summary>
internal static class MemoryRecallSecurityOptionsExtensions
{
    public static MemoryContextFormatterOptions ToFormatterOptions(this MemoryRecallSecurityOptions security) =>
        new()
        {
            Strict = security.SecurityMode == MemoryContextSecurityMode.Strict,
            MinimumTrustForAdmissionBypass = security.MinimumTrustForAdmissionBypass,
            MinimumTrustForSystemRole = security.MinimumTrustForSystemRole
        };
}
