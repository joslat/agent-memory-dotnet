using System.Security.Cryptography;

namespace AgentMemory.LongMemEval;

/// <summary>
/// Finds the LongMemEval dataset, and says whether it is the one every recorded result used.
/// </summary>
/// <remarks>
/// <para>
/// The dataset is a 277 MB file that is <b>gitignored in this repository and in AgentEval, and tracked
/// by neither</b>. It exists on exactly one disk, and its path has only ever lived in shell history —
/// which is how a session was lost to hunting for it. Worse, it is a <c>-cleaned</c> variant, so
/// re-downloading the public LongMemEval-S does not necessarily reproduce it.
/// </para>
/// <para>
/// <b>The sha is the part worth keeping in git.</b> Sixty-four characters, versioned forever, and they
/// answer the only question that matters about a recovered or re-downloaded file: is this the dataset
/// every sealed corpus and every recorded number was built from? Without it, a subtly different
/// dataset produces results that look normal and are not comparable to anything — and on a cold build
/// nothing downstream would catch it, because there is no earlier corpus to drift against.
/// </para>
/// <para>
/// A mismatch <b>warns and records</b> rather than failing. Evaluating a deliberately different
/// dataset — the oracle variant, a newer cleaned release — is legitimate work, and the run fingerprint
/// already carries the actual sha, so comparability stays mechanically checkable downstream. What must
/// not happen is that it passes <i>silently</i>.
/// </para>
/// </remarks>
internal static class LongMemEvalDatasetLocator
{
    /// <summary>
    /// The sha256 of the dataset behind every sealed corpus and every recorded measurement.
    /// </summary>
    /// <remarks>
    /// Pinned here on 2026-08-12, verified against the <c>datasetSha256</c> recorded in the prepared
    /// corpora from 2026-08-08 through 2026-08-10.
    /// </remarks>
    internal const string KnownGoodSha256 =
        "d6f21ea9d60a0d56f34a05b609c79c88a451d2ae03597821ea3d5a9678c3a442";

    /// <summary>The environment variable naming the dataset, so a path need not be typed each run.</summary>
    internal const string PathVariable = "LONGMEMEVAL_DATASET";

    /// <summary>
    /// The dataset path: the explicit argument, else <see cref="PathVariable"/>, else the known
    /// checkout locations.
    /// </summary>
    /// <remarks>
    /// The fallbacks are a convenience for the machine this was developed on, deliberately last and
    /// deliberately few: guessing widely would make it possible to run against a file nobody chose.
    /// Returns null rather than throwing, so the caller keeps its own "dataset is required" message.
    /// </remarks>
    internal static string? Resolve(string? explicitPath, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (!string.IsNullOrWhiteSpace(explicitPath)) return explicitPath;
        if (environment(PathVariable) is { Length: > 0 } fromEnvironment) return fromEnvironment;

        foreach (var candidate in KnownLocations)
            if (File.Exists(candidate)) return candidate;

        return null;
    }

    private static IEnumerable<string> KnownLocations
    {
        get
        {
            // AgentEval vendors it beside its own memory benchmark; that is where it actually lives.
            yield return Path.Combine(
                "..", "AgentEval", "src", "AgentEval.Memory", "Data", "longmemeval",
                "longmemeval_s_cleaned.json");
            yield return Path.Combine("data", "longmemeval_s_cleaned.json");
        }
    }

    /// <summary>
    /// Compares a dataset against the pinned sha and returns the line to print, or null when it matches.
    /// </summary>
    /// <remarks>
    /// Hashing 277 MB costs about a second, which is nothing beside a 7–9 hour cold build and is the
    /// only moment the question can still be answered cheaply.
    /// </remarks>
    internal static string? DescribeMismatch(string datasetPath, string actualSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(actualSha256);

        if (string.Equals(actualSha256, KnownGoodSha256, StringComparison.OrdinalIgnoreCase))
            return null;

        return
            $"longmemeval: WARNING - '{Path.GetFileName(datasetPath)}' is not the dataset every "
            + "recorded result was produced from." + Environment.NewLine
            + $"  expected sha256 {KnownGoodSha256}" + Environment.NewLine
            + $"  actual   sha256 {actualSha256}" + Environment.NewLine
            + "  Results from it are internally valid but NOT comparable to any sealed corpus or "
            + "published number, and reuse of an existing corpus will be refused on dataset drift. "
            + "Continuing, because evaluating a different dataset variant is legitimate work - the "
            + "actual sha travels in this run's fingerprint.";
    }

    /// <summary>The sha256 of a file on disk, lowercase hex.</summary>
    internal static async Task<string> ComputeSha256Async(
        string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
