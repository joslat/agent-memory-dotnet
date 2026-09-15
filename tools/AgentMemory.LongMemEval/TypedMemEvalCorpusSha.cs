using AgentEval.Memory.External.TypedMemEval;

namespace AgentMemory.LongMemEval;

/// <summary>
/// The sha256 of the corpus this build actually carries — the only field that distinguishes one
/// draw of a vertical from another.
/// </summary>
/// <remarks>
/// <para>
/// AgentEval redraw corpora keeping the question_id set <b>100% identical</b> with <b>zero</b>
/// byte-identical items, and neither <c>corpus_id</c> nor <c>revision</c> moves either. For
/// bitemporal, 27 of 60 items keep the same question text and carry different gold. Question ids,
/// question text, corpus id and revision are therefore all insufficient; the sha is not.
/// </para>
/// <para>
/// Extracted so the re-grade gate and the scoreboard share <b>one</b> implementation. Two copies of
/// a lineage check drift, and the copy that drifts is the one nobody is looking at.
/// </para>
/// </remarks>
internal static class TypedMemEvalCorpusSha
{
    /// <summary>The sha of the corpus this build carries for a vertical, or null if not found.</summary>
    /// <remarks>
    /// Hashed from the embedded resource rather than read from a manifest, so it is the bytes the run
    /// would use and not a claim about them. Null when the resource cannot be located, which is
    /// treated as "cannot verify" rather than as "matches".
    /// </remarks>
    internal static string? For(string verticalSlug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verticalSlug);

        var assembly = typeof(TypedMemEvalRunner).Assembly;
        var matches = assembly.GetManifestResourceNames().Where(resource =>
                resource.Contains($".{verticalSlug}.", StringComparison.OrdinalIgnoreCase)
                && resource.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                && !resource.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // Exactly one, or nothing. FirstOrDefault would resolve an ambiguity by manifest ordering,
        // which is not guaranteed -- and a gate that picks a different corpus on a different run is
        // worse than no gate, because it looks like it verified something.
        if (matches.Length != 1) return null;

        using var stream = assembly.GetManifestResourceStream(matches[0]);
        if (stream is null) return null;

        // Hashed straight off the stream: the corpora run to ~1.5 MB and buffering them twice to
        // compute a digest is allocation for nothing.
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream))
            .ToLowerInvariant();
    }
}
