namespace AgentMemory.Abstractions.Options;

/// <summary>
/// How precisely a stored memory is bound to the conversation turn it came from.
/// </summary>
/// <remarks>
/// <para>
/// <c>EXTRACTED_FROM</c> is written per <b>ingestion batch</b>: every item extracted from a call is
/// linked to every message that call saw. Measured on the evaluation corpus, a single fact links to a
/// mean of <b>12</b> source messages and as many as 30. That is broader than the field's other
/// implementations — upstream binds a mention to one message with character offsets, Zep binds to one
/// episode — and being broader <i>and</i> coarser is the worst of both, because <b>any attribution
/// metric derived from that edge is satisfied by construction and can never fail</b>: ask "is the
/// source of this fact among its linked messages?" and the answer is yes for a batch of thirty.
/// </para>
/// <para>
/// This is opt-in and defaults to <see cref="Batch"/> for one reason: <see cref="PerItem"/> numbers the
/// turns in the extraction transcript and asks the model which one stated each item, so it changes the
/// prompt <b>and</b> the rendered conversation. Prompt bytes are fingerprinted into every measured run
/// here, and a default that moved them would silently invalidate every sealed base.
/// </para>
/// </remarks>
public enum ExtractionProvenanceMode
{
    /// <summary>
    /// Link every extracted item to every message the extraction call saw. The behaviour that shipped,
    /// and the one every recorded measurement was taken under.
    /// </summary>
    Batch = 0,

    /// <summary>
    /// Ask the model which turn stated each fact and preference, and link only that message.
    /// </summary>
    /// <remarks>
    /// Applies to facts and preferences, not entities — deliberately. A fact asserts one claim made in
    /// one statement, so binding it to thirty messages is a loss of information. An <c>Entity</c> node
    /// is a <i>merged identity</i> that legitimately appears across many turns, so narrowing it to a
    /// single turn would be wrong rather than precise. An unreported or out-of-range turn falls back to
    /// the batch links: coarse provenance is recoverable, missing provenance is not.
    /// </remarks>
    PerItem = 1,
}
