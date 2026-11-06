using System.Text.RegularExpressions;

// One namespace per assembly that compiles this file, so the two copies never share a full name where both are visible
// (review 2: the format and the host both grant internals to the same assemblies).
#if AGENTMEMORY_PACKS_FORMAT
namespace AgentMemory.Packs.Format.PackIds;
#else
namespace AgentMemory.Extensibility.PackIds;
#endif

/// <summary>
/// The pack id namespace rules, one source compiled into both the pack format and the module host (review 1, L16): which
/// prefix covers which id, which prefixes are well formed, and the fewest tasks a trial counts. A trust policy's
/// <c>idPrefixes</c>, the host's trial and certificate namespaces, a consent grant's <c>containableBy</c> and an evaluation
/// certificate all read them from here, so they cannot drift apart.
/// </summary>
internal static partial class PackNamespace
{
    /// <summary>The fewest tasks a trial counts, on a host or in a certificate.</summary>
    internal const int MinimumTrialTasks = 10;

    /// <summary>Whether <paramref name="prefix"/> covers <paramref name="id"/>: the same id, or a whole leading run of its segments.</summary>
    internal static bool Covers(string prefix, string id) =>
        id == prefix || id.StartsWith(prefix.EndsWith('.') ? prefix : prefix + ".", StringComparison.Ordinal);

    /// <summary>
    /// Whether <paramref name="prefix"/> is a pack id prefix: one or more lowercase id segments, optionally ending with a dot,
    /// at most 64 characters. A prefix outside it could never cover an id, so it is refused, never ignored (review 1, H3, L17).
    /// </summary>
    internal static bool IsValidPrefix(string? prefix) => prefix is { Length: > 0 and <= 64 } && PrefixPattern().IsMatch(prefix);

    [GeneratedRegex(@"\A[a-z][a-z0-9]*(?:-[a-z0-9]+)*(?:\.[a-z][a-z0-9]*(?:-[a-z0-9]+)*)*\.?\z", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixPattern();
}
