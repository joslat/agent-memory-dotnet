namespace AgentMemory.Neo4j.Queries;

/// <summary>
/// "Does this owner hold any rows of this label at all?" — the check that separates a starved owner
/// from an empty one before the escalation ladder is climbed (PLAN 2.13).
/// </summary>
/// <remarks>
/// <para>
/// When an owner-scoped vector search returns nothing, recall escalates: a widened probe over the
/// <b>global</b> index, then an owner-scoped scan. For an owner that holds no rows of that label both
/// are futile by construction — widening a global index cannot surface rows that do not exist — and
/// the widened probe is the expensive one, since it asks the index for up to <c>MaxTopK</c> candidates
/// across the entire corpus.
/// </para>
/// <para>
/// <b>The ladder still cannot simply be shortened, and that is what the measurement showed.</b> A
/// starved owner — plenty of rows, crowded out of the global top-K by noisier neighbours — <i>is</i>
/// recovered by the escalation, so removing a rung would lose real answers. The distinction the
/// optimisation rests on is not "did the search return nothing" but "does this owner have anything to
/// find", and only the second is answerable cheaply: it is bounded by one owner's data rather than by
/// the corpus.
/// </para>
/// <para>
/// <c>LIMIT 1</c> rather than a count: the question is existence, and counting an owner's whole
/// partition to learn it is non-zero would reintroduce the cost this avoids.
/// </para>
/// </remarks>
internal static class OwnerRowExistence
{
    /// <summary>
    /// Builds the existence probe for a label.
    /// </summary>
    /// <param name="label">Node label, e.g. <c>Fact</c>.</param>
    /// <param name="includeShared">
    /// Whether un-owned (shared) rows count. Must mirror the search's own scoping exactly — a probe
    /// that judged the owner empty on stricter terms than the search would skip an escalation that
    /// could have succeeded, which is a silent recall loss rather than a saving.
    /// </param>
    public static string Any(string label, bool includeShared)
    {
        var owner = includeShared
            ? "(n.owner_id = $ownerId OR n.owner_id IS NULL)"
            : "n.owner_id = $ownerId";

        // invalidated_at mirrors the vector searches, which all exclude soft-invalidated rows. An
        // owner holding nothing but tombstones has nothing the escalation could return.
        return $"""
            MATCH (n:{label})
            WHERE {owner} AND n.invalidated_at IS NULL
            RETURN 1 AS present
            LIMIT 1
            """;
    }
}
