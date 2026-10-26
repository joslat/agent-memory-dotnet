using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentMemory.Packs.Format;

/// <summary>
/// <c>pack.json</c> (pack format v1.2 §2): identity, kind, and what the format itself checks before a host reads anything
/// else. The declarations a host loads (sdk, requires, display, ontology, views, traversal, trial) are mapped by
/// <see cref="PackLayout"/>.
/// </summary>
internal sealed partial record PackManifest
{
    internal const string FileName = "pack.json";
    internal const string Format = "agentmemory.pack/1.2";
    internal static readonly IReadOnlySet<string> Tiers = new HashSet<string>(StringComparer.Ordinal) { "open", "curated", "premium", "private", "hosted" };
    internal static readonly IReadOnlySet<string> Kinds = new HashSet<string>(StringComparer.Ordinal) { "knowledge", "definition", "snapshot", "composite", "set" };
    internal static readonly IReadOnlySet<string> Protections = new HashSet<string>(StringComparer.Ordinal) { "open", "audience", "licensed", "attested", "hosted" };
    internal static readonly IReadOnlySet<string> CodeTiers = new HashSet<string>(StringComparer.Ordinal) { "E0", "E1", "E2" };

    internal required string Id { get; init; }
    internal required string Version { get; init; }
    internal required string Kind { get; init; }
    internal string Tier { get; init; } = "open";
    internal string? Licence { get; init; }
    /// <summary>Packs this one contains: the only ones its links may point into (v1.2 §5).</summary>
    internal IReadOnlyList<string> Contains { get; init; } = [];
    /// <summary>Id prefixes of the packs allowed to contain this one and walk into it. Empty: none may.</summary>
    internal IReadOnlyList<string> ContainableBy { get; init; } = [];
    /// <summary>After this instant a host switches the pack off (v1.2 §11).</summary>
    internal DateTimeOffset? ValidUntil { get; init; }
    /// <summary>The protection profile (v1.2 §8).</summary>
    internal string Protection { get; init; } = "open";
    /// <summary>The highest code tier the pack uses (v1.2 §9).</summary>
    internal string Code { get; init; } = "E0";
    /// <summary>Where the content came from (v1.2 §2, §11): repository, commit, built at; a snapshot names its commit here.</summary>
    internal PackOrigin? Source { get; init; }

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
        if (Text("kind") is not { } kind || !Kinds.Contains(kind)) return (null, $"{FileName} needs a kind: {string.Join(", ", Kinds)}.");
        var tier = Text("tier") ?? "open";
        if (!Tiers.Contains(tier)) return (null, $"{FileName} has an unknown tier '{tier}'.");
        var protection = Text("protection") ?? "open";
        if (!Protections.Contains(protection)) return (null, $"{FileName} has an unknown protection '{protection}'.");
        var code = Text("code") ?? "E0";
        if (!CodeTiers.Contains(code)) return (null, $"{FileName} has an unknown code tier '{code}'.");
        if (Strings("contains") is not { } contains || contains.Any(c => !IsValidId(c))) return (null, $"{FileName} has a malformed 'contains'.");
        if (Strings("containableBy") is not { } containable || containable.Any(c => !IsValidId(c.TrimEnd('.')))) return (null, $"{FileName} has a malformed 'containableBy'.");
        DateTimeOffset? validUntil = null;
        if (Text("validUntil") is { } until)
        {
            if (!DateTimeOffset.TryParse(until, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                return (null, $"{FileName} has a validUntil that is not a date and time.");
            validUntil = parsed;
        }
        PackOrigin? origin = null;
        if (root.TryGetProperty("source", out var source))
        {
            if (source.ValueKind != JsonValueKind.Object || source.EnumerateObject().Any(p => p.Name is not ("repository" or "commit" or "builtAt")
                    || p.Value.ValueKind != JsonValueKind.String || p.Value.GetString()!.Length is 0 or > 200))
                return (null, $"{FileName} has a malformed 'source': an object of repository, commit and builtAt, each a string of 1 to 200 characters.");
            string? Field(string name) => source.TryGetProperty(name, out var value) ? value.GetString() : null;
            DateTimeOffset? builtAt = null;
            if (Field("builtAt") is { } built)
            {
                if (!DateTimeOffset.TryParse(built, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
                    return (null, $"{FileName} has a source.builtAt that is not a date and time.");
                builtAt = at;
            }
            if (Field("commit") is { } commit && !CommitPattern().IsMatch(commit))
                return (null, $"{FileName} has a source.commit that is not 7 to 64 hexadecimal characters.");
            origin = new PackOrigin(Field("repository"), Field("commit"), builtAt);
        }
        return (new PackManifest
        {
            Id = id, Version = version, Kind = kind, Tier = tier, Licence = Text("licence"), Contains = contains, ContainableBy = containable,
            ValidUntil = validUntil, Protection = protection, Code = code, Source = origin,
        }, null);
    }

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*(?:\\.[a-z0-9]+(?:-[a-z0-9]+)*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[0-9a-f]{7,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitPattern();
}

/// <summary>Where a pack's content came from: the repository, the commit, and when it was built; each optional.</summary>
internal sealed record PackOrigin(string? Repository, string? Commit, DateTimeOffset? BuiltAt);
