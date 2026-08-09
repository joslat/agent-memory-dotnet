using System.Collections.Concurrent;

namespace AgentMemory.Core.Memory;

/// <summary>
/// The set of relation names extraction has already established, so it reuses them instead of
/// inventing a new phrasing per sentence.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <see cref="MemoryTripleCanonicalizer"/> collapses <i>spelling</i>
/// (<c>were_born_in</c> → <c>were born in</c>). It cannot collapse <i>phrasing</i>. Measured on a
/// live extracted graph, one real-world event — a birth — arrived as <c>was born</c>,
/// <c>was born in</c>, <c>were born in</c>, <c>had</c> and <c>welcomed</c>, so retrieving any single
/// relation gathered at most three of five and counting questions were unanswerable. That graph held
/// 700 facts under 421 distinct predicates: a vocabulary almost as large as the data, which is what
/// happens when nothing tells the extractor which relations already exist.
/// </para>
/// <para>
/// <b>Deterministic only.</b> Admission matches on the canonical form and nothing else. No embedding
/// or similarity folding: <c>bought</c>/<c>sold</c> and <c>likes</c>/<c>dislikes</c> sit one
/// threshold apart and mean opposite things, and merging them would silently invert stored facts in
/// a way ordinary tests would not catch. Narrowing the vocabulary is the extractor's job, guided by
/// what it is shown; it is never this type's job to guess.
/// </para>
/// <para>
/// <b>First spelling wins</b>, so an established relation never drifts between runs and queries can
/// rely on its name. Growth is capped because the vocabulary is injected into the extraction prompt,
/// and an uncapped one would trend toward one predicate per fact and consume the budget it exists to
/// improve. A predicate beyond the cap is still returned and usable — it is simply not established.
/// </para>
/// </remarks>
public sealed class MemoryPredicateVocabulary
{
    /// <summary>Keyed on the canonical form; the value is the established surface spelling.</summary>
    private readonly ConcurrentDictionary<string, string> _established;
    private readonly int _maximumSize;

    /// <param name="maximumSize">
    /// Cap on established relations. The vocabulary is injected into the extraction prompt, so an
    /// uncapped one would trend toward one predicate per fact and consume the budget it improves.
    /// </param>
    public MemoryPredicateVocabulary(int maximumSize = 256)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSize);
        _maximumSize = maximumSize;
        _established = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>Number of established relations.</summary>
    public int Count => _established.Count;

    /// <summary>
    /// Returns the established spelling for <paramref name="predicate"/>, establishing it if the
    /// relation is new and there is room.
    /// </summary>
    /// <returns>
    /// The established predicate when one exists or is created; otherwise <paramref name="predicate"/>
    /// unchanged, so a full vocabulary degrades to today's behaviour rather than losing a fact.
    /// </returns>
    public string Admit(string? predicate)
    {
        var canonical = MemoryTripleCanonicalizer.Canonical(predicate);
        if (canonical.Length == 0)
            return string.Empty;

        if (_established.TryGetValue(canonical, out var established))
            return established;

        // Racing writers may briefly exceed the cap; the bound is a budget guard, not an invariant,
        // and rejecting a predicate is never worth a lock on the extraction hot path.
        if (_established.Count >= _maximumSize)
            return predicate!;

        return _established.GetOrAdd(canonical, predicate!.Trim());
    }

    /// <summary>The established relations, ordered so an injected prompt is reproducible.</summary>
    public IReadOnlyList<string> Snapshot() =>
        _established.Values.OrderBy(value => value, StringComparer.Ordinal).ToArray();
}
