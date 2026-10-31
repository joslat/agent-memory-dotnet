using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
#pragma warning disable AMEXT001 // Import helpers used only inside the experimental module API.
using static AgentMemory.Extensibility.Modules.ModuleJson;
#pragma warning restore AMEXT001

namespace AgentMemory.Extensibility.Modules;

[Experimental("AMEXT001")]
internal static class ModuleManifestReader
{
    internal static ModuleManifest Parse(string json)
    {
        using var document = Document(json, ModuleManifest.MaximumUtf8Bytes, "manifest");
        return Read(document.RootElement);
    }

    private static ModuleManifest Read(JsonElement root)
    {
        Object(root, "$", "format", "id", "version", "sdk", "requires", "ontology", "views", "seed", "display", "traversal", "trial", "containableBy");
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
                    if (!ModuleHostFeatures.Declarable.Contains(feature))
                        throw Error("$.requires.host", $"unknown or derived feature '{feature}'; code may require History, Reminders or Inference");
            }
        }
        return new ModuleManifest
        {
            Format = format, Id = id, Version = Version(Text(root, "version", "$"), "$.version"),
            Sdk = Range(Text(root, "sdk", "$"), "$.sdk"), Modules = modules.AsReadOnly(), Host = host,
            Ontology = root.TryGetProperty("ontology", out var ontology) ? Ontology(ontology, "$.ontology") : null,
            Views = Array(root, "views", "$", View),
            Seed = root.TryGetProperty("seed", out var seed) ? Strings(seed, "$.seed") : null,
            Display = root.TryGetProperty("display", out var display) ? Display(display, "$.display") : null,
            Traversal = root.TryGetProperty("traversal", out var traversal) ? Traversal(traversal, "$.traversal") : null,
            Trial = root.TryGetProperty("trial", out var trial) ? Trial(trial, "$.trial") : null,
            ContainableBy = root.TryGetProperty("containableBy", out var containable) ? Strings(containable, "$.containableBy") : null
        };
    }

    // A unit-bearing number (feature 02): an integer. A decimal or an exponent is refused here, at reading, so no reader
    // rounds it; its range (the registry's units) is the validator's, with every other range.
    private static long? Integer(JsonElement value, string name, string path)
    {
        if (!value.TryGetProperty(name, out var n)) return null;
        return PackNumbers.Integer(n) ?? throw Error(path + "." + name, "expected an integer");
    }

    private static ModuleTrial Trial(JsonElement value, string path)
    {
        Object(value, path, [.. PackRegistry.TrialFields]);
        if (!value.TryGetProperty("targets", out var targets)) throw Error(path + ".targets", "a trial needs targets");
        Object(targets, path + ".targets", [.. PackRegistry.TargetFields.Values]);
        long? Target(string name) => Integer(targets, PackRegistry.TargetFields[name], path + ".targets");
        var confidence = Integer(value, "confidencePermille", path);
        ModuleTrialUpgrade? upgrade = null;
        if (value.TryGetProperty("upgrade", out var rule))
        {
            Object(rule, path + ".upgrade", [.. PackRegistry.UpgradeFields]);
            if (!rule.TryGetProperty("noRegression", out var no) || no.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw Error(path + ".upgrade.noRegression", "expected true or false");
            upgrade = new ModuleTrialUpgrade { NoRegression = no.GetBoolean(), ToleranceMicro = Integer(rule, "toleranceMicro", path + ".upgrade") ?? 0 };
        }
        return new ModuleTrial
        {
            Measure = Text(value, "measure", path), Tasks = PositiveInteger(value, "tasks", path) ?? 40,
            Baseline = OptionalText(value, "baseline", path) ?? "none", ConfidencePermille = confidence is { } p ? (int)p : null, Upgrade = upgrade,
            Targets = new ModuleTrialTargets
            {
                HitRateMicro = Target("hitRate"), PrecisionMicro = Target("precision"), LiftMicro = Target("lift"),
                PrecisionLiftMicro = Target("precisionLift"), AddedP95Ms = Target("addedP95Ms"),
            },
        };
    }

    private static ModuleTraversal Traversal(JsonElement value, string path)
    {
        Object(value, path, "anchors", "relations", "hubs", "maxItems", "hops");
        return new ModuleTraversal
        {
            Anchors = Array(value, "anchors", path, TraversalAnchor), Relations = Array(value, "relations", path, TraversalRelation),
            Hubs = value.TryGetProperty("hubs", out var hubs) ? Strings(hubs, path + ".hubs") : [],
            MaxItems = PositiveInteger(value, "maxItems", path) ?? 4,
            Hops = PositiveInteger(value, "hops", path) ?? 2
        };
    }

    private static ModuleTraversalAnchor TraversalAnchor(JsonElement value, string path)
    {
        Object(value, path, "type", "property", "match", "kind");
        return new ModuleTraversalAnchor
        {
            Type = Text(value, "type", path), Property = Text(value, "property", path),
            Match = (OptionalText(value, "match", path) ?? "exact") switch
            {
                "exact" => ModuleAnchorMatch.Exact, "prefix" => ModuleAnchorMatch.Prefix,
                "glob" => ModuleAnchorMatch.Glob, "name" => ModuleAnchorMatch.Name,
                _ => throw Error(path + ".match", "expected exact, prefix, glob or name")
            },
            Kind = (OptionalText(value, "kind", path) ?? "path") switch
            {
                "path" => Context.ContextAnchorKind.Path, "symbol" => Context.ContextAnchorKind.Symbol,
                _ => throw Error(path + ".kind", "expected path or symbol")
            }
        };
    }

    private static ModuleTraversalRelation TraversalRelation(JsonElement value, string path)
    {
        Object(value, path, [.. PackRegistry.RelationFields]);
        var weight = Integer(value, "weightMicro", path);
        return new ModuleTraversalRelation
        {
            Name = Text(value, "name", path),
            Direction = (OptionalText(value, "direction", path) ?? "out") switch
            {
                "out" => ModuleTraversalDirection.Out, "in" => ModuleTraversalDirection.In,
                _ => throw Error(path + ".direction", "expected out or in")
            },
            WeightMicro = weight ?? PackRegistry.DefaultWeightMicro, FanOut = PositiveInteger(value, "fanOut", path) ?? 8
        };
    }

    private static ModuleDisplay Display(JsonElement value, string path)
    {
        Object(value, path, "title", "description", "icon", "color");
        return new ModuleDisplay
        {
            Title = OptionalText(value, "title", path), Description = OptionalText(value, "description", path),
            Icon = OptionalText(value, "icon", path), Color = OptionalText(value, "color", path)
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
        Object(value, path, "name", "scope", "description", "color", "properties", "render", "text", "memoryKind", PackBuiltIns.Marker);
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
            Text = OptionalText(value, "text", path), MemoryKind = OptionalText(value, "memoryKind", path), BuiltIn = OptionalText(value, PackBuiltIns.Marker, path),
            Properties = properties.AsReadOnly()
        };
    }

    private static ModuleProperty Property(JsonElement value, string path)
    {
        Object(value, path, "type", "required", "indexed", "values", "maxLength", "min", "max", "description");
        var type = Text(value, "type", path);
        if (!PackRegistry.PropertyTypes.Contains(type)) throw Error(path + ".type", "unknown property type");
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
        Object(value, path, "name", "from", "to", "required", "max", "description", PackBuiltIns.Marker);
        return new ModuleRelation
        {
            Name = Text(value, "name", path), From = Text(value, "from", path), To = Text(value, "to", path), BuiltIn = OptionalText(value, PackBuiltIns.Marker, path),
            Required = Boolean(value, "required", path), Max = PositiveInteger(value, "max", path),
            Description = Description(value, path)
        };
    }

    private static ModuleView View(JsonElement value, string path)
    {
        Object(value, path, "id", "type", "where", "orderBy", "limit", "selection", "search");
        var id = Text(value, "id", path);
        if (!ModuleIdentity.IsValidLocalId(id)) throw Error(path + ".id", "expected local view identifier");
        return new ModuleView
        {
            Id = id, Type = Text(value, "type", path), Where = Array(value, "where", path, Filter),
            OrderBy = value.TryGetProperty("orderBy", out var sort) ? Order(sort, path + ".orderBy") : null,
            Selection = value.TryGetProperty("selection", out var selection) ? String(selection, path + ".selection") switch
            {
                "snapshot" => ModuleRecordSelection.Snapshot,
                "history" => ModuleRecordSelection.History,
                _ => throw Error(path + ".selection", "expected snapshot or history")
            } : ModuleRecordSelection.Snapshot,
            Limit = PositiveInteger(value, "limit", path) ?? throw Error(path + ".limit", "required field missing"),
            Search = value.TryGetProperty("search", out var search) ? Search(search, path + ".search") : null
        };
    }

    private static ModuleViewSearch Search(JsonElement value, string path)
    {
        Object(value, path, "properties");
        if (!value.TryGetProperty("properties", out var properties)) throw Error(path + ".properties", "required field missing");
        return new ModuleViewSearch { Properties = Strings(properties, path + ".properties") };
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

}
