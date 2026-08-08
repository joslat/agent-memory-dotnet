namespace AgentMemory.Core.Memory;

/// <summary>
/// The reviewed <c>canonical → surface forms</c> table behind <see cref="MemoryRelationLexicon"/>.
/// </summary>
/// <remarks>
/// <para>
/// Authored in one direction only. The lookup index is derived at load, so the extraction vocabulary
/// and the query lexicon cannot drift apart.
/// </para>
/// <para>
/// Canonical names are written in stored <c>predicate_key</c> form - lowercase, separators folded to
/// single spaces - because resolution that produced keys the graph cannot match would be worthless.
/// </para>
/// <para>
/// <b>Grounded in measurement, not intuition.</b> The canonical set covers the predicates actually
/// observed in an extracted graph (4,659 facts over 10 owners), including the frequent forms the
/// extractor invented outside the offered vocabulary - <c>wants</c>, <c>is interested in</c>,
/// <c>uses</c>, <c>asked about</c>, <c>requested</c>, <c>considered</c>. Inflectional variants are the
/// bulk of that tail (<c>plans</c> beside <c>planned</c>, <c>was</c> beside <c>is</c>), and they are
/// resolved here rather than by enlarging the write-side vocabulary.
/// </para>
/// <para>
/// <b>Deliberately excluded:</b> genuinely ambiguous forms. <c>got</c> could be <c>bought</c> or
/// <c>received</c>; a form claimed by two relations is dropped at load and reported, so an authoring
/// mistake is visible rather than silently resolved in table order.
/// </para>
/// <para>
/// This is a starter table sized for the measured corpus. Growing it to a reviewed 200-400 entries
/// from schema.org Actions, Wikidata properties, PARAREL and Rel2Text is a separate, judged task.
/// </para>
/// </remarks>
internal static class MemoryRelationSeedTable
{
    internal static IReadOnlyDictionary<string, string[]> Table { get; } =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            // ── Acquisition and disposal, both directions kept apart ──────────────
            ["bought"] = ["buy", "buys", "buying", "purchase", "purchased", "purchases", "purchasing", "acquired", "ordered"],
            ["sold"] = ["sell", "sells", "selling"],
            ["rented"] = ["rent", "rents", "renting", "leased", "leases"],
            ["returned"] = ["return", "returns", "returning"],
            ["gave"] = ["give", "gives", "giving", "gifted", "donated"],
            ["received"] = ["receive", "receives", "receiving"],
            ["borrowed"] = ["borrow", "borrows", "borrowing"],
            ["lent"] = ["lend", "lends", "lending", "loaned"],

            // ── Preference and opinion, both polarities ───────────────────────────
            ["likes"] = ["like", "liked", "liking", "enjoys", "enjoy", "enjoyed", "loves", "love", "loved"],
            ["dislikes"] = ["dislike", "disliked", "hates", "hate", "hated"],
            ["prefers"] = ["prefer", "preferred", "preferring"],
            ["avoids"] = ["avoid", "avoided", "avoiding"],
            ["recommends"] = ["recommend", "recommended", "recommending"],
            ["rated"] = ["rate", "rates", "rating", "reviewed", "reviews", "review"],

            // ── Identity and state: the backbone schema.org Actions cannot express ─
            ["is"] = ["was", "are", "were", "be", "been", "am"],
            ["is a"] = ["was a", "is an", "was an"],
            ["owns"] = ["own", "owned", "owning", "possesses", "possess"],
            ["has"] = ["have", "had", "having"],
            ["works at"] = ["work at", "works for", "worked at", "employed at", "employed by"],
            ["lives in"] = ["live in", "lived in", "lives at", "resides in"],
            ["belongs to"] = ["belong to", "belonged to"],
            ["knows"] = ["know", "knew", "knowing"],
            ["related to"] = ["relates to", "related"],
            ["is interested in"] = ["interested in", "is interested", "interested"],
            ["believes"] = ["believe", "believed", "believing"],
            ["requires"] = ["require", "required", "requiring", "needs", "need", "needed"],

            // ── Activity ──────────────────────────────────────────────────────────
            ["attended"] = ["attend", "attends", "attending"],
            ["visited"] = ["visit", "visits", "visiting"],
            ["travelled to"] = ["travel to", "travels to", "traveled to", "travelling to", "went to", "flew to"],
            ["started"] = ["start", "starts", "starting", "began", "begin", "begins", "begun"],
            ["finished"] = ["finish", "finishes", "finishing"],
            ["completed"] = ["complete", "completes", "completing"],
            ["cancelled"] = ["cancel", "cancels", "cancelling", "canceled"],
            ["planned"] = ["plan", "plans", "planning"],
            ["scheduled"] = ["schedule", "schedules", "scheduling"],
            ["learned"] = ["learn", "learns", "learning", "learnt", "studied"],
            ["created"] = ["create", "creates", "creating", "made", "make", "makes", "making", "built", "build", "builds"],
            // The two verbs the known failing question needs, and which schema.org supplies neither of:
            // there is no RepairAction and no AssembleAction anywhere in the Action hierarchy.
            ["fixed"] = ["fix", "fixes", "fixing", "repair", "repaired", "repairs", "repairing", "mended"],
            ["assembled"] = ["assemble", "assembles", "assembling", "put together", "set up"],
            ["uses"] = ["use", "used", "using"],
            ["wants"] = ["want", "wanted", "wanting", "wishes", "wish"],
            ["asked about"] = ["ask about", "asks about", "asking about", "asked", "asks"],
            ["requested"] = ["request", "requests", "requesting"],
            ["considered"] = ["consider", "considers", "considering", "is considering"],
            ["watched"] = ["watch", "watches", "watching"],
            ["met"] = ["meet", "meets", "meeting"],
            ["finds"] = ["find", "found", "finding"],

            // ── Life events: schema.org has MarryAction and nothing else here ─────
            ["was born"] = ["born", "was born in", "were born in", "were born"],
            ["died"] = ["die", "dies", "dying", "passed away"],
            ["married"] = ["marry", "marries", "marrying", "wed", "wedded"],
            ["divorced"] = ["divorce", "divorces", "divorcing"],
            ["welcomed"] = ["welcome", "welcomes", "welcoming"],
            ["adopted"] = ["adopt", "adopts", "adopting"],

            // ── Change of value ───────────────────────────────────────────────────
            ["moved to"] = ["move to", "moves to", "moving to", "relocated to"],
            ["changed to"] = ["change to", "changes to", "changing to"],
            ["increased to"] = ["increase to", "increases to", "increased", "rose to"],
            ["decreased to"] = ["decrease to", "decreases to", "decreased", "fell to"],
            ["updated to"] = ["update to", "updates to", "updated"]
        };
}
