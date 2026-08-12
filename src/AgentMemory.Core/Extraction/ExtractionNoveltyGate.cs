using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Core.Extraction;

/// <summary>
/// Decides whether a batch of turns is worth spending an extraction call on (E4).
/// </summary>
/// <remarks>
/// <para>
/// <b>This gate sits before the completion, not before the write, and that placement is the whole
/// design.</b> A gate on the write path would skip persisting a triple already in the store — which
/// sounds like the obvious saving and would silently disable two things that depend on the duplicate
/// write actually happening: S2 confidence reinforcement, where a re-asserted fact earns α, and R7's
/// <c>mention_count</c>, which is incremented by the MERGE. Corroboration <i>is</i> the repeated write.
/// Gating it away would leave both features enabled and inert.
/// </para>
/// <para>
/// The completion is also where the money is. The write is one round trip to Neo4j; the extraction is
/// a model call over the rendered transcript, and on a corpus build it is essentially the entire bill.
/// </para>
/// <para>
/// <b>Precision over recall, and the asymmetry is worse here than anywhere else in the system.</b>
/// Declining to gate costs one extraction we might not have needed. Gating a turn that did carry a
/// fact loses that fact until the user happens to say it again — the memory is simply never formed,
/// and nothing downstream can recover it or even report that it is missing. So the gate fires only on
/// turns that cannot carry a proposition at all: greetings, thanks, acknowledgement. Every genuinely
/// ambiguous token is left OUT of the vocabulary on purpose — "yes", "no" and "sure" answer questions
/// and are therefore contentful, however short they look.
/// </para>
/// <para>
/// Deterministic and vocabulary-based rather than model-scored, for the same reason 9.1's resolver is:
/// a gate that cost a completion to decide whether to spend a completion saves nothing, and one that
/// varied between runs would make every measured build unreproducible.
/// </para>
/// </remarks>
internal static class ExtractionNoveltyGate
{
    /// <summary>
    /// Tokens that cannot assert anything on their own — greetings, gratitude, acknowledgement, and
    /// the filler words that glue them together ("thanks so much", "hi there").
    /// </summary>
    /// <remarks>
    /// Deliberately absent: <c>yes</c>, <c>no</c>, <c>yep</c>, <c>nope</c>, <c>sure</c>, <c>right</c>,
    /// <c>correct</c>. Each is a complete answer to a question, and a batch may hold the answer while
    /// the question sat in the previous batch — so the question-mark check below cannot save them.
    /// </remarks>
    private static readonly HashSet<string> Uninformative = new(StringComparer.OrdinalIgnoreCase)
    {
        // acknowledgement
        "ok", "okay", "k", "kk", "got", "it", "understood", "noted", "alright", "gotcha",
        // gratitude
        "thanks", "thank", "you", "thx", "ty", "cheers", "welcome", "youre", "pleasure", "my",
        // appraisal with no object
        "great", "cool", "nice", "perfect", "awesome", "excellent", "brilliant", "good", "fine",
        // greeting and farewell
        "hi", "hello", "hey", "there", "bye", "goodbye", "morning", "afternoon", "evening", "night",
        // apology and politeness
        "sorry", "apologies", "np", "problem", "worries",
        // filler that binds the above
        "a", "the", "so", "much", "very", "really", "that", "is", "was", "and", "then", "well",
        "lol", "haha", "hah", "yay",
    };

    /// <summary>
    /// <see langword="true"/> when this batch is worth an extraction call.
    /// </summary>
    /// <remarks>
    /// Biased hard toward <see langword="true"/>: anything the vocabulary does not fully explain is
    /// treated as potentially contentful, including empty input, which is cheap to extract from and
    /// whose handling belongs to the extractors rather than here.
    /// </remarks>
    internal static bool IsWorthExtracting(IReadOnlyList<Message> messages)
    {
        if (messages is null || messages.Count == 0) return true;

        foreach (var message in messages)
        {
            var content = message.Content;
            if (string.IsNullOrWhiteSpace(content)) continue;

            // A question mark means someone asked something, and the answer -- possibly a single word
            // -- is exactly the kind of content this gate must never discard.
            if (content.Contains('?', StringComparison.Ordinal)) return true;

            if (!IsPurelyUninformative(content)) return true;
        }

        return false;
    }

    private static bool IsPurelyUninformative(string content)
    {
        var tokens = content.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var token in tokens)
        {
            var trimmed = token.Trim(Punctuation);
            if (trimmed.Length == 0) continue;

            // Any digit makes it contentful: "3" answers "how many?", and a bare number is data.
            foreach (var c in trimmed)
            {
                if (char.IsDigit(c)) return false;
            }

            if (!Uninformative.Contains(trimmed.ToLowerInvariant())
                && !Uninformative.Contains(
                    trimmed.Replace("'", string.Empty, StringComparison.Ordinal).ToLowerInvariant()))
            {
                return false;
            }
        }

        return true;
    }

    private static readonly char[] Punctuation =
        ['.', ',', '!', '?', ';', ':', '-', '—', '–', '"', '\'', '(', ')', '[', ']', '…'];
}
