using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// One source compiled into both the pack format and the module host, as PackRegistry.cs is.
#if AGENTMEMORY_PACKS_FORMAT
namespace AgentMemory.Packs.Format;
#else
namespace AgentMemory.Extensibility;
#endif

/// <summary>
/// The AgentMemory binding of the built-in record types (feature 03; <c>spec/host/agentmemory-binding.md</c>): a pack names
/// them by their built-in names (<c>note</c>, <c>part_of</c>), and AgentMemory stores them under the module's prefix, as every
/// type it stores is (<c>&lt;Prefix&gt;Note</c>, <c>&lt;PREFIX&gt;_PART_OF</c>), shared. The types themselves are the registry's.
/// </summary>
internal static partial class PackBuiltIns
{
    /// <summary>The member a bound type or relation carries naming its built-in: the host's own, never a pack author's.</summary>
    internal const string Marker = "builtIn";

    /// <summary>A PascalCase or camelCase name in snake case (<c>LanternNotes</c> → <c>lantern_notes</c>): the schema's own names.</summary>
    internal static string SchemaName(string value) => Regex.Replace(Regex.Replace(value,
        "([A-Z]+)([A-Z][a-z])", "$1_$2", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking),
        "([a-z0-9])([A-Z])", "$1_$2", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking).ToLowerInvariant();

    /// <summary>A built-in type as AgentMemory stores it: the prefix and the name in PascalCase.</summary>
    internal static string StoredType(string prefix, string builtIn) => prefix + Pascal(builtIn);

    /// <summary>A built-in relation as AgentMemory stores it: the prefix in upper snake case, then the name.</summary>
    internal static string StoredRelation(string prefix, string builtIn) => SchemaName(prefix).ToUpperInvariant() + "_" + builtIn.ToUpperInvariant();

    /// <summary>The prefix a pack that declares no ontology takes: its id's last segment in PascalCase.</summary>
    internal static string ImpliedPrefix(string packId) => Pascal(packId[(packId.LastIndexOf('.') + 1)..].Replace('-', '_'));

    private static string Pascal(string name) =>
        string.Concat(name.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    /// <summary>The built-in types the record lines name.</summary>
    internal static IReadOnlySet<string> Used(IEnumerable<ReadOnlyMemory<byte>> recordFiles)
    {
        var names = PackRegistry.BuiltInTypes.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        return Named(recordFiles).Select(r => r.Type).Where(names.Contains).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The (type, key) each record line names, in order, read leniently from a verified pack's record files (every line already
    /// in the JSON profile): a line that is not a record is the record reader's to refuse, and is skipped here.
    /// </summary>
    internal static IEnumerable<(string Type, string Key)> Named(IEnumerable<ReadOnlyMemory<byte>> recordFiles)
    {
        foreach (var file in recordFiles)
            foreach (var line in System.Text.Encoding.UTF8.GetString(file.Span).Split('\n'))
            {
                if (line.Length == 0) continue;
                (string, string)? named = null;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
                        named = (type.GetString()!, root.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String ? key.GetString()! : "");
                }
                catch (JsonException) { }
                if (named is { } record) yield return record;
            }
    }

    /// <summary>
    /// The pack's ontology with the built-in types it uses bound in (with the types their relations lead to), or the ontology
    /// as it is when it uses none. A pack without an ontology takes the implied prefix. A pack's own ontology may not carry the
    /// host's marker.
    /// </summary>
    internal static JsonObject? Bind(JsonObject? ontology, string packId, IReadOnlySet<string> used)
    {
        if (ontology is not null && Marked(ontology)) throw new InvalidDataException($"ontology.json carries '{Marker}', which is the host's own: a pack names built-in types by their names.");
        if (used.Count == 0) return ontology;
        var bound = (JsonObject?)ontology?.DeepClone() ?? new JsonObject { ["labelPrefix"] = ImpliedPrefix(packId) };
        var prefix = bound["labelPrefix"]?.GetValue<string>() ?? throw new InvalidDataException("ontology.json names no labelPrefix.");
        var types = bound["types"] as JsonArray ?? (JsonArray)(bound["types"] = new JsonArray());
        var relations = bound["relations"] as JsonArray ?? (JsonArray)(bound["relations"] = new JsonArray());
        var needed = new HashSet<string>(used, StringComparer.Ordinal);
        foreach (var relation in PackRegistry.BuiltInRelations.Where(r => used.Contains(r.From))) needed.Add(relation.To);
        foreach (var type in PackRegistry.BuiltInTypes.Where(t => needed.Contains(t.Name)))
            types.Add((JsonNode)new JsonObject
            {
                ["name"] = StoredType(prefix, type.Name), ["scope"] = "shared", [Marker] = type.Name, ["text"] = type.Text,
                ["properties"] = new JsonObject(type.Properties.Select(p => KeyValuePair.Create(p.Name,
                    (JsonNode?)new JsonObject { ["type"] = p.Type, ["required"] = p.Required }))),
            });
        foreach (var relation in PackRegistry.BuiltInRelations.Where(r => needed.Contains(r.From)))
            relations.Add((JsonNode)new JsonObject
            {
                ["name"] = StoredRelation(prefix, relation.Name), [Marker] = relation.Name,
                ["from"] = StoredType(prefix, relation.From), ["to"] = StoredType(prefix, relation.To),
            });
        return bound;
    }

    /// <summary>Whether an ontology's types or relations carry the host's marker.</summary>
    internal static bool Marked(JsonObject ontology) =>
        new[] { ontology["types"], ontology["relations"] }.OfType<JsonArray>().SelectMany(a => a).OfType<JsonObject>().Any(o => o.ContainsKey(Marker));
}
