using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentMemory.Extensibility.Modules;

[Experimental("AMEXT001")]
internal sealed class ModuleManifestValidator
{
    private static readonly Regex Label = Pattern(@"\A[A-Z][A-Za-z0-9]*\z");
    private static readonly Regex PropertyName = Pattern(@"\A[A-Za-z][A-Za-z0-9_]*\z");
    private static readonly Regex Relationship = Pattern(@"\A[A-Z][A-Z0-9]*(?:_[A-Z0-9]+)*\z");
    private static readonly FrozenSet<string> ReservedLabels = ModuleReservedNames.Labels.Select(SchemaName).ToFrozenSet(StringComparer.Ordinal);
    private readonly SortedSet<string> _errors = new(StringComparer.Ordinal);

    internal IReadOnlyList<string> Validate(ModuleManifest manifest)
    {
        if (manifest.Format != "agentmemory.module/v1alpha1") Error("$.format", "unsupported format");
        if (!ModuleIdentity.IsValidModuleId(manifest.Id)) Error("$.id", "invalid module identifier");
        if (manifest.Version is null) Error("$.version", "required version missing");
        if (manifest.Sdk is null) Error("$.sdk", "required SDK interval missing");
        var modules = Map(manifest.Modules, "$.requires.modules");
        foreach (var pair in modules)
        {
            if (!ModuleIdentity.IsValidModuleId(pair.Key)) Error("$.requires.modules", "invalid dependency identifier");
            if (pair.Key == manifest.Id) Error("$.requires.modules", "module cannot depend on itself");
            if (pair.Value is null) Error("$.requires.modules." + pair.Key, "required dependency range missing");
        }
        var host = List(manifest.Host, "$.requires.host");
        if (host.Distinct(StringComparer.Ordinal).Count() != host.Count) Error("$.requires.host", "duplicate host feature");
        foreach (var feature in host)
            if (feature is not ("History" or "Reminders")) Error("$.requires.host", "unknown or derived host feature");
        var views = List(manifest.Views, "$.views");
        var types = new Dictionary<string, ModuleType>(StringComparer.Ordinal);
        if (manifest.Ontology is { } ontology)
        {
            var prefix = ontology.LabelPrefix ?? "";
            if (!Label.IsMatch(prefix)) Error("$.ontology.labelPrefix", "expected PascalCase label prefix");
            var names = new HashSet<string>(StringComparer.Ordinal);
            var schemaNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var type in List(ontology.Types, "$.ontology.types"))
            {
                if (type is null) { Error("$.ontology.types", "null type declaration"); continue; }
                var name = type.Name ?? "";
                var path = "$.ontology.types." + name;
                ValidateOwnedLabel(name, prefix, path);
                if (!types.TryAdd(name, type)) Error(path, "duplicate type name");
                if (!names.Add(SchemaName(name))) Error(path, "type names collide after schema-name normalization");
                if (type.Scope is not ("owner" or "shared")) Error(path + ".scope", "expected owner or shared");
                if (type.Description is not null && string.IsNullOrWhiteSpace(type.Description))
                    Error(path + ".description", "description cannot be empty");
                if (type.Color is not null && string.IsNullOrWhiteSpace(type.Color))
                    Error(path + ".color", "color cannot be empty");
                var props = Map(type.Properties, path + ".properties");
                foreach (var pair in props)
                {
                    var at = path + ".properties." + pair.Key;
                    ValidateWritableProperty(pair.Key, at);
                    if (pair.Value is null) Error(at, "null property declaration");
                    else ValidateProperty(pair.Value, at);
                }
                if (type.Render is { } render) ValidateRender(render, props, path + ".render");
                foreach (var shape in ModuleSchemaObjects.ForType(type))
                {
                    if (!schemaNames.Add(shape.Name)) Error(path, $"generated schema object '{shape.Name}' collides");
                    if (ModuleReservedNames.SchemaObjects.Contains(shape.Name)) Error(path, $"reserved schema object '{shape.Name}'");
                }
            }
            var relations = new HashSet<string>(StringComparer.Ordinal);
            foreach (var relation in List(ontology.Relations, "$.ontology.relations"))
            {
                if (relation is null) { Error("$.ontology.relations", "null relationship declaration"); continue; }
                var path = "$.ontology.relations." + relation.Name;
                var name = relation.Name ?? "";
                ValidateOwnedRelationship(name, prefix, path);
                if (!relations.Add(name)) Error(path, "duplicate relationship name");
                if (!types.ContainsKey(relation.From ?? "")) Error(path + ".from", "source must be an owned type");
                ValidateTarget(relation.To, types, modules, path + ".to");
                if (relation.Max is <= 0) Error(path + ".max", "maximum must be positive");
                Description(relation.Description, path + ".description");
            }
        }
        else if (views.Count != 0) Error("$.views", "views require an owned ontology");
        var viewIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var view in views)
        {
            if (view is null) { Error("$.views", "null view declaration"); continue; }
            var path = "$.views." + view.Id;
            if (!ModuleIdentity.IsValidLocalId(view.Id)) Error(path, "invalid local view identifier");
            if (!viewIds.Add(view.Id ?? "")) Error(path, "duplicate view identifier");
            if (view.Limit <= 0) Error(path + ".limit", "view limit must be positive");
            if (!types.TryGetValue(view.Type ?? "", out var type)) { Error(path + ".type", "view must name an owned type"); continue; }
            var props = Map(type.Properties, "$.ontology.types." + type.Name + ".properties");
            if (view.OrderBy is { } order && !props.ContainsKey(order.Property ?? ""))
                Error(path + ".orderBy", "sort must name a declared property");
            foreach (var filter in List(view.Where, path + ".where"))
                ValidateFilter(filter, props, path + ".where");
        }
        return _errors.ToList().AsReadOnly();
    }

    internal IReadOnlyList<string> ValidateReferences(ModuleManifest manifest, IReadOnlyList<string> labels,
        IReadOnlyList<string> relationships, IReadOnlyList<string> objects, IReadOnlyList<string> writtenProperties)
    {
        Validate(manifest);
        if (manifest.Ontology is not { } ontology)
        {
            if (labels.Count != 0 || relationships.Count != 0 || objects.Count != 0 || writtenProperties.Count != 0)
                Error("$.references", "schema references require an owned ontology");
        }
        else
        {
            var prefix = ontology.LabelPrefix ?? "";
            foreach (var name in labels) ValidateOwnedLabel(name ?? "", prefix, "$.references.labels." + name);
            foreach (var name in relationships) ValidateOwnedRelationship(name ?? "", prefix, "$.references.relationships." + name);
            foreach (var name in writtenProperties) ValidateWritableProperty(name ?? "", "$.references.writtenProperties." + name);
            foreach (var name in objects)
            {
                var path = "$.references.objects." + name;
                if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl)) Error(path, "invalid schema object identifier");
                else if (ModuleReservedNames.SchemaObjects.Contains(name)) Error(path, "reserved schema object");
            }
        }
        return _errors.ToList().AsReadOnly();
    }

    private void ValidateWritableProperty(string name, string path)
    {
        if (!PropertyName.IsMatch(name)) Error(path, "invalid property identifier");
        if (ModuleReservedNames.Properties.Contains(name)) Error(path, "reserved SDK property");
    }

    private void ValidateOwnedLabel(string name, string prefix, string path)
    {
        if (!Label.IsMatch(name)) Error(path, "invalid type identifier");
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) Error(path, "type must carry its label prefix");
        if (ReservedLabels.Contains(SchemaName(name))) Error(path, "reserved base, upstream or SDK label");
    }

    private void ValidateOwnedRelationship(string name, string prefix, string path)
    {
        if (!Relationship.IsMatch(name) || !name.StartsWith(SchemaName(prefix).ToUpperInvariant() + "_", StringComparison.Ordinal))
            Error(path, "relationship must carry its UPPER_SNAKE prefix and suffix");
        if (ModuleReservedNames.Relationships.Contains(name)) Error(path, "reserved relationship type");
    }

    private void ValidateTarget(string? target, IReadOnlyDictionary<string, ModuleType> types,
        IReadOnlyDictionary<string, ModuleVersionRange> modules, string path)
    {
        if (target == "core:Entity" || types.ContainsKey(target ?? "")) return;
        var parts = (target ?? "").Split(':');
        if (parts.Length != 2 || !ModuleIdentity.IsValidModuleId(parts[0]) || !Label.IsMatch(parts[1]))
            Error(path, "target must be own type, core:Entity or a qualified dependency type");
        else if (!modules.ContainsKey(parts[0])) Error(path, "target module must be a declared dependency");
    }

    private void ValidateRender(string render, IReadOnlyDictionary<string, ModuleProperty> properties, string path)
    {
        if (string.IsNullOrWhiteSpace(render)) Error(path, "empty render template");
        foreach (Match part in ModuleRenderTemplate.Parts.Matches(render))
        {
            if (part.Value is "{{" or "}}") continue;
            if (!part.Groups[1].Success) Error(path, "unbalanced or malformed render placeholder");
            else if (!properties.ContainsKey(part.Groups[1].Value)) Error(path, "render placeholder must name a declared property");
        }
    }

    internal static string SchemaName(string value) => Regex.Replace(Regex.Replace(value,
        "([A-Z]+)([A-Z][a-z])", "$1_$2", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking),
        "([a-z0-9])([A-Z])", "$1_$2", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking).ToLowerInvariant();
    private static Regex Pattern(string pattern) => new(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private void Error(string path, string message) => _errors.Add(path + ": " + message + ".");
    private IReadOnlyList<T> List<T>(IReadOnlyList<T>? value, string path)
    {
        if (value is not null) return value;
        Error(path, "collection cannot be null"); return [];
    }
    private IReadOnlyDictionary<string, T> Map<T>(IReadOnlyDictionary<string, T>? value, string path)
    {
        if (value is not null) return value.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        Error(path, "map cannot be null"); return new Dictionary<string, T>();
    }
    private void Description(string? text, string path)
    {
        if (text is not null && (string.IsNullOrWhiteSpace(text) || text.Length > 200)) Error(path, "description must have 1-200 characters");
    }

    private void ValidateProperty(ModuleProperty property, string path)
    {
        if (property.Type is not ("string" or "int" or "float" or "bool" or "date" or "datetime"))
            Error(path + ".type", "unknown property type");
        var values = List(property.Values, path + ".values");
        if (values.Count != 0 && property.Type != "string") Error(path + ".values", "enumeration requires string type");
        if (values.Any(string.IsNullOrWhiteSpace) || values.Distinct(StringComparer.Ordinal).Count() != values.Count)
            Error(path + ".values", "allowed values must be nonempty and unique");
        if (property.MaxLength is { } length)
        {
            if (property.Type != "string" || length <= 0) Error(path + ".maxLength", "positive maximum length requires string type");
            if (values.Any(v => v is not null && v.Length > length)) Error(path + ".values", "allowed value exceeds maximum length");
        }
        Description(property.Description, path + ".description");
        if (property.Minimum is null && property.Maximum is null) return;
        if (property.Type is not ("int" or "float")) { Error(path, "numeric bounds require int or float type"); return; }
        var min = property.Minimum;
        var max = property.Maximum;
        var validMin = min is null || ModuleScalarValues.NumberFits(min.Value, property.Type);
        var validMax = max is null || ModuleScalarValues.NumberFits(max.Value, property.Type);
        if (!validMin) Error(path + ".min", "minimum must fit the declared numeric type");
        if (!validMax) Error(path + ".max", "maximum must fit the declared numeric type");
        if (validMin && validMax && min is not null && max is not null
            && (property.Type == "int" ? min.Value.GetInt64() > max.Value.GetInt64() : min.Value.GetDouble() > max.Value.GetDouble()))
            Error(path, "minimum exceeds maximum");
    }

    private void ValidateFilter(ModuleViewFilter? filter, IReadOnlyDictionary<string, ModuleProperty> properties, string path)
    {
        if (filter is null) { Error(path, "null filter"); return; }
        path += "." + filter.Property;
        if (!properties.TryGetValue(filter.Property ?? "", out var property) || property is null)
        { Error(path, "filter must name a declared property"); return; }
        if (filter.Operator is not ("=" or "!=" or "<" or "<=" or ">" or ">=" or "in"))
        { Error(path, "unknown filter operator"); return; }
        if (property.Type == "bool" && filter.Operator is not ("=" or "!=" or "in"))
            Error(path, "Boolean filter supports equality or membership only");
        try
        {
            if (filter.Operator == "in")
            {
                if (filter.Value.ValueKind != JsonValueKind.Array) Error(path, "membership requires an array of typed literals");
                else if (filter.Value.EnumerateArray().Any(v => !ModuleScalarValues.ValueFits(v, property.Type, allowNull: false)))
                    Error(path, "membership element does not fit the property type");
            }
            else if (!ModuleScalarValues.ValueFits(filter.Value, property.Type, filter.Operator is "=" or "!="))
                Error(path, "filter value or placeholder does not fit the property type");
        }
        catch (ObjectDisposedException) { Error(path, "filter JSON document has been disposed"); }
    }

}
