using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Abstractions.Options;

/// <summary>
/// Configuration for reasoning memory.
/// </summary>
public sealed record ReasoningMemoryOptions
{
    /// <summary>Whether to generate embeddings for task descriptions automatically.</summary>
    public bool GenerateTaskEmbeddings { get; init; } = true;

    /// <summary>Whether to store tool call details.</summary>
    public bool StoreToolCalls { get; init; } = true;

    /// <summary>Maximum number of traces to retain per session.</summary>
    public int? MaxTracesPerSession { get; init; }

    /// <summary>
    /// The trust level stamped on every reasoning trace.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Traces were the one recall category with no trust level at all: <c>StartTraceAsync</c> strips
    /// any caller-supplied <c>trust_level</c> — correctly, since a caller must never self-assign a
    /// level that bypasses the admission policy — and then nothing stamped one, so every trace read
    /// back as <see cref="MemoryTrustLevel.Untrusted"/>. That is indistinguishable from "no signal
    /// recorded", which is exactly what the enum exists to avoid.
    /// </para>
    /// <para>
    /// <see cref="MemoryTrustLevel.ModelGenerated"/> is the accurate label: a trace is the agent's own
    /// record of what it did, not something a user asserted.
    /// </para>
    /// <para>
    /// <b>Safe at shipped defaults, and worth stating why.</b> <c>MinimumTrustForAdmissionBypass</c>
    /// defaults to <see cref="MemoryTrustLevel.ApplicationTrusted"/>, which
    /// <see cref="MemoryTrustLevel.ModelGenerated"/> does not reach, so no trace gains an admission
    /// bypass. <c>MinimumTrustForSystemRole</c> defaults to <see cref="MemoryTrustLevel.Untrusted"/>,
    /// which everything already met. A host that has <i>lowered</i> the bypass threshold to
    /// <see cref="MemoryTrustLevel.ModelGenerated"/> or below is the one case where this changes
    /// behaviour, and it can set this back to <see cref="MemoryTrustLevel.Untrusted"/>.
    /// </para>
    /// </remarks>
    public MemoryTrustLevel DefaultTraceTrustLevel { get; init; } = MemoryTrustLevel.ModelGenerated;
}
