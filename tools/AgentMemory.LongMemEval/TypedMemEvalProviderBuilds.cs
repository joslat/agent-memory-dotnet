using System.Globalization;

namespace AgentMemory.LongMemEval;

/// <summary>
/// The provider backend builds a single vertical's run actually used, kept BY ROLE.
/// </summary>
/// <remarks>
/// <para>
/// <b>By role, because a flattened set loses the thing it is for.</b> Answer and judge on build A
/// with extraction on B, and answer and judge on B with extraction on A, are different systems and
/// both serialise as <c>[A, B]</c>. Two runs configured differently would then band. The roles are
/// kept apart so the artifact says which component saw which build.
/// </para>
/// <para>
/// <b>Per vertical, because the meters outlive the vertical.</b> They are created once and reused
/// across an <c>all</c> invocation, so reading their totals at the end of the third vertical
/// reports builds the first two saw. A baseline is taken when the vertical starts and only the
/// difference is recorded — otherwise a clean run looks mixed after any rollover, and clean runs
/// stop banding.
/// </para>
/// <para>
/// <b>A call that reported no build makes the whole run UNKNOWN.</b> If nine responses carry
/// <c>fp_a</c> and the tenth carries nothing, the run is not "fp_a": part of it is unaccounted for,
/// and recording <c>fp_a</c> would let it band with a run that was fully accounted. That is exactly
/// the unknown-is-not-agreement rule the comparability gate states, applied one layer earlier —
/// without it the gate enforces the rule on the field while the field is built by breaking it.
/// </para>
/// </remarks>
internal sealed class TypedMemEvalProviderBuilds
{
    private readonly (string Role, LongMemEvalChatCallMeter? Meter, HashSet<string> Baseline, long MissingBaseline)[] _roles;

    private TypedMemEvalProviderBuilds(
        (string, LongMemEvalChatCallMeter?, HashSet<string>, long)[] roles) => _roles = roles;

    /// <summary>Takes the baseline for one vertical. Everything after this is that vertical's own.</summary>
    internal static TypedMemEvalProviderBuilds StartVertical(
        LongMemEvalChatCallMeter? answer,
        LongMemEvalChatCallMeter? judge,
        LongMemEvalChatCallMeter? extraction) =>
        new(
        [
            Baseline("answer", answer),
            Baseline("judge", judge),
            Baseline("extraction", extraction),
        ]);

    private static (string, LongMemEvalChatCallMeter?, HashSet<string>, long) Baseline(
        string role, LongMemEvalChatCallMeter? meter)
    {
        if (meter is null) return (role, null, new HashSet<string>(StringComparer.Ordinal), 0);

        var snapshot = meter.Snapshot();
        return (role,
            meter,
            new HashSet<string>(snapshot.ProviderBuilds.Keys, StringComparer.Ordinal),
            snapshot.CallsWithoutProviderBuild);
    }

    /// <summary>
    /// What this vertical saw: one sorted list per role that made calls, or null when any call
    /// anywhere in it reported no build.
    /// </summary>
    /// <remarks>
    /// Null is the honest answer to "which build produced this", not a defect: it says the run
    /// cannot account for all of its calls. The comparability gate then refuses to band it, which is
    /// the intended outcome rather than a cost.
    /// </remarks>
    internal IReadOnlyDictionary<string, IReadOnlyList<string>>? Snapshot()
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var (role, meter, baseline, missingBaseline) in _roles)
        {
            if (meter is null) continue;

            var snapshot = meter.Snapshot();
            if (snapshot.CallsWithoutProviderBuild > missingBaseline) return null;

            var seen = snapshot.ProviderBuilds.Keys
                .Where(build => !baseline.Contains(build))
                .OrderBy(build => build, StringComparer.Ordinal)
                .ToArray();

            // A role that made no calls in this vertical contributes nothing rather than an empty
            // list: "did not run" and "ran and reported nothing" must not serialise the same way.
            if (seen.Length > 0) result[role] = seen;
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>A one-line description for the console, for a reader watching a run.</summary>
    internal static string Describe(IReadOnlyDictionary<string, IReadOnlyList<string>>? builds) =>
        builds is null
            ? "unknown (a metered call reported no backend build)"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{string.Join(", ", builds.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => $"{pair.Key}={string.Join("+", pair.Value)}"))}");
}
