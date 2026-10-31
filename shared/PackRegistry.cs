using System.Text.Json;

// One source compiled into both the pack format and the module host, each with its own copy of the one embedded registry file,
// under its own namespace (as PackNamespace.cs: the two assemblies grant internals to the same test assemblies).
#if AGENTMEMORY_PACKS_FORMAT
namespace AgentMemory.Packs.Format;
#else
namespace AgentMemory.Extensibility;
#endif

/// <summary>
/// The one definition of every name the format defines (feature 01, step 1): the field, value, role and member lists live in
/// <c>spec/registry.json</c>, which this reader, the Python verifier and the spec's tables all read. No list here is written by
/// hand, so the two verifiers and the spec cannot drift apart.
/// </summary>
internal static class PackRegistry
{
#if AGENTMEMORY_PACKS_FORMAT
    private const string Resource = "AgentMemory.Packs.Format.registry.json";
#else
    private const string Resource = "AgentMemory.Extensibility.registry.json";
#endif

    private static readonly JsonElement Root = Load();

    /// <summary>Every top-level field <c>pack.json</c> may hold.</summary>
    internal static readonly IReadOnlySet<string> ManifestFields = Set("manifest", "fields");
    /// <summary>The fields of <c>pack.json</c> whose value is a string.</summary>
    internal static readonly IReadOnlyList<string> ManifestStringFields = [.. Strings("manifest", "stringFields")];
    internal static readonly IReadOnlySet<string> Kinds = Set("manifest", "values", "kind");
    /// <summary>The fields of <c>META/files.json</c>.</summary>
    internal static readonly IReadOnlySet<string> FilesListFields = Set("documents", "files", "fields");
    /// <summary>The fields of a composite's manifest.</summary>
    internal static readonly IReadOnlySet<string> CompositeFields = Set("documents", "composite", "fields");
    internal static readonly IReadOnlySet<string> Tiers = Set("manifest", "values", "tier");
    internal static readonly IReadOnlySet<string> Protections = Set("manifest", "values", "protection");
    internal static readonly IReadOnlySet<string> CodeTiers = Set("manifest", "values", "code");
    internal static readonly IReadOnlySet<string> Extensions = Set("manifest", "extensions");
    internal static readonly IReadOnlySet<string> MemberFields = Set("members", "fields");
    internal static readonly IReadOnlySet<string> SigningRoles = Set("roles", "signing");
    internal static readonly IReadOnlySet<string> KeyRoles = Set("roles", "keys");
    internal static readonly IReadOnlySet<string> AccessKinds = Set("federation", "accessKinds");
    internal static readonly IReadOnlySet<string> Measures = Set("certificate", "measures");
    internal static readonly IReadOnlySet<string> Baselines = Set("certificate", "baselines");
    /// <summary>The fields of a trial declaration, and the targets a trial (and so a certificate) may state.</summary>
    internal static readonly IReadOnlySet<string> TrialFields = Set("trial", "fields");
    internal static readonly IReadOnlySet<string> Targets = Set("trial", "targets");
    internal static readonly IReadOnlySet<int> Confidences = new HashSet<int>(Path("certificate", "confidences").EnumerateArray().Select(e => e.GetInt32()));
    /// <summary>Reserved device names, compared without case, as the path grammar refuses them.</summary>
    internal static readonly IReadOnlySet<string> DeviceNames = new HashSet<string>(Strings("paths", "deviceNames"), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A profile: an optional part of the format a pack uses when it holds one of the profile's files (a name, or a folder ending
    /// in <c>/</c>), names one of its <c>pack.json</c> fields, is one of its kinds, carries one of its extensions, or gives a field
    /// one of its values. Core is every pack and is not listed. A host refuses a pack that uses a profile it does not implement.
    /// </summary>
    internal sealed record Profile(string Name, bool Implemented, IReadOnlyList<string> Files, IReadOnlyList<string> Fields,
        IReadOnlyList<string> Kinds, IReadOnlyList<string> Extensions, IReadOnlyDictionary<string, IReadOnlySet<string>> Values, string? Refusal);

    /// <summary>The profiles, in the registry's order: derived from what a pack holds, never declared by it (feature 01, step 2).</summary>
    internal static readonly IReadOnlyList<Profile> Profiles = [.. Path("profiles").EnumerateObject().Select(p => new Profile(
        p.Name, p.Value.GetProperty("implemented").GetBoolean(), Optional(p.Value, "files"), Optional(p.Value, "fields"), Optional(p.Value, "kinds"),
        Optional(p.Value, "extensions"),
        p.Value.TryGetProperty("values", out var values)
            ? values.EnumerateObject().ToDictionary(v => v.Name, v => (IReadOnlySet<string>)new HashSet<string>(v.Value.EnumerateArray().Select(e => e.GetString()!), StringComparer.Ordinal), StringComparer.Ordinal)
            : new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal),
        p.Value.TryGetProperty("refusal", out var refusal) ? refusal.GetString() : null))];

    private static readonly Dictionary<string, IReadOnlySet<string>> Shapes = Path("documents").EnumerateObject()
        .ToDictionary(d => d.Name, d => (IReadOnlySet<string>)new HashSet<string>(d.Value.GetProperty("fields").EnumerateArray().Select(e => e.GetString()!), StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>The closed set of fields of a document the format defines, by its name in the registry's <c>documents</c>.</summary>
    internal static IReadOnlySet<string> Shape(string name) =>
        Shapes.TryGetValue(name, out var fields) ? fields : throw new InvalidOperationException($"The format registry has no document '{name}'.");

    private static IReadOnlyList<string> Optional(JsonElement element, string name) =>
        element.TryGetProperty(name, out var list) ? [.. list.EnumerateArray().Select(e => e.GetString()!)] : [];

    private static JsonElement Load()
    {
        using var stream = typeof(PackRegistry).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException($"The format registry ({Resource}) is not embedded.");
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        if (document.RootElement.TryGetProperty("format", out var format) is false || format.GetString() != "ampack.registry/1")
            throw new InvalidOperationException("The format registry is not ampack.registry/1.");
        return document.RootElement.Clone();
    }

    private static JsonElement Path(params string[] path)
    {
        var element = Root;
        foreach (var name in path)
            if (!element.TryGetProperty(name, out element)) throw new InvalidOperationException($"The format registry has no {string.Join(".", path)}.");
        return element;
    }

    private static IEnumerable<string> Strings(params string[] path) => Path(path).EnumerateArray().Select(e => e.GetString()!);

    private static IReadOnlySet<string> Set(params string[] path) => new HashSet<string>(Strings(path), StringComparer.Ordinal);
}
