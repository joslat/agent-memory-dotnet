using AgentMemory.Abstractions.Domain;

namespace AgentMemory.SemanticKernel;

/// <summary>
/// Security options for <see cref="Neo4jMemoryPlugin"/>'s <c>recall</c> function (#92 Phase 6). Brings the
/// same instruction-like-content admission (#92 Phase 2) and trust-bypass (#92 Phase 3) concepts the Agent
/// Framework adapter has had since those phases to the Semantic Kernel adapter, whose <c>recall</c>
/// function previously delimited nothing and evaluated nothing -- every recalled block was rendered as
/// plain, unescaped Markdown text with no admission check at all.
/// </summary>
public sealed class MemoryRecallSecurityOptions
{
    /// <summary>
    /// Governs how instruction-like recalled content is treated. Defaults to
    /// <see cref="MemoryContextSecurityMode.Permissive"/>: such content is still included -- every
    /// recalled entity/fact/preference/GraphRAG block is delimited/escaped regardless (#92 Phase 1) -- but
    /// flagged for observability at Debug level. Set to <see cref="MemoryContextSecurityMode.Strict"/> to
    /// exclude it entirely instead.
    /// </summary>
    public MemoryContextSecurityMode SecurityMode { get; set; } = MemoryContextSecurityMode.Permissive;

    /// <summary>
    /// The minimum <see cref="MemoryTrustLevel"/> (#92 Phase 3) that bypasses instruction-like-content
    /// evaluation entirely, regardless of <see cref="SecurityMode"/>. Defaults to
    /// <see cref="MemoryTrustLevel.ApplicationTrusted"/> -- the highest level -- so nothing bypasses by
    /// default; a host must both raise an item's trust level (via <c>ExtractionRequest.TrustLevel</c> or
    /// <c>ExtractionOptions.DefaultTrustLevel</c>) and explicitly reach this threshold to get the bypass.
    /// </summary>
    public MemoryTrustLevel MinimumTrustForAdmissionBypass { get; set; } = MemoryTrustLevel.ApplicationTrusted;

    /// <summary>
    /// The minimum <see cref="MemoryTrustLevel"/> (#92 Phase 7) a recalled conversation-history message
    /// must meet to keep a privileged role ("system"/"tool") when rendered; below it, the message's role
    /// label is demoted to "user" instead. Defaults to <see cref="MemoryTrustLevel.Untrusted"/> -- the
    /// lowest level -- so every message meets it and rendering is unchanged unless a host raises this
    /// threshold. Guards against a message persisted with a privileged role via a caller-facing tool (the
    /// <c>memory_store_message</c> MCP tool, or this package's own <see cref="Neo4jMemoryPlugin.AddMessageAsync"/>)
    /// resurfacing with that role unchanged.
    /// </summary>
    public MemoryTrustLevel MinimumTrustForSystemRole { get; set; } = MemoryTrustLevel.Untrusted;
}
