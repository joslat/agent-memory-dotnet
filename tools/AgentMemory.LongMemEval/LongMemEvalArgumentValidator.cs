namespace AgentMemory.LongMemEval;

/// <summary>
/// Rejects command-line options this harness does not recognise.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because a typo silently changed what was measured.</b> `--mode structured` was
/// passed where the option is `--memory-mode`; every parser here reads options by name and ignores
/// whatever it does not recognise, so the flag vanished, the run executed in Raw mode with no
/// extraction, and the report looked like a successful measurement of a question nobody asked.
/// </para>
/// <para>
/// The harness already fails closed on sealed manifests, extraction accounting and schema state, and
/// each of those caught a genuine problem. It did not fail closed on its own command line — the one
/// input typed by hand, and therefore the one most likely to be wrong.
/// </para>
/// </remarks>
internal static class LongMemEvalArgumentValidator
{
    /// <summary>
    /// Throws if <paramref name="args"/> contains an option outside <paramref name="known"/>.
    /// </summary>
    /// <remarks>
    /// Only tokens in OPTION position are checked. A token following a known option is its VALUE and
    /// is skipped even when it begins with a dash — a negative number or an odd-looking path must not
    /// be reported as an unknown option, or this would trade a silent failure for a false alarm.
    /// </remarks>
    internal static void Validate(IReadOnlyList<string> args, IReadOnlyCollection<string> known)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(known);

        var lookup = new HashSet<string>(known, StringComparer.Ordinal);

        for (var index = 0; index < args.Count; index++)
        {
            var token = args[index];
            if (!token.StartsWith("--", StringComparison.Ordinal)) continue;

            if (lookup.Contains(token))
            {
                // Skip this option's value so a value beginning with "--" is never validated as an
                // option. Whether an option takes a value is not knowable here, so the next token is
                // skipped only when it is not itself a known option — which keeps consecutive flags
                // (`--preflight-only --no-orphan-sweep`) working.
                if (index + 1 < args.Count && !lookup.Contains(args[index + 1])) index++;
                continue;
            }

            throw new ArgumentException(BuildMessage(token, known));
        }
    }

    private static string BuildMessage(string unknown, IReadOnlyCollection<string> known)
    {
        // CONTAINMENT FIRST, edit distance second. The failure that motivated this was `--mode` for
        // `--memory-mode`, and pure Levenshtein does not catch it: the strings are 7 apart, which no
        // sane distance threshold admits. But the typed name is a SUBSTRING of the real one, which is
        // what an abbreviation-style mistake looks like. Distance still covers transpositions and
        // single-character slips, which containment misses.
        var typed = unknown.TrimStart('-');
        var contained = known
            .Where(candidate => candidate.TrimStart('-').Contains(typed, StringComparison.Ordinal)
                                || typed.Contains(candidate.TrimStart('-'), StringComparison.Ordinal))
            .OrderBy(candidate => candidate.Length)
            .ThenBy(candidate => candidate, StringComparer.Ordinal)
            .FirstOrDefault();

        var nearest = known
            .Select(candidate => (candidate, distance: Distance(unknown, candidate)))
            .OrderBy(pair => pair.distance)
            .ThenBy(pair => pair.candidate, StringComparer.Ordinal)
            .First();

        var match = contained ?? (nearest.distance <= Math.Max(3, unknown.Length / 2)
            ? nearest.candidate
            : null);

        var suggestion = match is null ? string.Empty : $" Did you mean {match}?";

        return $"Unknown option '{unknown}'.{suggestion} Known options: "
            + string.Join(", ", known.OrderBy(name => name, StringComparer.Ordinal))
            + ".";
    }

    /// <summary>Levenshtein distance, used only to pick a suggestion.</summary>
    private static int Distance(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++) previous[j] = j;

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }
}
