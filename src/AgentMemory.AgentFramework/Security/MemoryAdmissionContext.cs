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
}
