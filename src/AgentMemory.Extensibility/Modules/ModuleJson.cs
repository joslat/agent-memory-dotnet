using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace AgentMemory.Extensibility.Modules;

[Experimental("AMEXT001")]
internal static class ModuleJson
{
    internal static JsonDocument Document(string json, int maximumUtf8Bytes, string kind)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > maximumUtf8Bytes)
            throw Error("$", $"{kind} exceeds {maximumUtf8Bytes} UTF-8 bytes");
        try
        {
            var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            try { RejectDuplicates(document.RootElement, "$"); return document; }
            catch { document.Dispose(); throw; }
        }
        catch (JsonException e) { throw new FormatException($"$: malformed or excessively nested {kind} JSON.", e); }
    }

    internal static void Object(JsonElement value, string path, params string[] allowed)
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

    internal static void RejectDuplicates(JsonElement value, string path)
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

    internal static IReadOnlyList<T> Array<T>(JsonElement parent, string name, string path, Func<JsonElement, string, T> read)
    {
        if (!parent.TryGetProperty(name, out var value)) return System.Array.AsReadOnly(System.Array.Empty<T>());
        path += "." + name;
        Kind(value, JsonValueKind.Array, path);
        var result = new List<T>();
        foreach (var item in value.EnumerateArray()) result.Add(read(item, path + $"[{result.Count}]"));
        return result.AsReadOnly();
    }

    internal static IReadOnlyList<string> Strings(JsonElement value, string path)
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

    internal static string Text(JsonElement parent, string name, string path) => OptionalText(parent, name, path)
        ?? throw Error(path + "." + name, "required field missing");

    internal static string? OptionalText(JsonElement parent, string name, string path) =>
        parent.TryGetProperty(name, out var value) ? String(value, path + "." + name) : null;

    internal static string String(JsonElement value, string path)
    {
        Kind(value, JsonValueKind.String, path);
        var result = value.GetString()!;
        if (string.IsNullOrWhiteSpace(result)) throw Error(path, "empty string");
        return result;
    }

    internal static string? Description(JsonElement parent, string path)
    {
        var value = OptionalText(parent, "description", path);
        if (value?.Length > 200) throw Error(path + ".description", "description exceeds 200 characters");
        return value;
    }

    internal static bool Boolean(JsonElement parent, string name, string path)
    {
        if (!parent.TryGetProperty(name, out var value)) return false;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Error(path + "." + name, "expected Boolean");
        return value.GetBoolean();
    }

    internal static int? PositiveInteger(JsonElement parent, string name, string path)
    {
        if (!parent.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number <= 0)
            throw Error(path + "." + name, "expected positive 32-bit integer");
        return number;
    }

    internal static JsonElement? Number(JsonElement parent, string name, string path)
    {
        if (!parent.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw Error(path + "." + name, "expected finite number");
        return value.Clone();
    }

    internal static ModuleVersion Version(string value, string path)
    {
        try { return ModuleVersion.Parse(value); }
        catch (FormatException e) { throw new FormatException(path + ": invalid module version.", e); }
    }

    internal static ModuleVersionRange Range(string value, string path)
    {
        try { return ModuleVersionRange.Parse(value); }
        catch (FormatException e) { throw new FormatException(path + ": invalid version interval.", e); }
    }

    internal static void Kind(JsonElement value, JsonValueKind expected, string path)
    {
        if (value.ValueKind != expected) throw Error(path, $"expected {expected}");
    }

    internal static FormatException Error(string path, string message) => new(path + ": " + message + ".");
}
