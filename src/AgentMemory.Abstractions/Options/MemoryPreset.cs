namespace AgentMemory.Abstractions.Options;

/// <summary>Which of the library's tested configurations a host runs.</summary>
/// <remarks>
/// A preset has a half in each package that it configures: <c>MemoryOptions</c> here, the extraction options, the
/// retrieval gate, and the Agent Framework adapter. <see cref="MemoryPresetChoice"/> is the decision a host makes once
/// and every half reads.
/// </remarks>
public enum MemoryPreset
{
    /// <summary>
    /// <see cref="MemoryOptions.CreateRecommended"/>: the store-aware writer, a judge that picks what reaches the
    /// prompt, and look-ahead recall. Needs a retrieval judge; without one the choice falls back to
    /// <see cref="Conversational"/>. The default.
    /// </summary>
    Recommended = 0,

    /// <summary><see cref="MemoryOptions.CreateConversational"/>: the extractors, and similarity picks what reaches the prompt.</summary>
    Conversational = 1,

    /// <summary>The library's low-level defaults, <c>new MemoryOptions()</c>: every capability as it ships, off.</summary>
    LowLevel = 2,
}

/// <summary>
/// The preset a host asked for, the one it runs, and why they differ when they do: Recommended without a retrieval
/// judge runs as Conversational, and says so.
/// </summary>
public sealed record MemoryPresetChoice
{
    /// <summary>What the host asked for (configuration or code). Recommended when nothing was asked.</summary>
    public required MemoryPreset Requested { get; init; }

    /// <summary>What the host runs.</summary>
    public required MemoryPreset Effective { get; init; }

    /// <summary>One sentence on why <see cref="Effective"/> is what it is; reported in the memory tiers line.</summary>
    public required string Reason { get; init; }

    /// <summary>Whether the effective preset runs the retrieval judge.</summary>
    public bool UsesJudge => Effective == MemoryPreset.Recommended;

    /// <summary>Whether the choice fell back from what was asked.</summary>
    public bool FellBack => Requested != Effective;

    /// <summary>
    /// Decides the preset: <paramref name="requested"/>, unless it is Recommended and no retrieval judge is configured,
    /// in which case Conversational. The judge is what makes Recommended worth running: without it, recall would fall
    /// to the similarity floor while the rest of the preset assumed a judge.
    /// </summary>
    public static MemoryPresetChoice Decide(MemoryPreset requested, int configuredJudges) =>
        requested == MemoryPreset.Recommended && configuredJudges <= 0
            ? new MemoryPresetChoice
            {
                Requested = requested,
                Effective = MemoryPreset.Conversational,
                Reason = "Recommended needs a retrieval judge (AgentMemory:RetrievalRouter:Judges) and none is configured, so the Conversational preset runs",
            }
            : new MemoryPresetChoice
            {
                Requested = requested,
                Effective = requested,
                Reason = requested == MemoryPreset.Recommended
                    ? $"Recommended, with {configuredJudges} retrieval judge(s)"
                    : $"{requested}, as configured",
            };

    /// <summary>The <see cref="MemoryOptions"/> half of the effective preset.</summary>
    public MemoryOptions CreateMemoryOptions() => Effective switch
    {
        MemoryPreset.Recommended => MemoryOptions.CreateRecommended(),
        MemoryPreset.Conversational => MemoryOptions.CreateConversational(),
        _ => new MemoryOptions(),
    };
}
