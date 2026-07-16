using AgentMemory.Abstractions.Domain;

namespace AgentMemory.AgentFramework.Security;

/// <summary>
/// Input to <see cref="IMemoryContextAdmissionPolicy.Evaluate"/>: one candidate recalled-memory block,
/// before it is delimited and injected into the model context (#92 Phase 2).
/// </summary>
public sealed record MemoryAdmissionContext
{
    /// <summary>The block's category, e.g. <c>"entities"</c>, <c>"facts"</c>, <c>"graphrag"</c>.</summary>
    public required string Category { get; init; }

    /// <summary>The block's rendered text content, before delimiting/escaping.</summary>
    public required string Content { get; init; }

    /// <summary>The configured security mode governing how instruction-like content is treated.</summary>
    public MemoryContextSecurityMode Mode { get; init; } = MemoryContextSecurityMode.Permissive;

    /// <summary>
    /// This item's trust level (#92 Phase 3), read from its <c>Metadata</c> (see
    /// <c>MemoryTrustMetadataExtensions.GetTrustLevel</c>). Defaults to <see cref="MemoryTrustLevel.Untrusted"/>
    /// for content with no per-item trust signal, e.g. GraphRAG (a single opaque string, not a list of
    /// items with their own metadata).
    /// </summary>
    public MemoryTrustLevel TrustLevel { get; init; } = MemoryTrustLevel.Untrusted;

    /// <summary>
    /// The configured minimum trust level that bypasses instruction-like-content evaluation entirely
    /// (#92 Phase 3) -- see <c>ContextFormatOptions.MinimumTrustForAdmissionBypass</c>.
    /// </summary>
    public MemoryTrustLevel MinimumTrustForAdmissionBypass { get; init; } = MemoryTrustLevel.ApplicationTrusted;
}
