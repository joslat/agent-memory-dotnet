using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Core.Services;

/// <summary>
/// Security-relevant options for <see cref="MemoryContextFormatter"/> (#92 Phase 6): brings the same
/// instruction-like-content admission and trust-bypass concepts the Agent Framework adapter has had since
/// Phases 2-3 to any adapter that renders a <see cref="RecallResult"/> as plain text. Internal -- adapters
/// (e.g. <c>AgentMemory.SemanticKernel</c>) construct this from their own public-facing options type, since
/// Core cannot reference an adapter's types (wrong dependency direction).
/// </summary>
internal sealed record MemoryContextFormatterOptions
{
    /// <summary>
    /// When <see langword="false"/> (the default), instruction-like content is still included -- delimited
    /// like every other recalled block -- but is not otherwise treated specially, matching the Agent
    /// Framework adapter's <c>Permissive</c> default. When <see langword="true"/>, instruction-like content
    /// is excluded entirely unless it meets <see cref="MinimumTrustForAdmissionBypass"/>.
    /// </summary>
    public bool Strict { get; init; }

    /// <summary>
    /// The minimum <see cref="MemoryTrustLevel"/> (#92 Phase 3) that bypasses instruction-like-content
    /// evaluation entirely, regardless of <see cref="Strict"/>. Defaults to
    /// <see cref="MemoryTrustLevel.ApplicationTrusted"/> -- the highest level -- so nothing bypasses unless
    /// a host both raises an item's trust level and explicitly reaches this threshold.
    /// </summary>
    public MemoryTrustLevel MinimumTrustForAdmissionBypass { get; init; } = MemoryTrustLevel.ApplicationTrusted;

    /// <summary>
    /// The minimum <see cref="MemoryTrustLevel"/> (#92 Phase 7) a recalled conversation-history message
    /// must meet to keep a privileged role ("system"/"tool") when rendered; below it, the message's role
    /// is demoted to "user" instead (see <c>AgentMemory.Core.Security.RecalledMessageRoleGate</c>). Defaults
    /// to <see cref="MemoryTrustLevel.Untrusted"/> -- the lowest level -- so every message meets it and
    /// rendering is unchanged unless a host raises this threshold.
    /// </summary>
    public MemoryTrustLevel MinimumTrustForSystemRole { get; init; } = MemoryTrustLevel.Untrusted;
}
