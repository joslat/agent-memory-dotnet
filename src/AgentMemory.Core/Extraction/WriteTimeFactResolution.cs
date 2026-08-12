using AgentMemory.Abstractions.Domain;
using AgentMemory.Core.Memory;

namespace AgentMemory.Core.Extraction;

/// <summary>
/// Decides whether a newly written fact <b>replaces</b> what was already stored about its subject, or
/// joins it.
/// </summary>
/// <remarks>
/// <para>
/// The write path appends. A conversation saying "I live in Zurich" three months after "I live in
/// Basel" leaves both live, both retrievable and both equally confident — the graph grows with the
/// conversation rather than with what is true, and recall is then asked to choose between two
/// assertions with nothing to choose on.
/// </para>
/// <para>
/// <b>What this is not.</b> The field's usual approach is a model call classifying each candidate
/// write as ADD/UPDATE/DELETE/NOOP. Three of those four are decidable without asking anything: the
/// exact triple already MERGEs, so NOOP is free; a new object for a functional relation is UPDATE; and
/// everything else is ADD. Deciding them from the store rather than from a model costs no completion,
/// cannot vary between runs, and can be falsified in a unit test instead of measured against a
/// provider. DELETE is the one that genuinely needs the conversation — "forget I ever lived in Basel"
/// is not derivable from a triple — and it is left out rather than approximated.
/// </para>
/// <para>
/// <b>The whole safety property is cardinality.</b> Superseding on any repeated subject+predicate
/// would drop "likes coffee" the moment "likes tea" arrived: a true fact closed, silently, with the
/// graph still looking correct. Only relations the vocabulary declares functional are eligible, and
/// anything undeclared — every event relation, most state relations, and every predicate the extractor
/// invented — is multi-valued.
/// </para>
/// <para>
/// The loser is closed non-destructively: it keeps its content, gains <c>invalidated_at</c> and a
/// <c>:SUPERSEDED_BY</c> edge, leaves live recall, and stays visible to as-of recall. So the worst
/// case of a wrong decision is a fact that must be recovered, not one that is gone.
/// </para>
/// </remarks>
internal static class WriteTimeFactResolution
{
    /// <summary>
    /// Whether <paramref name="fact"/> can supersede an earlier assertion at all.
    /// </summary>
    /// <remarks>
    /// The <i>which</i> is answered by the store, not here: liveness lives in
    /// <c>invalidated_at</c>, which the domain record does not carry, so filtering in memory would
    /// re-close already-closed facts and fan a supersession chain into a star. This is the gate that
    /// decides whether to ask at all — and it is the cheap half, since the overwhelming majority of
    /// extracted predicates are multi-valued and cost no query.
    /// </remarks>
    internal static bool CanSupersede(Fact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        return MemoryRelationCardinality.IsSingleValued(fact.Predicate);
    }
}
