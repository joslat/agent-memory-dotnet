using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;

namespace AgentMemory;

/// <summary>
/// The preset leg of the memory tiers line: "preset: Recommended", or, when Recommended fell back, "preset:
/// Conversational (Recommended needs a retrieval judge … so the Conversational preset runs)".
/// </summary>
internal sealed class PresetTierSource(MemoryPresetChoice preset) : IMemoryTierSource
{
    /// <summary>The leg's name in the tiers line.</summary>
    public const string Leg = "preset";

    public MemoryTier Describe() => new(Leg, preset.Effective.ToString(), preset.FellBack ? preset.Reason : null);
}
