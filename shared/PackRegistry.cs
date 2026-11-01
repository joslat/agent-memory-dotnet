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
    /// <summary>The value a <c>pack.json</c> field has when the pack leaves it out (tier, protection, code).</summary>
    internal static readonly IReadOnlyDictionary<string, string> ManifestDefaults = Path("manifest", "defaults").EnumerateObject()
        .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
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
    /// <summary>The trial's target names, as a certificate's checks name them.</summary>
    internal static readonly IReadOnlySet<string> Targets = new HashSet<string>(Path("trial", "targets").EnumerateObject().Select(p => p.Name), StringComparer.Ordinal);
    /// <summary>The targets a host decides on its own and no certificate certifies (feature 12: noHarm needs abstention counts no certificate arm holds).</summary>
    internal static readonly IReadOnlySet<string> HostOnlyTargets = Set("trial", "hostOnly");
    /// <summary>The field of <c>evals/trial.json</c>'s targets that holds each target, by target name (its unit is in its name).</summary>
    internal static readonly IReadOnlyDictionary<string, string> TargetFields = Path("trial", "targets").EnumerateObject()
        .ToDictionary(p => p.Name, p => p.Value.GetProperty("field").GetString()!, StringComparer.Ordinal);
    internal static readonly IReadOnlySet<string> UpgradeFields = Set("trial", "upgradeFields");
    internal static readonly IReadOnlySet<string> SafetyFields = Set("trial", "safetyFields");
    /// <summary>Why a record is erased (feature 08).</summary>
    internal static readonly IReadOnlySet<string> ErasureReasons = Set("erasure", "reasons");
    /// <summary>The roles whose key may erase a pack's records (feature 08): publisher or revoker for its id.</summary>
    internal static readonly IReadOnlySet<string> ErasureRoles = Set("erasure", "roles");
    /// <summary>A record line's life members (feature 07), beside type, key, properties and links.</summary>
    internal static readonly IReadOnlySet<string> LifeFields = Set("records", "lifeFields");
    /// <summary>A record line's trust members (feature 09): its own confidence, and the derivation input it came from.</summary>
    internal static readonly IReadOnlySet<string> TrustFields = Set("records", "trustFields");
    /// <summary>What a derivation says was done to its inputs (feature 09).</summary>
    internal static readonly IReadOnlySet<string> DerivationActions = Set("derivation", "actions");
    /// <summary>Where a pack's derivation is, inside its signed content, and the format it names (feature 09).</summary>
    internal static readonly string DerivationFile = Path("derivation", "path").GetString()!, DerivationFormat = Path("derivation", "format").GetString()!;
    /// <summary>Where a pack's usage policy is, inside its signed content, the format it names, and its verbs (feature 10).</summary>
    internal static readonly string PolicyFile = Path("policy", "path").GetString()!, PolicyFormat = Path("policy", "format").GetString()!;
    internal static readonly IReadOnlySet<string> PolicyVerbs = Set("policy", "verbs");
    /// <summary>The lifecycles a type may declare (feature 07): pinned never ages, refreshes ends ttlDays after validFrom.</summary>
    internal static readonly IReadOnlySet<string> Lifecycles = Set("records", "lifecycles");
    /// <summary>The targets met at or below their value (feature 04 review, M6); every other target is met at or above it.</summary>
    internal static readonly IReadOnlySet<string> AtMost = Set("trial", "atMost");
    /// <summary>The cost targets (feature 05), by name, each with its field in a trial's <c>cost</c>.</summary>
    internal static readonly IReadOnlyDictionary<string, string> CostFields = Path("trial", "costTargets").EnumerateObject()
        .ToDictionary(p => p.Name, p => p.Value.GetProperty("field").GetString()!, StringComparer.Ordinal);
    internal static readonly IReadOnlySet<string> RelationFields = Set("traversal", "relationFields");
    internal static readonly long DefaultWeightMicro = Path("traversal", "defaultWeightMicro").GetInt64();
    /// <summary>A unit-bearing field's unit and range (feature 02): an inclusive range, or a closed set of values.</summary>
    internal sealed record Unit(string Name, string Means, long Min, long Max, IReadOnlySet<long>? Values);
    internal static readonly IReadOnlyDictionary<string, Unit> UnitFields = Path("units", "fields").EnumerateObject().ToDictionary(p => p.Name, p =>
    {
        var name = p.Value.GetProperty("unit").GetString()!;
        var values = p.Value.TryGetProperty("values", out var v) ? new HashSet<long>(v.EnumerateArray().Select(x => x.GetInt64())) : null;
        return new Unit(name, Path("units", "kinds", name, "means").GetString()!,
            p.Value.TryGetProperty("min", out var min) ? min.GetInt64() : values?.Min() ?? 0, p.Value.TryGetProperty("max", out var max) ? max.GetInt64() : values?.Max() ?? 0, values);
    }, StringComparer.Ordinal);
    /// <summary>The types a record property may have (feature 03, Core 15.1).</summary>
    internal static readonly IReadOnlySet<string> PropertyTypes = Set("records", "propertyTypes");
    /// <summary>The memory kinds a type may declare, for mapping and display only.</summary>
    internal static readonly IReadOnlySet<string> MemoryKinds = Set("records", "memoryKinds");
    internal sealed record BuiltInProperty(string Name, string Type, bool Required);
    internal sealed record BuiltInType(string Name, string Text, IReadOnlyList<BuiltInProperty> Properties);
    internal sealed record BuiltInRelation(string Name, string From, string To);
    /// <summary>The types a pack may use without declaring them, in the registry's order.</summary>
    internal static readonly IReadOnlyList<BuiltInType> BuiltInTypes = [.. Path("records", "builtInTypes").EnumerateObject().Select(t => new BuiltInType(
        t.Name, t.Value.GetProperty("text").GetString()!, [.. t.Value.GetProperty("properties").EnumerateObject().Select(p => new BuiltInProperty(
            p.Name, p.Value.GetProperty("type").GetString()!, p.Value.TryGetProperty("required", out var r) && r.GetBoolean()))]))];
    /// <summary>The relations between built-in types.</summary>
    internal static readonly IReadOnlyList<BuiltInRelation> BuiltInRelations = [.. Path("records", "builtInRelations").EnumerateObject().Select(r => new BuiltInRelation(
        r.Name, r.Value.GetProperty("from").GetString()!, r.Value.GetProperty("to").GetString()!))];
    /// <summary>A confidence's quantiles as the statistic reads them: decimals as written (review F02, M7).</summary>
    internal sealed record TrialQuantiles(string Z, int HedgeQuarters, IReadOnlyList<string> T);
    /// <summary>The statistic's quantiles and hedge weights, by confidence in thousandths: one table for every implementation.</summary>
    internal static readonly IReadOnlyDictionary<int, TrialQuantiles> Quantiles = Path("trial", "statistic", "quantiles").EnumerateObject().ToDictionary(
        p => int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture),
        p => new TrialQuantiles(p.Value.GetProperty("z").GetString()!, p.Value.GetProperty("hedgeQuarters").GetInt32(), [.. p.Value.GetProperty("t").EnumerateArray().Select(t => t.GetString()!)]));
    /// <summary>The number types an embeddings set may hold (feature 03).</summary>
    internal static readonly IReadOnlySet<string> EmbeddingDataTypes = Set("embeddings", "dataTypes");
    /// <summary>The distances an embeddings set may be meant for.</summary>
    internal static readonly IReadOnlySet<string> EmbeddingDistances = Set("embeddings", "distances");
    /// <summary>The code points display-safe text refuses, as sorted inclusive ranges (feature 04, Unicode 15.0).</summary>
    internal static readonly IReadOnlyList<(int First, int Last)> DisplayRefused = [.. Path("text", "displayRefused").EnumerateArray()
        .Select(r => (int.Parse(r[0].GetString()!, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture),
                      int.Parse(r[1].GetString()!, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture)))
        .OrderBy(r => r.Item1)];
    /// <summary>The confidences a trial and a certificate may name, in thousandths.</summary>
    internal static readonly IReadOnlySet<int> Confidences = new HashSet<int>(UnitFields["confidencePermille"].Values!.Select(v => (int)v));
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
        // A key given twice is refused, as the Python verifier refuses it (feature 01 review, F01-9): one meaning per name.
        static void Once(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in e.EnumerateObject())
                {
                    if (!seen.Add(p.Name)) throw new InvalidOperationException($"The format registry repeats the key '{p.Name}'.");
                    Once(p.Value);
                }
            }
            else if (e.ValueKind == JsonValueKind.Array)
                foreach (var item in e.EnumerateArray()) Once(item);
        }
        Once(document.RootElement);
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
