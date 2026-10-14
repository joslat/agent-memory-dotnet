using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentMemory.Extensibility.Modules;

[Experimental("AMEXT001")]
internal static class ModuleRenderTemplate
{
    internal static readonly Regex Parts = new(@"\{\{|\}\}|\{([A-Za-z][A-Za-z0-9_]*)(?::([^{}]+))?\}|[{}]",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static string Render(ModuleType type, IReadOnlyDictionary<string, JsonElement> properties)
    {
        string Field(string name, string? format)
        {
            if (!properties.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                if (type.Properties[name].Required) throw new FormatException("Required render property is absent: " + name);
                return string.Empty;
            }
            return type.Properties[name].Type switch
            {
                "string" => value.GetString()!,
                "int" => value.GetInt64().ToString(format, CultureInfo.InvariantCulture),
                "float" => value.GetDouble().ToString(format, CultureInfo.InvariantCulture),
                "bool" => value.GetBoolean() ? "true" : "false",
                "date" => DateOnly.ParseExact(value.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString(format ?? "yyyy-MM-dd", CultureInfo.InvariantCulture),
                "datetime" => DateTimeOffset.Parse(value.GetString()!, CultureInfo.InvariantCulture).ToUniversalTime().ToString(format ?? "O", CultureInfo.InvariantCulture),
                _ => throw new FormatException("Unsupported render property type.")
            };
        }
        if (type.Render is not { } template)
            return string.Join("; ", type.Properties.Keys.OrderBy(k => k, StringComparer.Ordinal)
                .Where(properties.ContainsKey).Select(key => key + "=" + Field(key, null)));
        // Replace once. Braces in stored text are data and cannot introduce new placeholders.
        return Parts.Replace(template, match => match.Value switch
        {
            "{{" => "{", "}}" => "}",
            _ when match.Groups[1].Success => Field(match.Groups[1].Value, match.Groups[2].Success ? match.Groups[2].Value : null),
            _ => throw new FormatException("Malformed render placeholder.")
        });
    }
}
