namespace AgentMemory.Abstractions.Options;

/// <summary>
/// Whether extraction should record how long a fact is expected to hold — prospective memory.
/// </summary>
/// <remarks>
/// <para>
/// The substrate has always existed and nothing has ever written to it. <c>Fact</c> nodes carry
/// <c>valid_from</c> and <c>valid_until</c>, <c>ExtractedFact</c> exposes both, and the bitemporal
/// recall path already reads them — but no extractor populated them and no prompt mentioned validity,
/// so the columns are empty on every fact ever stored. This setting supplies the missing writer.
/// </para>
/// <para>
/// <b>Off by default, and the off-state costs nothing.</b> Under <see cref="Ignore"/> no instruction is
/// appended, so the prompt is byte-for-byte what shipped before this existed. Prompt bytes are a
/// measured variable — they are fingerprinted into every evaluation run and feed the frozen batch
/// plan's token accounting — so a default that shifted them would invalidate sealed bases silently.
/// </para>
/// <para>
/// <b>The risk that keeps it off.</b> Live recall filters on these columns, so a fabricated
/// <c>valid_until</c> does not merely add noise: it silently removes a memory from every future
/// answer. A wrongly-expiring fact is worse than one that never expires, which is why the instruction
/// tells the model to omit validity rather than guess it.
/// </para>
/// </remarks>
public enum TemporalValidityMode
{
    /// <summary>
    /// Do not ask for validity. Facts are stored with no <c>valid_from</c> or <c>valid_until</c>,
    /// exactly as before this setting existed.
    /// </summary>
    Ignore = 0,

    /// <summary>
    /// Ask for validity where the conversation states or clearly implies it, and omit it otherwise.
    /// </summary>
    Extract = 1,
}
