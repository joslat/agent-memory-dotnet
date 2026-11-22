using System.Text.RegularExpressions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Memory;

namespace AgentMemory.Core.Resolution;

/// <summary>
/// IDENTITY-NAMESAKE-WRITER-MERGE (IKB-3). What a turn's own words say a named mention is to the speaker ("my niece
/// Valentina", "Valentina, my niece", "Valentina is my niece") and what a stored candidate is already known to be (its
/// description, aliases and subtype: "the designer on Atlas"). A writer's reference is agreement only when these do not
/// disagree: a relation the turn gives that the stored person is not known by, when the stored person is known by another,
/// is two different descriptions of a name, not one person. Deterministic, no model call; words outside the lists below
/// are never read as a relation, so an unknown word can only leave the decision as it was.
/// </summary>
internal static class IdentityRelationCue
{
    // One word each, singular, as the speaker would put it after "my". Synonyms fold to one word.
    private static readonly Dictionary<string, string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        // family
        ["mother"] = "mother", ["mom"] = "mother", ["mum"] = "mother", ["mama"] = "mother", ["mommy"] = "mother",
        ["father"] = "father", ["dad"] = "father", ["papa"] = "father", ["daddy"] = "father",
        ["sister"] = "sister", ["sis"] = "sister", ["brother"] = "brother", ["bro"] = "brother",
        ["son"] = "son", ["daughter"] = "daughter", ["niece"] = "niece", ["nephew"] = "nephew",
        ["aunt"] = "aunt", ["auntie"] = "aunt", ["uncle"] = "uncle", ["cousin"] = "cousin",
        ["grandmother"] = "grandmother", ["grandma"] = "grandmother", ["granny"] = "grandmother", ["nana"] = "grandmother",
        ["grandfather"] = "grandfather", ["grandpa"] = "grandfather", ["granddaughter"] = "granddaughter", ["grandson"] = "grandson",
        ["wife"] = "wife", ["husband"] = "husband", ["partner"] = "partner", ["girlfriend"] = "girlfriend", ["boyfriend"] = "boyfriend",
        ["fiancee"] = "fiance", ["fiancée"] = "fiance", ["fiance"] = "fiance", ["fiancé"] = "fiance",
        ["stepmother"] = "stepmother", ["stepfather"] = "stepfather", ["stepsister"] = "stepsister", ["stepbrother"] = "stepbrother",
        ["godmother"] = "godmother", ["godfather"] = "godfather", ["goddaughter"] = "goddaughter", ["godson"] = "godson",
        // people around the speaker
        ["friend"] = "friend", ["neighbour"] = "neighbor", ["neighbor"] = "neighbor", ["roommate"] = "roommate", ["flatmate"] = "roommate",
        ["colleague"] = "colleague", ["coworker"] = "colleague", ["co-worker"] = "colleague", ["teammate"] = "colleague",
        ["boss"] = "boss", ["manager"] = "manager", ["client"] = "client", ["customer"] = "client",
        ["teacher"] = "teacher", ["tutor"] = "tutor", ["student"] = "student", ["coach"] = "coach", ["mentor"] = "mentor",
        ["doctor"] = "doctor", ["dentist"] = "dentist", ["therapist"] = "therapist", ["nurse"] = "nurse", ["lawyer"] = "lawyer",
        ["landlord"] = "landlord", ["landlady"] = "landlord", ["accountant"] = "accountant",
        // work roles a description carries
        ["designer"] = "designer", ["developer"] = "developer", ["engineer"] = "engineer", ["architect"] = "architect",
        ["analyst"] = "analyst", ["consultant"] = "consultant", ["director"] = "director", ["recruiter"] = "recruiter",
        ["intern"] = "intern", ["assistant"] = "assistant", ["founder"] = "founder", ["ceo"] = "ceo", ["cto"] = "cto",
        // animals
        ["dog"] = "dog", ["puppy"] = "dog", ["cat"] = "cat", ["kitten"] = "cat", ["horse"] = "horse", ["pony"] = "horse",
        ["parrot"] = "bird", ["bird"] = "bird", ["rabbit"] = "rabbit", ["bunny"] = "rabbit", ["hamster"] = "hamster",
        ["turtle"] = "turtle", ["tortoise"] = "turtle", ["fish"] = "fish", ["goldfish"] = "fish",
    };

    private static readonly Regex Token = new(@"[\p{L}\p{M}][\p{L}\p{M}'’-]*", RegexOptions.CultureInvariant);

    /// <summary>
    /// The relations the turn gives the mention: "my|our [one word] REL NAME", "NAME, my|our REL", "NAME is my|our REL"
    /// and "NAME (my|our REL)", for each word of the mention's names that the text contains.
    /// </summary>
    public static IReadOnlySet<string> InTurn(string text, IEnumerable<string> names) => Collect(text, names, describes: false);

    /// <summary>
    /// RECALL-NAMESAKE-LEAK: what a text says a name is, whoever it belongs to: the speaker's relations of <see cref="InTurn"/>
    /// and also "X's REL NAME" ("my sister's cat Luna"), "REL [is] [also] called|named NAME" ("cat is also called Luna") and
    /// "NAME is|was the|a REL" ("Valentina Ríos is the designer on Atlas"). Read from an entity's own source messages.
    /// </summary>
    public static IReadOnlySet<string> InText(string text, IEnumerable<string> names) => Collect(text, names, describes: true);

    /// <summary>RECALL-NAMESAKE-LEAK: the relations a question names for the speaker, with or without a name ("my dog", "our niece").</summary>
    public static IReadOnlySet<string> InQuestion(string question)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(question)) return found;
        foreach (Match m in Regex.Matches(question, @"\b(?:my|our)\s+(?<a>[\p{L}-]+)(?:\s+(?<b>[\p{L}-]+))?",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)))
            Add(found, m);
        return found;
    }

    private static void Add(HashSet<string> found, Match m)
    {
        foreach (var group in new[] { "a", "b" })
            if (m.Groups[group].Success && Words.TryGetValue(m.Groups[group].Value, out var rel)) found.Add(rel);
    }

    private static IReadOnlySet<string> Collect(string text, IEnumerable<string> names, bool describes)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return found;
        var words = names.Where(n => !string.IsNullOrWhiteSpace(n))
            .SelectMany(n => Token.Matches(n).Select(m => m.Value))
            .Where(w => w.Length >= 2 && char.IsUpper(w[0]))
            .Distinct(StringComparer.Ordinal).ToList();
        foreach (var name in words)
        {
            var n = Regex.Escape(name);
            // Up to two words after "my": the relation may follow one other word ("my little niece"), so both are looked up.
            const string two = @"(?<a>[\p{L}-]+)(?:\s+(?<b>[\p{L}-]+))?";
            var patterns = new List<string>
            {
                $@"\b(?:my|our)\s+(?<a>[\p{{L}}-]+)\s+(?:(?<b>[\p{{L}}-]+)\s+)?{n}\b",
                $@"\b{n}\s*,\s*(?:my|our)\s+{two}",
                $@"\b{n}\s+(?:is|was)\s+(?:my|our)\s+{two}",
                $@"\b{n}\s*\(\s*(?:my|our)\s+{two}",
            };
            if (describes)
                patterns.AddRange(
                [
                    $@"\b[\p{{L}}]+['’]s\s+(?<a>[\p{{L}}-]+)\s+(?:(?<b>[\p{{L}}-]+)\s+)?{n}\b",
                    $@"\b(?<a>[\p{{L}}-]+)\s+(?:is\s+)?(?:also\s+)?(?:called|named)\s+{n}\b",
                    $@"\b{n}\s+(?:is|was)\s+(?:the|a|an)\s+{two}",
                ]);
            foreach (var pattern in patterns)
                foreach (Match m in Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)))
                    Add(found, m);
        }
        return found;
    }

    /// <summary>
    /// What a stored candidate is already known to be: the listed words in its description, aliases and subtype, and in what
    /// its own facts say it is (<see cref="IdentityCandidate.KnownAs"/>).
    /// </summary>
    public static IReadOnlySet<string> Known(IdentityCandidate candidate) => Known(candidate.Entity, candidate.KnownAs);

    /// <summary>The listed words in an entity's description, aliases and subtype, and in the phrases its facts give.</summary>
    public static IReadOnlySet<string> Known(Entity entity, IEnumerable<string> knownAs)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in new[] { entity.Description, entity.Subtype }.Concat(entity.Aliases).Concat(knownAs))
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            foreach (Match m in Token.Matches(text))
                if (Words.TryGetValue(m.Value, out var rel)) found.Add(rel);
        }
        return found;
    }

    /// <summary>
    /// The phrases of a candidate's facts that say what it is, for <see cref="IdentityCandidate.KnownAs"/>: "NAME is|was|works
    /// as|serves as|became ..." (the predicate and object), and "the owner has|is NAME's REL" style facts that name it as the
    /// object of the owner (the predicate). Any other fact says something it does, not what it is, and is left out.
    /// </summary>
    public static IReadOnlyList<string> FromFacts(Entity entity, IEnumerable<(string? Subject, string? Predicate, string? Object)> facts, int max = 16)
    {
        var names = new[] { entity.Name, entity.CanonicalName }.Concat(entity.Aliases)
            .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => MemoryTripleCanonicalizer.CanonicalValue(n))
            .ToHashSet(StringComparer.Ordinal);
        var phrases = new List<string>();
        foreach (var (subject, predicate, @object) in facts)
        {
            if (phrases.Count >= max) break;
            if (string.IsNullOrWhiteSpace(predicate)) continue;
            var p = predicate.Trim();
            if (subject is not null && names.Contains(MemoryTripleCanonicalizer.CanonicalValue(subject)) && Being.IsMatch(p))
                phrases.Add(Short(p + " " + (@object ?? string.Empty)));
            else if (@object is not null && names.Contains(MemoryTripleCanonicalizer.CanonicalValue(@object)) && MemoryUserNames.IsSelf(subject))
                phrases.Add(Short(p));
        }
        return phrases;
    }

    private static readonly Regex Being = new(@"^(?:is|was|works\s+as|worked\s+as|serves\s+as|became|is\s+now)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string Short(string text) => text.Length <= 256 ? text.Trim() : text[..256].Trim();

    /// <summary>
    /// True when the turn gives the mention a relation the stored entity is not known by, and the stored entity is known by
    /// another: the two do not describe one person. Nothing known on either side is no disagreement.
    /// </summary>
    public static bool Disagree(IReadOnlySet<string> turn, IReadOnlySet<string> known) =>
        turn.Count > 0 && known.Count > 0 && !turn.Overlaps(known);
}
