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
    /// <summary>
    /// Asks for the source turn's role on every fact and preference. Appended to — and only to — the
    /// non-<see cref="AssistantContentMode.Ignore"/> instructions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Trust is stamped once per extraction request, so the moment assistant content is extracted a
    /// single batch contains both a user's claims and the model's own, recorded identically. This is
    /// the only signal that separates them without a second extraction call, and cost per ingested
    /// conversation is a headline number here — a role-split extraction would double it.
    /// </para>
    /// <para>
    /// It is attached <b>here</b>, rather than added to the base prompt, so that the
    /// <see cref="AssistantContentMode.Ignore"/> prompt stays byte-for-byte what it was. That is not
    /// tidiness: prompt bytes are fingerprinted into every measured run, and at <c>Ignore</c> nothing
    /// assistant-derived is extracted at all, so the field would have exactly one possible value and
    /// would buy nothing for the base it invalidated.
    /// </para>
    /// </remarks>
    internal const string SourceRoleInstruction =
        "\nOn every fact and preference, add \"source_role\":\"user\" or \"source_role\":\"assistant\" " +
        "to record which turn it came from. Use \"assistant\" only when the assistant's own turn is " +
        "what states it; if the user said it, or if you are unsure, use \"user\".";

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
            "and do not assert it as a fact about the world."
            + SourceRoleInstruction,

        // Records the claim itself as a world fact. Stronger subject-matter recall, and a real
        // hazard, which is why it is opt-in and separately named rather than folded into Utterance.
        AssistantContentMode.Fact =>
            "\nAlso extract the information the assistant provides as ordinary facts about their " +
            "subjects, not about the user: from a recommendation of a film, extract facts about that " +
            "film. Use the assistant's statements as the source of these facts."
            + SourceRoleInstruction,

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
    /// <summary>
    /// The instruction asking which numbered turn stated each item, or empty for
    /// <see cref="ExtractionProvenanceMode.Batch"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only meaningful alongside a numbered transcript — the two ship together, because an instruction
    /// naming turn numbers against an unnumbered transcript asks for something the model can only
    /// invent.
    /// </para>
    /// <para>
    /// <b>"Omit when unsure" is the load-bearing clause</b>, for the same reason it is on temporal
    /// validity. A resolved turn <i>replaces</i> the batch links, so a guessed number does not merely
    /// add noise — it discards the true source and substitutes a wrong one, and the result is
    /// indistinguishable afterwards from precise attribution.
    /// </para>
    /// </remarks>
    internal static string ProvenanceInstruction(ExtractionProvenanceMode mode) => mode switch
    {
        ExtractionProvenanceMode.PerItem =>
            "\nEach turn in the conversation is numbered as [N]. On every fact and preference, add " +
            "\"source_turn\":N naming the single turn that states it. Use the turn where the " +
            "information is actually given, not one that merely refers to it, and omit the field " +
            "entirely when no single turn states it or you are unsure - never guess a number.",
        _ => string.Empty,
    };

    /// <summary>
    /// Asks for validity windows, <b>anchored to the turn that states them</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The anchor sentence is not decoration.</b> Without it the model has no stated reference
    /// for a relative expression and dates it from its own notion of now. Measured 2026-09-16 on
    /// the prospective corpus: 41 extracted windows, <b>0 before today, 26 exactly today, 15 in the
    /// future</b>, against conversations anchored in the past. Every window was therefore useless to
    /// a point-in-time read, and firing -- whose query asks for validity opening around the
    /// question's instant -- could not match one.
    /// </para>
    /// <para>
    /// The reference was always available: the conversation builder prefixes every turn with its
    /// own ISO-8601 timestamp, unconditionally. The prompt simply never pointed at it.
    /// </para>
    /// <para>
    /// <b>Safe to change.</b> Prompt bytes are fingerprinted into every measured run, but this
    /// string appears only under <see cref="TemporalValidityMode.Extract"/> -- which no sealed
    /// measurement has ever used, because nothing could set it until 2026-09-16. No recorded
    /// number moves.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Tells the model that a stated identity is an alias, and where to put it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The schema has always REQUIRED an <c>aliases</c> field on every entity, and the multi-session
    /// prompt's only example of it is <c>"aliases":[]</c> — sitting directly above "use empty arrays
    /// when a category has no supported memory". A required field whose sole example is empty, with
    /// no definition anywhere in the prompt, is an instruction to emit nothing, and that is what
    /// every run this project has made received.
    /// </para>
    /// <para>
    /// <b>Capture, not inference.</b> The instruction deliberately names the sentence pattern rather
    /// than asking the model to judge whether two things are "the same": every alias case in the
    /// measured corpus states itself outright — <i>"The new flat is the place on Ferrow Row"</i> —
    /// and a model asked to infer identity from similarity would merge distinct referents, which is
    /// the over-merge failure the E-1 Goodhart guard watches for. Naming the pattern keeps the
    /// evidence bar at "the conversation said so".
    /// </para>
    /// <para>
    /// It lives here, not in one extractor, because this is the fifth time a semantic has been
    /// carried by one rung and not its twin. The per-kind entity extractor already says "include
    /// aliases when mentioned"; the batch rung the benchmark runs never did.
    /// </para>
    /// </remarks>
    internal static string IdentityAliasInstruction(bool capture) => capture
        ? "\nWhen a turn STATES that two names refer to one thing - \"the new flat is the place on "
          + "Ferrow Row\", \"head office, i.e. the Calderwick office\" - emit ONE entity and put the "
          + "other name in its \"aliases\". Record an alias ONLY when the conversation says the two "
          + "are the same; never merge two names because they look or sound similar, and never "
          + "because they appear together. Two distinct things recorded as one cannot be separated "
          + "again, while two names left apart can still be joined later."
        : string.Empty;

    internal static string TemporalValidityInstruction(TemporalValidityMode mode) => mode switch
    {
        TemporalValidityMode.Extract =>
            "\nWhere the conversation states or clearly implies how long a fact holds, add ISO-8601 " +
            "\"valid_from\" and/or \"valid_until\" to that fact. RESOLVE EVERY RELATIVE " +
            "EXPRESSION AGAINST THE TIMESTAMP OF THE TURN THAT STATES IT: each turn is " +
            "prefixed with its own ISO-8601 time, so \"from next Monday\" means the Monday " +
            "after THAT instant, never the Monday after today. A conversation from last " +
            "spring records last spring's dates. Omit both when the fact has no stated " +
            "time bound - never guess an expiry, because an unbounded fact recorded as expiring is " +
            "worse than one recorded as permanent.",
        _ => string.Empty,
    };


    /// <summary>
    /// Tells the model that the fenced earlier turns are background and not extraction targets (E2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Appended only when a window actually carries context, so a prompt without context is
    /// <b>byte-for-byte</b> what it was before E2 existed. Prompt bytes are fingerprinted into every
    /// measured run in this track; an instruction that appeared unconditionally would invalidate every
    /// sealed base to say something about turns that were not supplied.
    /// </para>
    /// <para>
    /// Authored here rather than per extractor for the usual reason: an instruction only some
    /// extractors carry makes what a memory is depend on which performance flag is set.
    /// </para>
    /// </remarks>
    internal const string ExtractionContextInstruction =
        "\nThe transcript begins with earlier turns marked as EARLIER CONVERSATION. Read them only to " +
        "resolve references — who \"she\" is, where \"there\" is, what \"it\" refers to. Do NOT extract " +
        "any memory whose statement lives in those turns; extract only from the turns after the end " +
        "marker, even if an earlier turn states something you would otherwise record.";
}
