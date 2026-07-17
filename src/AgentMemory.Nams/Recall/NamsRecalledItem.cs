namespace AgentMemory.Nams.Recall;

/// <summary>
/// A single piece of NAMS-hosted memory, mapped into a neutral shape.
///
/// <para><b>SECURITY:</b> this content is raw, unescaped, undelimited, and has NOT passed through any
/// admission policy. It is not safe to insert directly into a model prompt. The consuming MAF adapter
/// (Phase 6, <c>AgentMemory.AgentFramework.Nams</c>) MUST escape, delimit, and admission-check this content
/// -- using the same <c>IMemoryContextAdmissionPolicy</c>/<c>RecalledMemoryDelimiter</c>/<c>MemoryTrustLevel</c>
/// machinery the direct backend already uses (#92) -- before it ever reaches a model. <c>AgentMemory.Nams</c>
/// cannot do that gating itself (it has zero sibling-package references by design, B9); see
/// <c>docs/reviews/NAMS_Phase4_RecallAndContextMapping_PlanningAndImplementationPlan.md</c> §1.</para>
/// </summary>
public sealed record NamsRecalledItem
{
    /// <summary>The NAMS-assigned ID this item was read from (message/observation/reflection/entity ID) --
    /// used for de-duplication across categories.</summary>
    public required string SourceId { get; init; }

    public required NamsRecallCategory Category { get; init; }

    public required string Content { get; init; }

    public required NamsRecallProvenance Provenance { get; init; }

    /// <summary>The original message role (<c>user</c>/<c>assistant</c>/<c>tool</c>/<c>system</c>), populated
    /// only for <see cref="NamsRecallCategory.RecentMessage"/>/<see cref="NamsRecallCategory.RelevantMessage"/>.</summary>
    public string? Role { get; init; }

    public string? CreatedAt { get; init; }
}
