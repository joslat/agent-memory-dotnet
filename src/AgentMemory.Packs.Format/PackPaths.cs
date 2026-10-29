using System.Text.RegularExpressions;

namespace AgentMemory.Packs.Format;

/// <summary>
/// The pack path grammar (v1.1 L1): ASCII, <c>/</c>-separated, each segment a letter or digit followed by letters, digits,
/// dots, underscores or hyphens; no trailing dot; no Windows device names; at most 8 segments and 255 characters. Paths
/// that differ only by case are refused together, so a pack unpacks the same on every file system.
/// </summary>
internal static partial class PackPaths
{
    internal const string FilesList = "META/files.json";
    internal const string LockFile = "META/lock.json";
    internal const string SignaturesFolder = "META/signatures/";
    internal const string AttestationsFolder = "META/attestations/";

    /// <summary>Version-control files a pack folder may hold at its root; they are never part of the pack (v1.1 M1).</summary>
    internal static readonly IReadOnlySet<string> IgnoredAtRoot = new HashSet<string>(StringComparer.Ordinal) { ".git", ".gitattributes", ".gitignore" };

    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>The most segments a pack path holds.</summary>
    internal const int MaximumSegments = 8;

    internal static bool IsSafe(string path)
    {
        if (path.Length is 0 or > 255) return false;
        var segments = path.Split('/');
        return segments.Length <= MaximumSegments && segments.All(IsSafeSegment);
    }

    private static bool IsSafeSegment(string segment) =>
        Segment().IsMatch(segment) && !segment.EndsWith('.') && !DeviceNames.Contains(segment.Split('.')[0]);

    /// <summary>Signatures live outside the files list: everything else must be listed.</summary>
    internal static bool IsSignature(string path) => path.StartsWith(SignaturesFolder, StringComparison.Ordinal);

    /// <summary>
    /// Attestations live outside the files list too, beside the signatures (pack format v1.2 §16): statements others make about
    /// the pack by its digest, such as evaluation certificates, which can be added after release without changing the pack.
    /// </summary>
    internal static bool IsAttestation(string path) => path.StartsWith(AttestationsFolder, StringComparison.Ordinal);

    /// <summary>Every problem with a set of paths: unsafe names and names that collide ignoring case.</summary>
    internal static IEnumerable<string> Problems(IEnumerable<string> paths)
    {
        var all = paths.ToList();
        foreach (var path in all.Where(p => !IsSafe(p)).Order(StringComparer.Ordinal))
            yield return $"'{path}' is not a safe pack path.";
        foreach (var clash in all.GroupBy(p => p, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            yield return $"Paths differ only by case: {string.Join(", ", clash.Order(StringComparer.Ordinal))}.";
        // Review 1, L1: folders too. Two spellings of one folder (a/B/x and a/b/y), or a file named like a folder (x and x/y),
        // merge or collide when unpacked on a case-insensitive file system.
        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in all.Order(StringComparer.Ordinal))
        {
            var segments = path.Split('/');
            for (var i = 1; i < segments.Length; i++)
            {
                var folder = string.Join('/', segments[..i]);
                if (!folders.TryAdd(folder, folder) && folders[folder] != folder)
                {
                    yield return $"Folders differ only by case: {folders[folder]}, {folder}.";
                    folders[folder] = folder;
                }
            }
        }
        foreach (var path in all.Where(folders.ContainsKey).Order(StringComparer.Ordinal))
            yield return $"'{path}' is both a file and a folder.";
    }

    [GeneratedRegex("\\A[A-Za-z0-9][A-Za-z0-9._-]{0,99}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex Segment();
}
