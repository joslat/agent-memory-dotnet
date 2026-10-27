using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Ampack;

/// <summary>
/// Whether a snapshot pack was taken from the code a host has (PACK-staleness): the commit it was built at, the host's
/// commit, and the verdict. <see cref="Fresh"/> is null when the pack names no commit, so nothing can be said.
/// </summary>
/// <param name="Pack">The snapshot pack's id and version.</param>
/// <param name="BuiltAt">The commit its <c>pack.json</c> names, or null.</param>
/// <param name="Head">The host's commit.</param>
/// <param name="Fresh">True when it was built at the host's commit; false when at another; null when unknown.</param>
/// <param name="Detail">The verdict in one sentence, with what to do about a stale pack.</param>
[Experimental("AMEXT001")]
public sealed record SnapshotFreshness(string Pack, string? BuiltAt, string Head, bool? Fresh, string Detail);

/// <summary>Freshness of snapshot packs against the host's code (pack format v1.2 §11).</summary>
[Experimental("AMEXT001")]
public static class PackFreshness
{
    /// <summary>
    /// A snapshot pack against the host's current commit: fresh when the commit it was built at is the host's (a pack may name
    /// a commit by a prefix of at least 7 hex digits), stale when it is another, unknown when it names none.
    /// </summary>
    /// <param name="snapshot">A loaded snapshot pack.</param>
    /// <param name="headCommit">The host's commit, full or abbreviated, with or without a <c>git:</c> prefix.</param>
    /// <exception cref="ArgumentException">The pack is not a snapshot, or the head is not a commit.</exception>
    public static SnapshotFreshness Freshness(this LoadedPack snapshot, string headCommit)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(headCommit);
        if (snapshot.Kind != "snapshot") throw new ArgumentException($"{snapshot.Id} is a {snapshot.Kind} pack, not a snapshot.", nameof(snapshot));
        var head = headCommit.StartsWith("git:", StringComparison.Ordinal) ? headCommit[4..] : headCommit;
        if (head.Length < 7 || !head.All(Uri.IsHexDigit)) throw new ArgumentException($"'{headCommit}' is not a commit.", nameof(headCommit));
        var name = snapshot.Id + " " + snapshot.Version;
        if (snapshot.SnapshotSource is not { } source || !source.StartsWith("git:", StringComparison.Ordinal))
            return new(name, null, head, null, $"{name} names no commit, so whether it matches this code ({Short(head)}) is unknown.");
        var built = source[4..];
        var fresh = head.StartsWith(built, StringComparison.OrdinalIgnoreCase) || built.StartsWith(head, StringComparison.OrdinalIgnoreCase);
        return new(name, built, head, fresh, fresh
            ? $"{name} was built at {Short(built)}, this code's commit: fresh."
            : $"{name} was built at {Short(built)}; this code is at {Short(head)}: stale. Rebuild it from the repository (agentmemory codemap pack), or import the repository itself.");
    }

    private static string Short(string commit) => commit.Length > 12 ? commit[..12] : commit;
}
