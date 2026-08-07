namespace AgentMemory.Core.Memory;

/// <summary>
/// The starting set of relation names offered to extraction.
/// </summary>
/// <remarks>
/// <para>
/// Curated once rather than mined per run, for two reasons. A vocabulary that accumulated <i>during</i>
/// a run would make each call's prompt depend on which concurrent extraction finished first, so the
/// same input could produce different prompts — the precise property that made an earlier
/// Structured score sequence unattributable. And a reviewed list can be checked by a human for the
/// one mistake that matters here: silently omitting one side of an opposing pair.
/// </para>
/// <para>
/// Drawn from relations actually observed in extracted graphs. Deliberately small: it is injected
/// into every extraction prompt, and a list approaching the 421-predicates-per-700-facts figure that
/// motivated it would consume the budget it exists to improve.
/// </para>
/// <para>
/// <b>Opposing relations are both present by design.</b> <c>bought</c>/<c>sold</c> and
/// <c>likes</c>/<c>dislikes</c> are one embedding threshold apart and mean opposite things; offering
/// only one would invite the extractor to collapse them and invert facts.
/// </para>
/// </remarks>
public static class MemoryPredicateSeedVocabulary
{
    private static readonly string[] Seed =
    [
        // Existence and life events — the family that motivated this work, where one birth arrived
        // as "was born", "was born in", "were born in", "had" and "welcomed".
        "was_born", "died", "married", "divorced", "welcomed", "adopted",
        // Acquisition and disposal, both directions.
        "bought", "sold", "rented", "returned", "gave", "received", "borrowed", "lent",
        // Preference and opinion, both polarities.
        "likes", "dislikes", "prefers", "avoids", "recommends", "rated",
        // Association and identity.
        "is", "is_a", "works_at", "lives_in", "owns", "belongs_to", "knows", "related_to",
        // Activity.
        "attended", "visited", "travelled_to", "started", "finished", "cancelled",
        "planned", "scheduled", "completed", "learned", "created", "fixed",
        // State change.
        "moved_to", "changed_to", "increased_to", "decreased_to", "updated_to"
    ];

    /// <summary>A vocabulary pre-populated with the curated seed relations.</summary>
    public static MemoryPredicateVocabulary Create()
    {
        var vocabulary = new MemoryPredicateVocabulary();
        foreach (var predicate in Seed)
            vocabulary.Admit(predicate);
        return vocabulary;
    }
}
