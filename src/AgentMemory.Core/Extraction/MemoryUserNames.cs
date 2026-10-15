using AgentMemory.Abstractions.Domain;
using AgentMemory.Core.Memory;

namespace AgentMemory.Core.Extraction;

/// <summary>I-5: the words that mean the user, and the fact that names them.</summary>
internal static class MemoryUserNames
{
    /// <summary>Words that can mean the speaker as a subject.</summary>
    internal static readonly string[] SelfWords = ["user", "the user", "i", "me", "myself"];

    /// <summary>
    /// Words that mean the speaker as a fact's OBJECT. Not "I"/"me": as an object they are as often
    /// something else ("lives in | ME", Maine), which a fact's free-text object cannot tell apart.
    /// </summary>
    private static readonly HashSet<string> UserAsObject = new(StringComparer.Ordinal) { "user", "the user" };

    private static readonly HashSet<string> Self = new(SelfWords, StringComparer.Ordinal);

    internal static readonly string[] NamingPredicates =
        ["is named", "name is", "has name", "named", "is called", "goes by", "has the name"];

    private static readonly HashSet<string> Naming = new(NamingPredicates, StringComparer.Ordinal);

    internal static bool IsSelf(string? value) => Self.Contains(MemoryTripleCanonicalizer.CanonicalValue(value));

    /// <summary>
    /// I-7: whether a relationship endpoint is the user: any self word, at either end. Unlike a fact's
    /// object, an endpoint is only ever asked about when it is not an extracted entity, so "ME" the
    /// state would have resolved as one, and "Fabrikam EMPLOYS me" is the user.
    /// </summary>
    internal static bool MeansUserEndpoint(string? endpoint, bool source) => IsSelf(endpoint);

    internal static bool IsNamingPredicate(string? predicate) => Naming.Contains(MemoryTripleCanonicalizer.Canonical(predicate));

    internal static bool IsNamingFact(ExtractedFact fact) => IsSelf(fact.Subject) && IsNamingPredicate(fact.Predicate);

    /// <summary>
    /// Whether this slot of the fact is the user and may be stored under their name: never in the
    /// naming fact itself, never in what the assistant said about itself ("I | recommend | …"), and
    /// as an object only "user" / "the user", and only when the subject is not itself the user.
    /// </summary>
    internal static bool MeansUser(ExtractedFact fact, bool subject)
    {
        if (IsNamingFact(fact)) return false;
        if (string.Equals(fact.SourceRole, "assistant", StringComparison.OrdinalIgnoreCase)) return false;
        return subject
            ? IsSelf(fact.Subject)
            : UserAsObject.Contains(MemoryTripleCanonicalizer.CanonicalValue(fact.Object)) && !IsSelf(fact.Subject);
    }
}
