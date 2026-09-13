using System.Globalization;
using System.Reflection;
using System.Text.Json;
using AgentEval.Memory.External.TypedMemEval;

namespace AgentMemory.LongMemEval;

/// <summary>
/// The corpus's own REACHABLE ceiling, read from the <c>.meta.json</c> AgentEval ships beside every
/// corpus — because it is not 1.0, and every score this project has reported assumed it was.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the ceiling is.</b> Each corpus declares <c>ceiling.k_ref</c> (the reference retrieval
/// budget) and <c>ceiling.by_g</c> — the best achievable score for a question needing <c>g</c> gold
/// sessions. At <c>k_ref = 5</c>, conjunction's <c>by_g</c> reads <c>{2..5: 1.0, 6: 0.8333,
/// 8: 0.625}</c>: a question whose answer is spread over eight sessions <b>cannot be fully answered
/// from five</b>. Combined with <c>structure.g_distribution</c>, 13 of conjunction's 65 questions
/// (20%) are structurally capped, for a corpus ceiling of <b>0.947</b>.
/// </para>
/// <para>
/// <b>Why this had to be wired.</b> A per-type scoreboard read against 1.0 overstates every gap it
/// exists to rank, and ranking by "score versus REACHABLE headroom" is the stated basis for choosing
/// what to improve. We asked AgentEval to expose this; <b>they already shipped it</b>, per corpus, in
/// a resource the harness never opened. The error was reflecting over the assembly's type surface and
/// concluding absence without reading the embedded resources.
/// </para>
/// <para>
/// <b>Nothing is hardcoded.</b> The values come from the package the run actually loads, keyed to the
/// same corpus the questions come from — so a redrawn corpus brings its own ceiling and the two
/// cannot drift. Null when the resource is absent or malformed, which reads as <i>not known</i>
/// rather than as 1.0: assuming a perfect ceiling is precisely the error this closes.
/// </para>
/// </remarks>
internal static class TypedMemEvalReachableCeiling
{
    /// <summary>The ceiling for one vertical, or null when the corpus does not declare one.</summary>
    internal static ReachableCeiling? For(string verticalSlug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verticalSlug);

        var assembly = typeof(TypedMemEvalRunner).Assembly;
        // Exactly one, or nothing -- the same rule the corpus-sha gate uses. FirstOrDefault would
        // resolve an ambiguity by manifest ordering, which is not guaranteed, and a denominator that
        // silently changes between runs is worse than no denominator at all.
        var matches = assembly.GetManifestResourceNames().Where(resource =>
                resource.Contains($".{verticalSlug}.", StringComparison.OrdinalIgnoreCase)
                && resource.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1) return null;

        using var stream = assembly.GetManifestResourceStream(matches[0]);
        if (stream is null) return null;

        try
        {
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (!root.TryGetProperty("ceiling", out var ceiling) ||
                !ceiling.TryGetProperty("by_g", out var byG) ||
                !root.TryGetProperty("structure", out var structure) ||
                !structure.TryGetProperty("g_distribution", out var distribution))
            {
                return null;
            }

            var kRef = ceiling.TryGetProperty("k_ref", out var k) && k.TryGetInt32(out var kv) ? kv : (int?)null;

            double weighted = 0;
            var questions = 0;
            var capped = 0;
            foreach (var bucket in distribution.EnumerateObject())
            {
                if (!int.TryParse(bucket.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) continue;
                if (!bucket.Value.TryGetInt32(out var count) || count <= 0) continue;

                // A g-bucket with no declared ceiling is UNKNOWN, not 1.0. Defaulting it to a perfect
                // ceiling would quietly reintroduce the assumption this type exists to remove.
                if (!byG.TryGetProperty(bucket.Name, out var reach) || !reach.TryGetDouble(out var value))
                    return null;

                weighted += count * value;
                questions += count;
                if (value < 1.0) capped += count;
            }

            return questions == 0 ? null : new ReachableCeiling(kRef, questions, capped, weighted);
        }
        catch (JsonException)
        {
            // Malformed meta reads as "not known". A thrown exception here would cost a completed
            // paid run for a diagnostic, which no denominator is worth.
            return null;
        }
    }
}

/// <summary>A corpus's reachable ceiling, and how much of it is structural.</summary>
/// <param name="KRef">The reference retrieval budget the ceiling was computed at.</param>
/// <param name="Questions">Questions covered by the distribution.</param>
/// <param name="CappedQuestions">How many cannot reach 1.0 at <paramref name="KRef"/>.</param>
/// <param name="ReachableQuestions">Sum of per-question ceilings — the honest denominator.</param>
internal readonly record struct ReachableCeiling(
    int? KRef, int Questions, int CappedQuestions, double ReachableQuestions)
{
    /// <summary>The corpus ceiling as a fraction: below 1.0 whenever any question is capped.</summary>
    public double Fraction => Questions == 0 ? 0 : ReachableQuestions / Questions;

    /// <summary>A score expressed against what is reachable rather than against 1.0.</summary>
    public double ShareOfReachable(int correct) =>
        ReachableQuestions <= 0 ? 0 : correct / ReachableQuestions;
}
