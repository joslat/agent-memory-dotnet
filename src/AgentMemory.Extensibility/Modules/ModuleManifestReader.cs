using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace AgentMemory.Extensibility.Modules;

[Experimental("AMEXT001")]
internal static class ModuleManifestReader
{
    internal static ModuleManifest Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > ModuleManifest.MaximumUtf8Bytes)
            throw Error("$", $"manifest exceeds {ModuleManifest.MaximumUtf8Bytes} UTF-8 bytes");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            RejectDuplicates(document.RootElement, "$");
            return Read(document.RootElement);
        }
        catch (JsonException e) { throw new FormatException("$: malformed or excessively nested manifest JSON.", e); }
    }

    private static ModuleManifest Read(JsonElement root)
    {
        Object(root, "$", "format", "id", "version", "sdk", "requires", "ontology", "views");
        var format = Text(root, "format", "$");
        if (format != "agentmemory.module/v1alpha1") throw Error("$.format", "unsupported module format");
        var id = Text(root, "id", "$");
        if (!ModuleIdentity.IsValidModuleId(id)) throw Error("$.id", "invalid dotted module identity");
        var modules = new Dictionary<string, ModuleVersionRange>(StringComparer.Ordinal);
        IReadOnlyList<string> host = [];
        if (root.TryGetProperty("requires", out var requires))
        {
            Object(requires, "$.requires", "modules", "host");
            if (requires.TryGetProperty("modules", out var dependencies))
            {
                Kind(dependencies, JsonValueKind.Object, "$.requires.modules");
                foreach (var pair in dependencies.EnumerateObject())
                {
                    var path = "$.requires.modules." + pair.Name;
                    if (!ModuleIdentity.IsValidModuleId(pair.Name)) throw Error(path, "invalid dependency identity");
                    modules.Add(pair.Name, Range(String(pair.Value, path), path));
                }
            }
            if (requires.TryGetProperty("host", out var features))
            {
                host = Strings(features, "$.requires.host");
                foreach (var feature in host)
                    if (feature is not ("History" or "Reminders"))
                        throw Error("$.requires.host", $"unknown or derived feature '{feature}'; code may require History or Reminders");
            }
        }
        return new ModuleManifest
        {
            Format = format, Id = id, Version = Version(Text(root, "version", "$"), "$.version"),
            Sdk = Range(Text(root, "sdk", "$"), "$.sdk"), Modules = modules.AsReadOnly(), Host = host,
            Ontology = root.TryGetProperty("ontology", out var ontology) ? Ontology(ontology, "$.ontology") : null,
            Views = Array(root, "views", "$", View)
        };
    }

    private static ModuleOntology Ontology(JsonElement value, string path)
    {
        Object(value, path, "labelPrefix", "types", "relations");
        return new ModuleOntology
        {
            LabelPrefix = Text(value, "labelPrefix", path),
            Types = Array(value, "types", path, Type), Relations = Array(value, "relations", path, Relation)
        };
    }

    private static ModuleType Type(JsonElement value, string path)
    {
        Object(value, path, "name", "scope", "description", "color", "properties", "render");
        var scope = OptionalText(value, "scope", path) ?? "owner";
        if (scope is not ("owner" or "shared")) throw Error(path + ".scope", "expected owner or shared");
        var properties = new Dictionary<string, ModuleProperty>(StringComparer.Ordinal);
        if (value.TryGetProperty("properties", out var props))
        {
            Kind(props, JsonValueKind.Object, path + ".properties");
            foreach (var pair in props.EnumerateObject())
                properties.Add(pair.Name, Property(pair.Value, path + ".properties." + pair.Name));
        }
        return new ModuleType
        {
            Name = Text(value, "name", path), Scope = scope, Description = OptionalText(value, "description", path),
            Color = OptionalText(value, "color", path), Render = OptionalText(value, "render", path),
            Properties = properties.AsReadOnly()
        };
    }

    private static ModuleProperty Property(JsonElement value, string path)
    {
        Object(value, path, "type", "required", "indexed", "values", "maxLength", "min", "max", "description");
        var type = Text(value, "type", path);
        if (type is not ("string" or "int" or "float" or "bool" or "date" or "datetime"))
            throw Error(path + ".type", "unknown property type");
        IReadOnlyList<string> values = [];
        if (value.TryGetProperty("values", out var choices))
        {
            values = Strings(choices, path + ".values");
            if (values.Count == 0) throw Error(path + ".values", "allowed values must not be empty");
        }
        return new ModuleProperty
        {
            Type = type, Required = Boolean(value, "required", path), Indexed = Boolean(value, "indexed", path),
            Values = values, MaxLength = PositiveInteger(value, "maxLength", path),
            Minimum = Number(value, "min", path), Maximum = Number(value, "max", path),
            Description = Description(value, path)
        };
    }

    private static ModuleRelation Relation(JsonElement value, string path)
    {
        Object(value, path, "name", "from", "to", "required", "max", "description");
        return new ModuleRelation
        {
            Name = Text(value, "name", path), From = Text(value, "from", path), To = Text(value, "to", path),
            Required = Boolean(value, "required", path), Max = PositiveInteger(value, "max", path),
            Description = Description(value, path)
        };
    }

    private static ModuleView View(JsonElement value, string path)
    {
        Object(value, path, "id", "type", "where", "orderBy", "limit");
        var id = Text(value, "id", path);
        if (!ModuleIdentity.IsValidLocalId(id)) throw Error(path + ".id", "expected local view identifier");
        return new ModuleView
        {
            Id = id, Type = Text(value, "type", path), Where = Array(value, "where", path, Filter),
            OrderBy = value.TryGetProperty("orderBy", out var sort) ? Order(sort, path + ".orderBy") : null,
            Limit = PositiveInteger(value, "limit", path) ?? throw Error(path + ".limit", "required field missing")
        };
    }

    private static ModuleViewOrder Order(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.String) return new ModuleViewOrder { Property = String(value, path) };
        Object(value, path, "property", "descending");
        return new ModuleViewOrder { Property = Text(value, "property", path), Descending = Boolean(value, "descending", path) };
    }

    private static ModuleViewFilter Filter(JsonElement value, string path)
    {
        Kind(value, JsonValueKind.Array, path);
        if (value.GetArrayLength() != 3) throw Error(path, "filter requires property, operator and value");
        var property = String(value[0], path + "[0]");
        var op = String(value[1], path + "[1]");
        if (op is not ("=" or "!=" or "<" or "<=" or ">" or ">=" or "in"))
            throw Error(path + "[1]", "unknown view operator");
        if (value[2].ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value[2].EnumerateArray()) Literal(item, path + "[2]");
        }
        else Literal(value[2], path + "[2]");
        return new ModuleViewFilter { Property = property, Operator = op, Value = value[2].Clone() };
    }

    private static void Literal(JsonElement value, string path)
    {
        if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
            throw Error(path, "expected scalar literal or supported placeholder");
    }

    private static void Object(JsonElement value, string path, params string[] allowed)
    {
        Kind(value, JsonValueKind.Object, path);
        foreach (var field in value.EnumerateObject())
        {
            if (allowed.Contains(field.Name, StringComparer.Ordinal)) continue;
            if (field.Name == "routing")
                throw Error(path + ".routing", "retired declaration; use all applicable contributors and explicit host selection settings");
            throw Error(path + "." + field.Name, "unknown field");
        }
    }

    private static void RejectDuplicates(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject())
            {
                if (!names.Add(field.Name)) throw Error(path + "." + field.Name, "duplicate field");
                RejectDuplicates(field.Value, path + "." + field.Name);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item, path + $"[{i++}]");
        }
    }

    private static IReadOnlyList<T> Array<T>(JsonElement parent, string name, string path, Func<JsonElement, string, T> read)
    {
        if (!parent.TryGetProperty(name, out var value)) return System.Array.AsReadOnly(System.Array.Empty<T>());
        path += "." + name;
        Kind(value, JsonValueKind.Array, path);
        var result = new List<T>();
        foreach (var item in value.EnumerateArray()) result.Add(read(item, path + $"[{result.Count}]"));
        return result.AsReadOnly();
    }

    private static IReadOnlyList<string> Strings(JsonElement value, string path)
    {
        Kind(value, JsonValueKind.Array, path);
        var result = new List<string>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateArray())
        {
            var text = String(item, path + $"[{result.Count}]");
            if (!unique.Add(text)) throw Error(path, "duplicate value");
            result.Add(text);
        }
        return result.AsReadOnly();
    }

    private static string Text(JsonElement parent, string name, string path) => OptionalText(parent, name, path)
        ?? throw Error(path + "." + name, "required field missing");

    private static string? OptionalText(JsonElement parent, string name, string path) =>
        parent.TryGetProperty(name, out var value) ? String(value, path + "." + name) : null;

    private static string String(JsonElement value, string path)
    {
        Kind(value, JsonValueKind.String, path);
        var result = value.GetString()!;
        if (string.IsNullOrWhiteSpace(result)) throw Error(path, "empty string");
        return result;
    }

    private static string? Description(JsonElement parent, string path)
    {
        var value = OptionalText(parent, "description", path);
        if (value?.Length > 200) throw Error(path + ".description", "description exceeds 200 characters");
        return value;
    }

    private static bool Boolean(JsonElement parent, string name, string path)
    {
        if (!parent.TryGetProperty(name, out var value)) return false;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Error(path + "." + name, "expected Boolean");
        return value.GetBoolean();
    }

    private static int? PositiveInteger(JsonElement parent, string name, string path)
    {
        if (!parent.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number <= 0)
            throw Error(path + "." + name, "expected positive 32-bit integer");
        return number;
    }

    private static JsonElement? Number(JsonElement parent, string name, string path)
    {
        if (!parent.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw Error(path + "." + name, "expected finite number");
        return value.Clone();
    }

    private static ModuleVersion Version(string value, string path)
    {
        try { return ModuleVersion.Parse(value); }
        catch (FormatException e) { throw new FormatException(path + ": invalid module version.", e); }
    }

    private static ModuleVersionRange Range(string value, string path)
    {
        try { return ModuleVersionRange.Parse(value); }
        catch (FormatException e) { throw new FormatException(path + ": invalid version interval.", e); }
    }

    private static void Kind(JsonElement value, JsonValueKind expected, string path)
    {
        if (value.ValueKind != expected) throw Error(path, $"expected {expected}");
    }

    private static FormatException Error(string path, string message) => new(path + ": " + message + ".");
}
