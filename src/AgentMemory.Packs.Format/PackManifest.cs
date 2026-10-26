using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentMemory.Packs.Format;

/// <summary>
/// The identity part of <c>pack.json</c>: what the format itself needs to check before a host reads anything else. The
/// rest of the file (ontology, records, traversal) belongs to the host's importer.
/// </summary>
internal sealed partial record PackManifest
{
    internal const string FileName = "pack.json";
    internal const string Format = "agentmemory.pack/1.1";
    internal static readonly IReadOnlySet<string> Tiers = new HashSet<string>(StringComparer.Ordinal) { "open", "curated", "premium", "private", "hosted" };

    internal required string Id { get; init; }
    internal required string Version { get; init; }
    internal required string Kind { get; init; }
    internal string Tier { get; init; } = "open";
    internal string? Licence { get; init; }
    /// <summary>Packs this one contains: the only ones its links may point into (v1.1 §6).</summary>
    internal IReadOnlyList<string> Contains { get; init; } = [];
    /// <summary>Id prefixes of the packs allowed to contain this one (v1.1 M4). Empty: none may.</summary>
    internal IReadOnlyList<string> ContainableBy { get; init; } = [];

    /// <summary>Reverse-DNS: at least two lowercase segments of letters, digits and inner hyphens.</summary>
    internal static bool IsValidId(string id) => id.Length <= 200 && IdPattern().IsMatch(id);

    /// <summary>Whether <paramref name="prefix"/> covers <paramref name="id"/>: the same id, or a parent namespace of it.</summary>
    internal static bool Covers(string prefix, string id) =>
        id == prefix || id.StartsWith(prefix.EndsWith('.') ? prefix : prefix + ".", StringComparison.Ordinal);

    internal static (PackManifest? Manifest, string? Problem) Parse(ReadOnlySpan<byte> bytes)
    {
        if (PackJson.Check(bytes) is { } problem) return (null, $"{FileName} {problem}.");
        using var document = JsonDocument.Parse(bytes.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return (null, $"{FileName} is not an object.");
        string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        IReadOnlyList<string>? Strings(string name)
        {
            if (!root.TryGetProperty(name, out var value)) return [];
            if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String)) return null;
            return [.. value.EnumerateArray().Select(v => v.GetString()!)];
        }
        if (Text("format") is not { } format) return (null, $"{FileName} names no format.");
        if (format != Format) return (null, $"{FileName} has format '{format}'; this reader knows '{Format}'.");
        if (Text("id") is not { } id || !IsValidId(id)) return (null, $"{FileName} has no valid reverse-DNS id.");
        if (Text("version") is not { } version || !SemVer.TryParse(version, out _)) return (null, $"{FileName} has no valid SemVer version.");
        if (Text("kind") is not { Length: > 0 } kind) return (null, $"{FileName} names no kind.");
        var tier = Text("tier") ?? "open";
        if (!Tiers.Contains(tier)) return (null, $"{FileName} has an unknown tier '{tier}'.");
        if (Strings("contains") is not { } contains || contains.Any(c => !IsValidId(c))) return (null, $"{FileName} has a malformed 'contains'.");
        if (Strings("containableBy") is not { } containable || containable.Any(c => !IsValidId(c.TrimEnd('.')))) return (null, $"{FileName} has a malformed 'containableBy'.");
        return (new PackManifest { Id = id, Version = version, Kind = kind, Tier = tier, Licence = Text("licence"), Contains = contains, ContainableBy = containable }, null);
    }

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*(?:\\.[a-z0-9]+(?:-[a-z0-9]+)*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
