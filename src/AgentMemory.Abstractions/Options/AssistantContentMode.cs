namespace AgentMemory.Abstractions.Options;

/// <summary>
/// What extraction does with what the <b>assistant</b> said.
/// </summary>
/// <remarks>
/// Measured on 2026-08-10: extraction produced only facts about the <i>user</i>. Given a turn where
/// the assistant recommended a specific film, the stored memory was <c>User asked about ...</c>,
/// <c>User is interested in ...</c> — and the recommendation itself existed nowhere in the graph.
/// The system recorded what the user <i>is and wants</i> and discarded what the assistant <i>told
/// them</i>, which makes "what did you suggest last time?" unanswerable.
/// <para>
/// This is a genuine design choice rather than a defect with one right answer, so it is a setting.
/// The two non-default modes store different things and therefore retrieve differently, and which is
/// better is an empirical question this enum exists to let us ask.
/// </para>
/// </remarks>
public enum AssistantContentMode
{
    /// <summary>
    /// Extract nothing from assistant turns. <b>The default</b>, because it is the behaviour every
    /// existing measurement was taken under, and changing it silently would make prior results
    /// incomparable without anyone noticing.
    /// </summary>
    Ignore = 0,

    /// <summary>
    /// Record what the assistant <b>did</b> — the utterance-act — with the assistant as subject:
    /// <c>assistant | recommended | Homecoming King</c>.
    /// </summary>
    /// <remarks>
    /// The act is <b>objectively true and verifiable against the transcript</b>, independent of
    /// whether the recommendation was any good. This is the episodic half of memory: not "what is
    /// true" but "what happened in this conversation".
    /// <para>
    /// Needs no schema change — the subject field already accepts <c>assistant</c>, and
    /// <c>recommends</c>, <c>told</c>, <c>provided</c> and <c>suggested</c> are already in the
    /// 110-relation vocabulary. The vocabulary was built for a semantics the prompt never requested.
    /// </para>
    /// </remarks>
    Utterance = 1,

    /// <summary>
    /// Record what the assistant <b>claimed</b> as ordinary world facts:
    /// <c>Homecoming King | is | a masterclass in storytelling</c>.
    /// </summary>
    /// <remarks>
    /// Stronger recall of subject matter, and a real hazard: an assistant statement is a
    /// <b>model-generated claim of unknown reliability</b>. Storing it as fact makes memory a
    /// repository of one model's assertions, which then decay, are re-retrieved, and are restated as
    /// truth with no record that they were ever only claimed.
    /// <para>
    /// Offered so the trade can be measured rather than argued about. <see cref="Utterance"/> is the
    /// safer default of the two, because it separates the certainty of the act from the uncertainty
    /// of the content.
    /// </para>
    /// </remarks>
    Fact = 2,
}
