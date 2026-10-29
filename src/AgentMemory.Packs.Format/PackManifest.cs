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

    /// <summary>
    /// Every field <c>pack.json</c> may hold (v1.1 §5 rule 1, PACK-strict): any other refuses the pack, so a field a newer
    /// writer adds is never silently ignored. What the format does not define goes in <c>extensions</c>.
    /// </summary>
    internal static readonly IReadOnlySet<string> Fields = new HashSet<string>(StringComparer.Ordinal)
    {
        "format", "id", "version", "kind", "sdk", "requires", "display", "containableBy", "contains", "tier", "licence", "validUntil",
        "protection", "code", "source", "extensions",
    };

    /// <summary>
    /// The extensions this reader understands. An extension in <c>extensions</c> is ignored unless the pack lists its name in
    /// <c>extensions.critical</c>; a critical extension the reader does not understand refuses the pack. The first one is the
    /// federated profile (federated packs §3).
    /// </summary>
    internal static readonly IReadOnlySet<string> UnderstoodExtensions = new HashSet<string>(StringComparer.Ordinal) { PackFederation.ExtensionName };

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
    /// <summary>The federated profile: publisher WebID, audience, purpose; null when the pack is not federated.</summary>
    internal PackFederation? Federation { get; init; }

    /// <summary>
    /// Reverse-DNS: at least three lowercase segments, each starting with a letter, with inner hyphens, at most 64 characters.
    /// Every id this accepts is also a valid module id (ModuleIdentity.IsValidModuleId), so a pack that builds also loads.
    /// </summary>
    internal static bool IsValidId(string id) => id.Length <= 64 && IdPattern().IsMatch(id);

    /// <summary>
    /// Whether <paramref name="prefix"/> is a pack id prefix: one or more id segments (lowercase, as an id's), optionally ending
    /// with a dot; at most 64 characters. A prefix outside it could never cover an id, so it is refused, never ignored
    /// (review 1, H3 and L17); the host's trial and certificate namespaces use the same rule.
    /// </summary>
    internal static bool IsValidPrefix(string prefix) => PackIds.PackNamespace.IsValidPrefix(prefix);

    /// <summary>Whether <paramref name="prefix"/> covers <paramref name="id"/>: the same id, or a parent namespace of it.</summary>
    internal static bool Covers(string prefix, string id) => PackIds.PackNamespace.Covers(prefix, id);

    // More than a listed JSON file may hold is refused here, whoever calls (review 6, H1).
    internal static (PackManifest? Manifest, string? Problem) Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > PackVerifier.MaximumJsonBytes) return (null, $"{FileName} is larger than {PackVerifier.MaximumJsonBytes >> 20} MiB.");
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
        if (root.EnumerateObject().Select(p => p.Name).FirstOrDefault(n => !Fields.Contains(n)) is { } unknown)
            return (null, $"{FileName} has an unknown field '{unknown}'; a field this format does not define goes in 'extensions'.");
        if (Extensions(root) is { } extensionProblem) return (null, extensionProblem);
        // A field of the wrong type is refused, never read as absent (an expiry as a number would never expire).
        foreach (var name in (string[])["format", "id", "version", "kind", "sdk", "tier", "licence", "validUntil", "protection", "code"])
            if (root.TryGetProperty(name, out var field) && field.ValueKind != JsonValueKind.String)
                return (null, $"{FileName} has a '{name}' that is not a string.");
        foreach (var name in (string[])["requires", "display"])
            if (root.TryGetProperty(name, out var field) && field.ValueKind != JsonValueKind.Object)
                return (null, $"{FileName} has a '{name}' that is not an object.");
        if (root.TryGetProperty("display", out var shown) && shown.TryGetProperty("icon", out var icon) && icon.ValueKind != JsonValueKind.String)
            return (null, $"{FileName} has a display.icon that is not a string.");
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
        if (Strings("containableBy") is not { } containable || containable.Any(c => !IsValidId(c))) return (null, $"{FileName} has a malformed 'containableBy'.");
        DateTimeOffset? validUntil = null;
        if (Text("validUntil") is { } until)
        {
            if (!PackTime.TryParse(until, out var parsed))
                return (null, $"{FileName} has a validUntil that is not an RFC 3339 date and time.");
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
                if (!PackTime.TryParse(built, out var at))
                    return (null, $"{FileName} has a source.builtAt that is not an RFC 3339 date and time.");
                builtAt = at;
            }
            if (Field("commit") is { } commit && !CommitPattern().IsMatch(commit))
                return (null, $"{FileName} has a source.commit that is not 7 to 64 hexadecimal characters.");
            origin = new PackOrigin(Field("repository"), Field("commit"), builtAt);
        }
        PackFederation? federation = null;
        if (root.TryGetProperty("extensions", out var extensions))
        {
            var (read, federationProblem) = PackFederation.Read(extensions);
            if (federationProblem is not null) return (null, federationProblem);
            federation = read;
        }
        return (new PackManifest
        {
            Id = id, Version = version, Kind = kind, Tier = tier, Licence = Text("licence"), Contains = contains, ContainableBy = containable,
            ValidUntil = validUntil, Protection = protection, Code = code, Source = origin, Federation = federation,
        }, null);
    }

    // extensions: an object; "critical", when present, lists names of other members, each once; a critical one this reader
    // does not understand refuses the pack, any other extension is ignored.
    private static string? Extensions(JsonElement root)
    {
        if (!root.TryGetProperty("extensions", out var extensions)) return null;
        if (extensions.ValueKind != JsonValueKind.Object) return $"{FileName} has a malformed 'extensions': an object.";
        if (!extensions.TryGetProperty("critical", out var critical)) return null;
        if (critical.ValueKind != JsonValueKind.Array || critical.EnumerateArray().Any(c => c.ValueKind != JsonValueKind.String))
            return $"{FileName} has a malformed 'extensions.critical': a list of extension names.";
        var names = critical.EnumerateArray().Select(c => c.GetString()!).ToList();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Count) return $"{FileName} lists a critical extension twice.";
        foreach (var name in names)
        {
            if (name == "critical" || !extensions.TryGetProperty(name, out _))
                return $"{FileName} marks '{name}' critical, but 'extensions' holds no such extension.";
            if (!UnderstoodExtensions.Contains(name))
                return $"{FileName} marks the extension '{name}' critical, and this reader does not understand it.";
        }
        return null;
    }

    [GeneratedRegex("\\A[a-z][a-z0-9]*(?:-[a-z0-9]+)*(?:\\.[a-z][a-z0-9]*(?:-[a-z0-9]+)*){2,}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex("\\A[0-9a-f]{7,64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex CommitPattern();
}

/// <summary>Where a pack's content came from: the repository, the commit, and when it was built; each optional.</summary>
internal sealed record PackOrigin(string? Repository, string? Commit, DateTimeOffset? BuiltAt);
