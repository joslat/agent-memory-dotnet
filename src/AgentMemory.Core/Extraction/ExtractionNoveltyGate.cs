using System.Text.RegularExpressions;
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
    internal static bool IsWorthExtracting(IReadOnlyList<Message> messages) =>
        IsWorthExtracting(messages, skipUninformative: true, skipPlainQuestions: false);

    /// <summary>
    /// <see langword="true"/> when this batch is worth an extraction call, with each rule switched on or
    /// off: <paramref name="skipUninformative"/> (E4: greetings, thanks, acknowledgement) and
    /// <paramref name="skipPlainQuestions"/> (H-2: a question that cannot carry a new fact).
    /// </summary>
    internal static bool IsWorthExtracting(IReadOnlyList<Message> messages, bool skipUninformative, bool skipPlainQuestions)
    {
        if (messages is null || messages.Count == 0) return true;

        foreach (var message in messages)
        {
            var content = message.Content;
            if (string.IsNullOrWhiteSpace(content)) continue;

            if (skipPlainQuestions && IsPlainQuestion(content)) continue;
            if (!skipUninformative) return true;

            // A question mark means someone asked something, and the answer -- possibly a single word
            // -- is exactly the kind of content this gate must never discard.
            if (content.Contains('?', StringComparison.Ordinal)) return true;

            if (!IsPurelyUninformative(content)) return true;
        }

        return false;
    }

    /// <summary>Time words: a question that mentions when something happens may be stating a plan.</summary>
    private static readonly HashSet<string> TimeWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "today", "tonight", "tomorrow", "yesterday", "now", "soon", "later", "ago", "next", "last", "this",
        "morning", "afternoon", "evening", "night", "weekend", "week", "weeks", "month", "months", "year", "years",
        "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday",
        "january", "february", "march", "april", "may", "june", "july", "august", "september", "october",
        "november", "december", "christmas", "easter", "birthday", "anniversary", "since", "until",
    };

    /// <summary>Words that open a clause inside a question: whatever follows them may be a statement.</summary>
    private static readonly HashSet<string> ClauseMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "who", "whom", "whose", "which", "that", "because", "since", "where", "when", "why", "how", "what",
    };

    /// <summary>Verbs that carry a statement inside a question (“do you remember my dog is allergic…”).</summary>
    private static readonly HashSet<string> TellingVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "remember", "know", "knew", "told", "tell", "said", "say", "mention", "mentioned", "guess", "think",
        "believe", "realize", "realise", "heard",
    };

    /// <summary>What a telling verb introduces a statement with (“…remember my dog…”, “…know that…”).</summary>
    private static readonly HashSet<string> StatementStarts = new(StringComparer.OrdinalIgnoreCase)
    {
        "that", "i", "my", "our", "his", "her", "their", "we", "he", "she", "they", "me",
    };

    /// <summary>What marks something as the user's (or someone's): a state verb after it says what it is.</summary>
    /// <summary>The auxiliaries a question opens its verb with (“Where <b>does</b> my brother live?”).</summary>
    private static readonly HashSet<string> Auxiliaries = new(StringComparer.OrdinalIgnoreCase)
    {
        "do", "does", "did", "is", "are", "was", "were", "am", "has", "have", "had", "can", "could", "will", "would",
        "should", "may", "might",
    };

    private static readonly HashSet<string> Possessives = new(StringComparer.OrdinalIgnoreCase)
    {
        "my", "our", "your", "his", "her", "their",
    };

    /// <summary>A copula or state verb after a possessive: the question then says what something is.</summary>
    private static readonly HashSet<string> StateVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "is", "are", "was", "were", "has", "have", "had", "got", "gets", "loves", "likes", "lives", "works",
        "moved", "married", "engaged", "allergic", "died", "born",
    };

    /// <summary>
    /// A question that cannot carry a new fact: every sentence is a question, and there are no digits, no
    /// time words and no names (a capitalised word other than a sentence's first word and “I”); no clause inside
    /// it (“…my sister who loves gardening?”), no telling verb introducing a statement (“…remember my dog…”), no
    /// state verb after a possessive (“…my dog is allergic…”), and no “I”/“i” beyond the opening auxiliary.
    /// </summary>
    internal static bool IsPlainQuestion(string content)
    {
        var text = content.Trim();
        if (text.Length == 0 || !text.EndsWith('?')) return false;
        // Every sentence a question: nothing may end in '.' or '!' before the last '?'.
        if (text.IndexOfAny(['.', '!']) >= 0) return false;

        var position = 0;   // the token's place in its sentence
        var previous = string.Empty;
        var possessed = false;
        var afterPossessive = -1;   // words still allowed after "my …" before the question ends (-1: none open)
        foreach (var raw in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim(Punctuation);
            var at = position;
            var ends = raw.EndsWith('?');
            position = ends ? 0 : position + 1;
            if (token.Length == 0) continue;
            // Review round 2: "…for my celiac son?" describes the son. What the user owns may be named by one word
            // ("…about my family?"), or, after an auxiliary, one word and its verb ("Where does my brother live?");
            // anything longer may say something about it.
            if (afterPossessive == 0) return false;
            if (afterPossessive > 0) afterPossessive--;
            foreach (var c in token)
                if (char.IsDigit(c)) return false;
            if (TimeWords.Contains(token)) return false;
            // "I" (or a lowercase "i") only right after the opening auxiliary ("Do I…", "Am I…"): later it is the
            // user saying something about themselves ("did i tell you i got engaged?"), which may be new.
            var isI = token.Equals("i", StringComparison.OrdinalIgnoreCase) || token.StartsWith("I'", StringComparison.OrdinalIgnoreCase);
            if (isI && at > 1) return false;
            if (!isI && at > 0 && char.IsUpper(token[0])) return false;
            if (at > 0 && ClauseMarkers.Contains(token)) return false;
            // A state verb after something the user owns says what it is ("…my dog is allergic…").
            if (possessed && StateVerbs.Contains(token)) return false;
            if (Possessives.Contains(token))
            {
                possessed = true;
                afterPossessive = Auxiliaries.Contains(previous) ? 2 : 1;
            }
            if (TellingVerbs.Contains(previous) && StatementStarts.Contains(token)) return false;
            previous = token;
            if (ends) afterPossessive = -1;
        }
        return true;
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

    // ── 36.5: a turn that only asks (the generous rule, for deferring) ─────────────────────────────

    private const RegexOptions AskOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly TimeSpan AskTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly Regex Sentences = new(@"(?<=[.!?])\s+|\n+", AskOptions, AskTimeout);
    private static readonly Regex Request = new(
        @"^((can|could|would|will)\s+you|tell\s+me|show\s+me|recommend|suggest|explain)\b", AskOptions, AskTimeout);

    /// <summary>
    /// Asked to keep something is telling it ("can you remember that my sister is Ana?", "please remind me to call her"):
    /// such a turn never only asks.
    /// </summary>
    private static readonly Regex KeepThis = new(@"\b(remember|remind|note|save|don'?t\s+forget|keep\s+in\s+mind)\b", AskOptions, AskTimeout);

    /// <summary>
    /// 36.5. Whether every sentence the user said in <paramref name="turn"/> is a question or a request: the GENEROUS
    /// rule, for deferring a turn's extraction (nothing is lost, the turn is extracted with the next one). It is not
    /// <see cref="IsPlainQuestion"/>, the strict rule for skipping (a skipped fact is never formed), and it holds every
    /// plain question: a plain question only asks. False for a turn without a user sentence.
    /// </summary>
    internal static bool OnlyAsks(IReadOnlyList<Message> turn)
    {
        var sentences = turn
            .Where(message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
            .SelectMany(message => Sentences.Split(message.Content ?? string.Empty))
            .Select(sentence => sentence.Trim())
            .Where(sentence => sentence.Length > 0)
            .ToList();
        return sentences.Count > 0 && sentences.All(sentence =>
            !KeepThis.IsMatch(sentence) && (sentence.EndsWith('?') || Request.IsMatch(sentence)));
    }
}
