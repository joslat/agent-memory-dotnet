using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentMemory.Packs.Format;

/// <summary>What a host loads from a pack: its module manifest, the record files it seeds, and its icon.</summary>
internal sealed record ModuleImage(byte[] Manifest, IReadOnlyList<string> Seed, string? Icon);

/// <summary>
/// The pack format v1.2 content layout (§2) and its mapping onto the host's module model (§4), both ways, as pure JSON:
/// <c>pack.json</c>, <c>ontology.json</c>, <c>views.json</c>, <c>traversal.json</c>, <c>evals/trial.json</c> and
/// <c>records/*.jsonl</c> become one module manifest with seed files, and an existing module bundle becomes a pack.
/// Nothing here trusts its input: a host maps only a pack that verified, and validates the manifest it gets.
/// </summary>
internal static class PackLayout
{
    internal const string Ontology = "ontology.json";
    internal const string Views = "views.json";
    internal const string Traversal = "traversal.json";
    internal const string Trial = "evals/trial.json";
    internal const string Tests = "evals/tests.json";
    internal const string Records = "records/";
    internal const string Assets = "assets/";
    internal const string Members = "members.json";
    internal const string ModuleFormat = "agentmemory.module/v1alpha1";
    internal const string CompositeFormat = "agentmemory.composite/v1alpha1";
    internal const string ModuleManifestFile = "manifest.json";

    private static readonly JsonWriterOptions Pretty = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The record files of a pack, in path order.</summary>
    internal static IReadOnlyList<string> RecordFiles(IEnumerable<string> paths) =>
        [.. paths.Where(p => p.StartsWith(Records, StringComparison.Ordinal) && p.EndsWith(".jsonl", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

    /// <summary>The module manifest a host composes, from a verified pack's files (v1.2 §4).</summary>
    internal static ModuleImage ToModule(IReadOnlyDictionary<string, byte[]> files)
    {
        // Typed access on input the profile admitted: a value of the wrong type is a refusal, never an escaping exception.
        try { return Map(files); }
        catch (InvalidOperationException e) { throw new InvalidDataException($"A declaration has a value of the wrong type: {e.Message}", e); }
    }

    private static ModuleImage Map(IReadOnlyDictionary<string, byte[]> files)
    {
        var pack = Object(files, PackManifest.FileName) ?? throw new InvalidDataException($"There is no {PackManifest.FileName}.");
        var kind = pack["kind"]?.GetValue<string>();
        if (kind is not ("knowledge" or "definition" or "snapshot"))
            throw new InvalidDataException($"A {kind} pack is not loaded as one module; open its members.");
        var manifest = new JsonObject
        {
            ["format"] = ModuleFormat,
            ["id"] = pack["id"]!.GetValue<string>(),
            ["version"] = pack["version"]!.GetValue<string>(),
            ["sdk"] = pack["sdk"]?.GetValue<string>() ?? throw new InvalidDataException($"{PackManifest.FileName} names no sdk range."),
        };
        foreach (var name in (string[])["requires", "display", "containableBy"])
            if (pack[name] is { } value && !(value is JsonArray { Count: 0 })) manifest[name] = value.DeepClone();
        // The AgentMemory binding of the built-in types (feature 03), here and only here, so every path that composes a pack
        // composes the same declarations and the same declarations digest.
        var recordFiles = RecordFiles(files.Keys).Select(f => (ReadOnlyMemory<byte>)files[f]).ToList();
        if (PackBuiltIns.Bind(Node(files, Ontology), manifest["id"]!.GetValue<string>(), PackBuiltIns.Used(recordFiles), PackBuiltIns.UsedRelations(recordFiles)) is { } ontology)
            manifest["ontology"] = ontology;
        if (Node(files, Views) is { } views) manifest["views"] = views;
        // What the pack shows the model on every turn (feature 06), from its card: one value for the trial, the provider and the
        // listing, and in the declarations digest, since it is a declared cost.
        if (Node(files, PackCard.FileName) is JsonObject card && card["whenToUse"] is JsonValue whenToUse) manifest["whenToUse"] = whenToUse.DeepClone();
        if (Node(files, Traversal) is { } traversal) manifest["traversal"] = traversal;
        if (Node(files, Trial) is { } trial) manifest["trial"] = trial;
        var seed = kind == "knowledge" ? RecordFiles(files.Keys) : [];
        if (kind == "knowledge") manifest["seed"] = new JsonArray([.. seed.Select(s => (JsonNode)s)]);
        var icon = pack["display"]?["icon"]?.GetValue<string>();
        return new ModuleImage(Write(manifest), seed, icon);
    }

    /// <summary>
    /// An existing module bundle (<c>manifest.json</c>, seed files, icon) as a v1.2 pack source folder: a knowledge pack when
    /// it seeds records, a definition pack when it only declares.
    /// </summary>
    internal static Dictionary<string, byte[]> FromModule(IReadOnlyDictionary<string, byte[]> bundle)
    {
        var module = Object(bundle, ModuleManifestFile) ?? throw new InvalidDataException($"There is no {ModuleManifestFile}.");
        if (module["format"]?.GetValue<string>() == CompositeFormat) return FromComposite(module);
        if (module["format"]?.GetValue<string>() != ModuleFormat) throw new InvalidDataException($"{ModuleManifestFile} is not a {ModuleFormat} or {CompositeFormat} manifest.");
        // The host's binding of built-in types is never written into a pack: a pack names them by their built-in names.
        if (module["ontology"] is JsonObject declared && PackBuiltIns.Marked(declared))
            throw new InvalidDataException($"The module's ontology carries '{PackBuiltIns.Marker}', the host's binding of built-in types: write its records with their built-in names and leave them undeclared.");
        var seedFiles = module["seed"] is JsonArray seed ? seed.Select(s => s!.GetValue<string>()).ToList() : null;
        var pack = new JsonObject
        {
            ["format"] = PackManifest.Format,
            ["id"] = module["id"]!.GetValue<string>(),
            ["version"] = module["version"]!.GetValue<string>(),
            ["kind"] = seedFiles is null ? "definition" : "knowledge",
            ["sdk"] = module["sdk"]!.GetValue<string>(),
        };
        if (module["requires"] is { } requires) pack["requires"] = requires.DeepClone();
        string? iconFrom = null, iconTo = null;
        if (module["display"] is JsonObject display)
        {
            var copy = (JsonObject)display.DeepClone();
            if (copy["icon"]?.GetValue<string>() is { } icon)
            {
                iconFrom = icon;
                iconTo = Assets + icon[(icon.LastIndexOf('/') + 1)..];
                copy["icon"] = iconTo;
            }
            pack["display"] = copy;
        }
        if (module["containableBy"] is JsonArray containable) pack["containableBy"] = containable.DeepClone();
        if (module["whenToUse"] is not null)
            throw new InvalidDataException("A pack's whenToUse travels in its card.json (feature 06): write it there, not in the module manifest.");
        pack["tier"] = "open";
        pack["protection"] = "open";
        pack["code"] = "E0";

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [PackManifest.FileName] = Write(pack) };
        if (module["ontology"] is { } ontology) files[Ontology] = Write(ontology);
        if (module["views"] is JsonArray { Count: > 0 } views) files[Views] = Write(views);
        if (module["traversal"] is { } traversal) files[Traversal] = Write(traversal);
        if (module["trial"] is { } trial) files[Trial] = Write(trial);
        foreach (var file in seedFiles ?? [])
        {
            if (!bundle.TryGetValue(file, out var bytes)) throw new InvalidDataException($"The seed file '{file}' is missing.");
            files[Records + file[(file.LastIndexOf('/') + 1)..]] = Lines(bytes);
        }
        if (iconFrom is not null)
            files[iconTo!] = bundle.TryGetValue(iconFrom, out var image) ? image : throw new InvalidDataException($"The icon '{iconFrom}' is missing.");
        return files;
    }

    /// <summary>A composite manifest as a v1.2 composite pack: <c>pack.json</c> (kind composite) and <c>members.json</c>; the lock comes from its members.</summary>
    private static Dictionary<string, byte[]> FromComposite(JsonObject composite)
    {
        var members = composite["members"] as JsonArray ?? throw new InvalidDataException("A composite names its members.");
        var pack = new JsonObject
        {
            ["format"] = PackManifest.Format,
            ["id"] = composite["id"]!.GetValue<string>(),
            ["version"] = composite["version"]!.GetValue<string>(),
            ["kind"] = "composite",
            ["contains"] = new JsonArray([.. members.Select(m => (JsonNode)m!["id"]!.GetValue<string>())]),
        };
        if (composite["display"] is { } display) pack["display"] = display.DeepClone();
        pack["tier"] = "open";
        pack["protection"] = "open";
        pack["code"] = "E0";
        return new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [PackManifest.FileName] = Write(pack),
            [Members] = Write(new JsonObject { ["members"] = members.DeepClone() }),
        };
    }

    /// <summary>The host's composite manifest for a verified composite pack.</summary>
    internal static byte[] ToComposite(IReadOnlyDictionary<string, byte[]> files)
    {
        try { return MapComposite(files); }
        catch (InvalidOperationException e) { throw new InvalidDataException($"A declaration has a value of the wrong type: {e.Message}", e); }
    }

    private static byte[] MapComposite(IReadOnlyDictionary<string, byte[]> files)
    {
        var pack = Object(files, PackManifest.FileName) ?? throw new InvalidDataException($"There is no {PackManifest.FileName}.");
        var members = Object(files, Members) ?? throw new InvalidDataException($"A composite pack carries {Members}.");
        if (members.Select(p => p.Key).FirstOrDefault(k => k != "members") is { } unknown)
            throw new InvalidDataException($"{Members} has an unknown field '{unknown}'; it holds members only.");
        var composite = new JsonObject
        {
            ["format"] = CompositeFormat,
            ["id"] = pack["id"]!.GetValue<string>(),
            ["version"] = pack["version"]!.GetValue<string>(),
        };
        if (pack["display"] is { } display) composite["display"] = display.DeepClone();
        composite["members"] = members["members"]?.DeepClone() ?? throw new InvalidDataException($"{Members} names no members.");
        return Write(composite);
    }

    /// <summary>JSON Lines as the profile wants them: no byte order mark, LF only, a final LF, no blank lines.</summary>
    internal static byte[] Lines(byte[] bytes)
    {
        var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = text.Split('\n').Where(l => l.Length > 0);
        return Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n");
    }

    internal static byte[] Write(JsonNode node)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, Pretty)) node.WriteTo(writer);
        return Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(buffer.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
    }

    private static JsonNode? Node(IReadOnlyDictionary<string, byte[]> files, string path) =>
        files.TryGetValue(path, out var bytes) ? JsonNode.Parse(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF')) : null;

    private static JsonObject? Object(IReadOnlyDictionary<string, byte[]> files, string path) =>
        Node(files, path) as JsonObject ?? (files.ContainsKey(path) ? throw new InvalidDataException($"{path} is not a JSON object.") : null);
}
