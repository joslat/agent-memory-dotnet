using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Core.Extraction;

/// <summary>
/// Maps a reported source-turn role onto the trust level that turn's content deserves.
/// </summary>
/// <remarks>
/// <para>
/// One mapping, in one place, so the meaning of "this came from the assistant" cannot differ between
/// facts and preferences, or between the three extractors. The same mapping already exists correctly
/// in the NAMS subsystem (<c>"assistant" =&gt; ModelGenerated</c>); this is the extraction pipeline
/// finally being able to express it.
/// </para>
/// <para>
/// <b>Only <c>assistant</c> is mapped, deliberately.</b> The obvious extension — <c>user</c> →
/// <see cref="MemoryTrustLevel.UserProvided"/>, <c>tool</c> → <see cref="MemoryTrustLevel.ToolDerived"/>
/// — would be wrong here. <see cref="MemoryTrustLevel"/> is ordered so a numeric <c>&gt;=</c> means "at
/// least this trusted", and the default request trust is <see cref="MemoryTrustLevel.Untrusted"/>, so
/// those mappings would <b>raise</b> trust on hosts that never asked for any, purely on the strength of
/// a label the model wrote about itself. Admission bypass and the system-role gate both compare with
/// <c>&gt;=</c>, so that is a security-relevant direction, not a cosmetic one.
/// </para>
/// <para>
/// The <c>assistant</c> case is the one exception because it is the case this exists to record, it is
/// requested only when assistant content is actually being extracted, and mislabelling in the other
/// direction — a model-generated claim recorded as if a user had said it — is precisely the failure
/// mode being closed. A null return means "say nothing", which leaves the request's own trust level
/// applying unchanged.
/// </para>
/// </remarks>
internal static class SourceRoleTrust
{
    /// <summary>The role name extractors report for a model turn.</summary>
    internal const string AssistantRole = "assistant";

    /// <summary>
    /// The trust level implied by <paramref name="sourceRole"/>, or <see langword="null"/> when the
    /// role is absent, unrecognised, or one this mapping deliberately declines to interpret.
    /// </summary>
    internal static MemoryTrustLevel? FromSourceRole(string? sourceRole) =>
        string.Equals(sourceRole, AssistantRole, StringComparison.OrdinalIgnoreCase)
            ? MemoryTrustLevel.ModelGenerated
            : null;

    /// <summary>
    /// Combines the request-level trust with whatever the reported role implies, taking the higher of
    /// the two.
    /// </summary>
    /// <remarks>
    /// Max, not override, so this composes with the existing monotonic rule rather than competing with
    /// it: a host that declared the whole ingestion <see cref="MemoryTrustLevel.ApplicationTrusted"/>
    /// is making a statement about the ingestion, and a model's self-report must not quietly demote it.
    /// </remarks>
    internal static MemoryTrustLevel Refine(MemoryTrustLevel requestTrustLevel, string? sourceRole) =>
        FromSourceRole(sourceRole) is { } implied && implied > requestTrustLevel
            ? implied
            : requestTrustLevel;
}
