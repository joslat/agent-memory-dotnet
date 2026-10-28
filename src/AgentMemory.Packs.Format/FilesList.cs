using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentMemory.Packs.Format;

/// <summary>One listed file.</summary>
internal sealed record ListedFile(string Path, long Length, string Sha256);

/// <summary>
/// <c>META/files.json</c>: the pack's identity (v1.1 H1). It names the format, the pack id and version, and every file except
/// itself and the signatures, with length and SHA-256, sorted by path. Its exact bytes are RFC 8785 canonical, and the pack
/// digest is <c>sha256:</c> plus the hex SHA-256 of those bytes, the same in a folder, an <c>.ampack</c> file or a registry.
/// </summary>
internal sealed partial record FilesList(string Id, string Version, IReadOnlyList<ListedFile> Files)
{
    internal const string Format = "agentmemory.pack.files/1.1";

    /// <summary>The canonical bytes for <paramref name="content"/>: every file but the list itself, the signatures and the attestations.</summary>
    internal static byte[] Build(string id, string version, IReadOnlyDictionary<string, byte[]> content)
    {
        var files = new JsonArray();
        foreach (var (path, bytes) in content.Where(f => IsListed(f.Key)).OrderBy(f => f.Key, StringComparer.Ordinal))
            files.Add((JsonNode)new JsonObject { ["path"] = path, ["length"] = bytes.LongLength, ["sha256"] = Hex(bytes) });
        var root = new JsonObject { ["format"] = Format, ["id"] = id, ["version"] = version, ["files"] = files };
        return Jcs.Canonical(root);
    }

    /// <summary>Whether a path belongs in the list: everything except the list, the signatures and the attestations.</summary>
    internal static bool IsListed(string path) => path != PackPaths.FilesList && !PackPaths.IsSignature(path) && !PackPaths.IsAttestation(path);

    /// <summary>The digest that names a pack: <c>sha256:</c> and the hex SHA-256 of the list's exact bytes.</summary>
    internal static string DigestOf(ReadOnlySpan<byte> listBytes) => "sha256:" + Hex(listBytes);

    internal static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Parses canonical list bytes, or returns why they are not a valid list.</summary>
    internal static (FilesList? List, string? Problem) Parse(ReadOnlySpan<byte> bytes)
    {
        if (Jcs.CheckCanonical(bytes) is { } problem) return (null, $"{PackPaths.FilesList} {problem}.");
        using var document = JsonDocument.Parse(bytes.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return (null, $"{PackPaths.FilesList} is not an object.");
        var known = new HashSet<string>(StringComparer.Ordinal) { "format", "id", "version", "files" };
        if (root.EnumerateObject().Select(p => p.Name).FirstOrDefault(n => !known.Contains(n)) is { } unknown)
            return (null, $"{PackPaths.FilesList} has an unknown field '{unknown}'.");
        if (!root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String)
            return (null, $"{PackPaths.FilesList} names no format.");
        if (format.GetString() != Format) return (null, $"{PackPaths.FilesList} has format '{format.GetString()}'; this reader knows '{Format}'.");
        if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || !PackManifest.IsValidId(id.GetString()!))
            return (null, $"{PackPaths.FilesList} names no valid pack id.");
        if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String || !SemVer.TryParse(version.GetString()!, out _))
            return (null, $"{PackPaths.FilesList} names no valid SemVer version.");
        if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            return (null, $"{PackPaths.FilesList} has no files array.");
        var list = new List<ListedFile>();
        string? previous = null;
        foreach (var entry in files.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || entry.EnumerateObject().Count() != 3
                || !entry.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String
                || !entry.TryGetProperty("length", out var length) || length.ValueKind != JsonValueKind.Number || !length.TryGetInt64(out var bytesLength) || bytesLength < 0
                || !entry.TryGetProperty("sha256", out var sha) || sha.ValueKind != JsonValueKind.String || !HexSha256().IsMatch(sha.GetString()!))
                return (null, $"{PackPaths.FilesList} has a malformed entry.");
            var name = path.GetString()!;
            if (!IsListed(name) || !PackPaths.IsSafe(name)) return (null, $"{PackPaths.FilesList} lists '{name}', which cannot be listed.");
            if (previous is not null && string.CompareOrdinal(previous, name) >= 0) return (null, $"{PackPaths.FilesList} is not sorted by path, or repeats '{name}'.");
            list.Add(new ListedFile(name, bytesLength, sha.GetString()!));
            previous = name;
        }
        return (new FilesList(id.GetString()!, version.GetString()!, list), null);
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HexSha256();
}
