namespace AgentMemory.LongMemEval;

/// <summary>
/// Turns a request for memory <b>types</b> into the dataset <b>task labels</b> that exercise them.
/// </summary>
/// <remarks>
/// <para>
/// This is the inverse of <see cref="LongMemEvalMemoryTypeMap"/>, and it exists because a per-type
/// claim needs a per-type <i>sample</i>, not a per-type slice of a mixed one. A 50-question stratified
/// sample yields roughly 6 <c>single-session-assistant</c> questions; on 6 questions one item is worth
/// 16.7 points, while two runs of an <b>identical</b> configuration have been measured 25 points apart
/// on 50. A subset that small cannot decide anything, so reporting it as a per-type accuracy would
/// publish noise with a decimal point on it.
/// </para>
/// <para>
/// Selecting the type first is what fixes that: 50 questions of one type gives that type the same
/// denominator the aggregate has always had.
/// </para>
/// <para>
/// <b>Derived from the same embedded mapping the reports use</b>, never from a second hardcoded list.
/// Two copies of a taxonomy drift, and the failure would be silent — a run would sample one set of
/// labels and report per-type figures computed from another, with both looking correct.
/// </para>
/// </remarks>
internal static class LongMemEvalMemoryTypeSelection
{
    /// <summary>
    /// The dataset task labels whose questions exercise any of <paramref name="memoryTypes"/>.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="null"/> for an empty request, which is the sampler's "no filter" value
    /// and reproduces the stratified-across-all-types default exactly.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// A named type no task label reaches. That is a hard failure on purpose: the alternative is a run
    /// that quietly samples everything and reports it as if it were the requested type. Procedural is
    /// the live case — <b>LongMemEval-S contains no procedural questions at any sample size</b>, so
    /// asking for it must say so rather than return an empty or unfiltered selection.
    /// </exception>
    internal static IReadOnlyList<string>? TaskTypesFor(
        IReadOnlyList<string> memoryTypes, LongMemEvalMemoryTypeMap? map = null)
    {
        ArgumentNullException.ThrowIfNull(memoryTypes);
        if (memoryTypes.Count == 0) return null;

        map ??= LongMemEvalMemoryTypeMap.Default;

        var requested = new HashSet<string>(memoryTypes, StringComparer.OrdinalIgnoreCase);
        var selected = new List<string>();
        var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (taskType, types) in map.TaskTypes)
        {
            if (!types.Any(requested.Contains)) continue;
            selected.Add(taskType);
            foreach (var type in types.Where(requested.Contains)) reached.Add(type);
        }

        var unreachable = requested.Where(type => !reached.Contains(type))
            .OrderBy(type => type, StringComparer.Ordinal).ToList();
        if (unreachable.Count > 0)
        {
            throw new ArgumentException(
                $"No LongMemEval task label exercises: {string.Join(", ", unreachable)}. "
                + $"Known memory types are: {string.Join(", ", map.KnownMemoryTypes)}. "
                + "Abstention questions are the only source of metamemory and are selected with "
                + "--abstention rather than by task label; LongMemEval-S contains no procedural "
                + "questions at all, so no sample of it can measure a procedural tier.");
        }

        selected.Sort(StringComparer.Ordinal);
        return selected;
    }
}
