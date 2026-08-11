using AgentMemory.Abstractions.Options;

namespace AgentMemory.Extraction.Llm;

/// <summary>
/// Prompt language shared by <b>every</b> extractor, so extraction semantics cannot drift between them.
/// </summary>
/// <remarks>
/// The three extractors — per-kind, unified, and multi-session unified — are three rungs of one
/// cost-optimisation ladder and are meant to be interchangeable. Each rung was written as a
/// <i>performance</i> change and each rewrote its prompt from scratch, so the semantics were never
/// carried forward: the per-kind fact extractor says <c>"skip opinions"</c> and both unified prompts
/// say nothing at all about what a fact is. The result is that a flag advertised as "1 call instead
/// of 4" silently changes <b>what a memory is</b>.
/// <para>
/// Anything here is authored once and appended by all three, so a semantic decision is made in one
/// place rather than three. The conformance test asserts every extractor honours it — a setting only
/// some extractors respect is worse than no setting, because it makes behaviour depend on a
/// performance flag.
/// </para>
/// </remarks>
internal static class ExtractionPromptSemantics
{
    /// <summary>
    /// The instruction describing what to do with assistant turns, or empty for
    /// <see cref="AssistantContentMode.Ignore"/>.
    /// </summary>
    /// <remarks>
    /// Empty for <see cref="AssistantContentMode.Ignore"/> so the resulting prompt is
    /// <b>byte-for-byte</b> what it was before this setting existed. Prompt bytes are a measured
    /// variable here — they are fingerprinted into every run and feed the frozen batch plan's token
    /// accounting — so a default that shifted them would invalidate sealed bases silently.
    /// </remarks>
    internal static string AssistantContentInstruction(AssistantContentMode mode) => mode switch
    {
        AssistantContentMode.Ignore => string.Empty,

        // Records the utterance-act. The act is objectively true and checkable against the
        // transcript; the content is the assistant's claim and is NOT asserted. Keeping those apart
        // is what stops memory becoming a repository of one model's assertions, restated later as
        // truth with no record that they were ever only claimed.
        AssistantContentMode.Utterance =>
            "\nAlso record what the assistant did in the conversation, using the assistant as the " +
            "subject: for example {\"subject\":\"assistant\",\"predicate\":\"recommended\"," +
            "\"object\":\"<what was recommended>\"}. Prefer recommended, told, provided, suggested, " +
            "explained. Record only that the assistant said it — do not treat the content as true, " +
            "and do not assert it as a fact about the world.",

        // Records the claim itself as a world fact. Stronger subject-matter recall, and a real
        // hazard, which is why it is opt-in and separately named rather than folded into Utterance.
        AssistantContentMode.Fact =>
            "\nAlso extract the information the assistant provides as ordinary facts about their " +
            "subjects, not about the user: from a recommendation of a film, extract facts about that " +
            "film. Use the assistant's statements as the source of these facts.",

        _ => string.Empty,
    };
    /// <summary>
    /// The instruction asking for temporal validity, or empty for
    /// <see cref="TemporalValidityMode.Ignore"/>.
    /// </summary>
    /// <remarks>
    /// Empty for <see cref="TemporalValidityMode.Ignore"/> for the same reason
    /// <see cref="AssistantContentInstruction"/> is: prompt bytes are fingerprinted into every run, so
    /// the off-state must not move them.
    /// <para>
    /// <b>"Omit when unbounded" is the load-bearing clause.</b> Live recall filters on these columns,
    /// so a fabricated <c>valid_until</c> silently removes a memory from every future answer — a fact
    /// that wrongly expires is worse than one that never expires. The instruction therefore asks for
    /// validity only where the conversation states or clearly implies it, and says explicitly what to
    /// do otherwise, rather than leaving the model to infer that omission is allowed.
    /// </para>
    /// </remarks>
    internal static string TemporalValidityInstruction(TemporalValidityMode mode) => mode switch
    {
        TemporalValidityMode.Extract =>
            "\nWhere the conversation states or clearly implies how long a fact holds, add ISO-8601 " +
            "\"valid_from\" and/or \"valid_until\" to that fact. Omit both when the fact has no stated " +
            "time bound - never guess an expiry, because an unbounded fact recorded as expiring is " +
            "worse than one recorded as permanent.",
        _ => string.Empty,
    };

}
