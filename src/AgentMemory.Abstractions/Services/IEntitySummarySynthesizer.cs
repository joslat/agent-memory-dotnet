using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Turns an entity's facts into one synthesized description (S1).
/// </summary>
/// <remarks>
/// <para>
/// A seam rather than a fixed implementation because the two reasonable answers have opposite cost
/// profiles. The shipped default composes the text deterministically — no completion, reproducible
/// across runs, and free to regenerate whenever sources move, which matters because sources move
/// constantly. A host that wants prose can supply an LLM implementation and pay for it.
/// </para>
/// <para>
/// <b>Whatever synthesises, it may only use the facts it is given.</b> The summary's fingerprint is
/// computed from exactly those facts, so a synthesizer that reached for anything else would produce
/// text whose staleness could never be detected — the one failure this design exists to rule out.
/// </para>
/// </remarks>
public interface IEntitySummarySynthesizer
{
    /// <summary>
    /// Composes a description of <paramref name="entity"/> from <paramref name="facts"/>.
    /// </summary>
    /// <returns>
    /// The summary text, or <see langword="null"/> when there is nothing worth summarising — which is
    /// a real answer, not a failure, and must not be stored as an empty summary.
    /// </returns>
    Task<string?> SynthesizeAsync(
        Entity entity,
        IReadOnlyList<Fact> facts,
        CancellationToken cancellationToken = default);
}
