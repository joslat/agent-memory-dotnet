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
            if (!ModuleHostFeatures.Declarable.Contains(feature)) Error("$.requires.host", "unknown or derived host feature");
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
                // How its records age (feature 07): a known lifecycle, ttlDays exactly for refreshes, 1 to 36,500 days.
                if (type.Lifecycle is { } lifecycle && !PackRegistry.Lifecycles.Contains(lifecycle))
                    Error(path + ".lifecycle", "expected " + string.Join(" or ", PackRegistry.Lifecycles.Order(StringComparer.Ordinal)));
                if (type.Lifecycle == "refreshes" && type.TtlDays is not (>= 1 and <= 36_500)) Error(path + ".ttlDays", "a refreshes type names ttlDays, 1 to 36,500");
                if (type.Lifecycle != "refreshes" && type.TtlDays is not null) Error(path + ".ttlDays", "only a refreshes type names ttlDays");
                // Shown to a person and to a model (the module proposer's prompt), so display text (feature 04 review, M8).
                if (type.Description is not null && !PackText.IsDisplayText(type.Description, int.MaxValue, line: false))
                    Error(path + ".description", "a description is display-safe text, not empty");
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
                // Feature 03: the main text is one of the type's string properties; a memory kind is the registry's; a bound
                // built-in is exactly the registry's, under this module's prefix.
                if (type.Text is { } text && !(props.TryGetValue(text, out var main) && main?.Type == "string"))
                    Error(path + ".text", "the main text must name one of the type's string properties");
                if (type.MemoryKind is { } kind && !PackRegistry.MemoryKinds.Contains(kind))
                    Error(path + ".memoryKind", "expected " + string.Join(", ", PackRegistry.MemoryKinds));
                if (type.BuiltIn is { } builtIn)
                {
                    var known = PackRegistry.BuiltInTypes.FirstOrDefault(b => b.Name == builtIn);
                    if (known is null || name != PackBuiltIns.StoredType(prefix, builtIn) || type.Scope != "shared"
                        || props.Count != known.Properties.Count || known.Properties.Any(p => !props.TryGetValue(p.Name, out var q) || q?.Type != p.Type || q.Required != p.Required))
                        Error(path, $"is not the built-in type '{builtIn}' as this host binds it");
                }
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
                if (relation.BuiltIn is { } builtIn && (PackRegistry.BuiltInRelations.FirstOrDefault(b => b.Name == builtIn) is not { } known
                        || name != PackBuiltIns.StoredRelation(prefix, builtIn) || relation.From != PackBuiltIns.StoredType(prefix, known.From)
                        || relation.To != PackBuiltIns.StoredType(prefix, known.To)))
                    Error(path, $"is not the built-in relation '{builtIn}' as this host binds it");
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
            if (view.Summary is { } summary && !PackText.IsDisplayText(summary, 160, line: false))
                Error(path + ".summary", "a view's summary is 1 to 160 display-safe characters (feature 06)");
            if (view.Selection is not (ModuleRecordSelection.Snapshot or ModuleRecordSelection.History))
                Error(path + ".selection", "unknown record selection");
            if (view.Selection == ModuleRecordSelection.History && view.OrderBy is not null)
                Error(path + ".orderBy", "history has fixed newest-version-first ordering");
            if (view.Limit <= 0) Error(path + ".limit", "view limit must be positive");
            if (!types.TryGetValue(view.Type ?? "", out var type)) { Error(path + ".type", "view must name an owned type"); continue; }
            var props = Map(type.Properties, "$.ontology.types." + type.Name + ".properties");
            if (view.OrderBy is { } order && !props.ContainsKey(order.Property ?? ""))
                Error(path + ".orderBy", "sort must name a declared property");
            foreach (var filter in List(view.Where, path + ".where"))
                ValidateFilter(filter, props, path + ".where");
            if (view.Search is { } search) ValidateSearch(view, search, props, path + ".search");
        }
        var searchNames = new HashSet<string>(StringComparer.Ordinal);
        var typeShapes = types.Values.SelectMany(ModuleSchemaObjects.ForType).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var shape in ModuleSchemaObjects.ForSearch(manifest))
        {
            if (!searchNames.Add(shape.Name) || typeShapes.Contains(shape.Name)) Error("$.views", $"generated schema object '{shape.Name}' collides");
            if (ModuleReservedNames.SchemaObjects.Contains(shape.Name)) Error("$.views", $"reserved schema object '{shape.Name}'");
        }
        if (manifest.Seed is { } seed) ValidateKnowledgePack(manifest, seed, host, modules);
        if (manifest.Traversal is { } traversal) ValidateTraversal(traversal, manifest.Ontology, types);
        if (manifest.Trial is { } trial) ValidateTrial(trial);
        foreach (var container in manifest.ContainableBy ?? [])
            if (!ModuleIdentity.IsValidModuleId(container)) Error("$.containableBy", $"'{container}' is not a dotted module identity or namespace");
        if (manifest.Display is { } display) ValidateDisplay(manifest, display);
        if (manifest.WhenToUse is { } whenToUse && !PackText.IsDisplayText(whenToUse, 400, line: false))
            Error("$.whenToUse", "whenToUse is 1 to 400 display-safe characters (feature 06)");
        return _errors.ToList().AsReadOnly();
    }

    private static readonly Regex HexColor = Pattern(@"\A#[0-9A-Fa-f]{6}\z");

    // Extensibility 0.13 §D: what a person sees of a module or pack; bounded, and an icon only where a signed bundle holds it.
    private void ValidateDisplay(ModuleManifest manifest, ModuleDisplay display)
    {
        if (display.Title is { } title && !PackText.IsDisplayText(title, 60, line: true))
            Error("$.display.title", "a title is 1 to 60 characters without control characters");
        if (display.Description is { } description && !PackText.IsDisplayText(description, 300, line: false))
            Error("$.display.description", "a description is 1 to 300 display-safe characters");
        if (display.Color is { } color && !HexColor.IsMatch(color)) Error("$.display.color", "a colour is #RRGGBB");
        if (display.Icon is { } icon)
        {
            if (!manifest.IsKnowledgePack) Error("$.display.icon", "only a knowledge pack's signed bundle can carry an icon");
            if (!icon.EndsWith(".png", StringComparison.Ordinal) || !Bundles.BundleSigning.IsSafePath(icon) || (manifest.Seed?.Contains(icon) ?? false))
                Error("$.display.icon", "an icon is a safe relative .png path in the bundle");
        }
    }

    // Extensibility 0.13 §K4: a search view returns the best matches for a request's text, so it has its own order and bound.
    private void ValidateSearch(ModuleView view, ModuleViewSearch search, IReadOnlyDictionary<string, ModuleProperty> props, string path)
    {
        var properties = List(search.Properties, path + ".properties");
        if (properties.Count is 0 or > 8) Error(path + ".properties", "search requires 1 to 8 properties");
        if (properties.Distinct(StringComparer.Ordinal).Count() != properties.Count) Error(path + ".properties", "duplicate search property");
        foreach (var name in properties)
            if (!props.TryGetValue(name ?? "", out var declared) || declared?.Type != "string")
                Error(path + ".properties", "search properties must be declared string properties");
        if (view.Selection != ModuleRecordSelection.Snapshot) Error(path, "search reads current records only");
        if (view.OrderBy is not null) Error(path, "search results are ordered by score");
        if (view.Limit > ModuleViewSearch.MaximumLimit) Error(path, $"search returns at most {ModuleViewSearch.MaximumLimit} records");
    }

    // Extensibility 0.13 §K1: a knowledge pack is data only. Its records are shared, imported by the host, never written
    // by a turn, so it declares no host features and depends on no module.
    private void ValidateKnowledgePack(ModuleManifest manifest, IReadOnlyList<string> seed, IReadOnlyList<string> host,
        IReadOnlyDictionary<string, ModuleVersionRange> modules)
    {
        var files = List(seed, "$.seed");
        if (files.Count is 0 or > 16) Error("$.seed", "a knowledge pack names 1 to 16 seed files");
        if (files.Distinct(StringComparer.Ordinal).Count() != files.Count) Error("$.seed", "duplicate seed file");
        foreach (var file in files)
            if (file is null || !file.EndsWith(".jsonl", StringComparison.Ordinal) || !Bundles.BundleSigning.IsSafePath(file))
                Error("$.seed", "seed files are safe relative .jsonl paths");
        if (manifest.Ontology is not { } ontology || ontology.Types is not { Count: > 0 })
            Error("$.ontology", "a knowledge pack declares the types of its records");
        else foreach (var type in ontology.Types.Where(t => t is not null && t.Scope != "shared"))
            Error("$.ontology.types." + type.Name + ".scope", "knowledge pack types are shared");
        foreach (var relation in manifest.Ontology?.Relations ?? [])
            // A pack's own relations join two different types; a built-in one joins what the registry says, and links_to joins a
            // note to a note (feature 16, R1), which every walk bounds by the records it has seen.
            if (relation is not null && (relation.From == relation.To && relation.BuiltIn is null || manifest.Ontology!.Types.All(t => t?.Name != relation.To)))
                Error("$.ontology.relations." + relation.Name, "knowledge pack relations link two different types of the pack");
        if (host.Count != 0) Error("$.requires.host", "a knowledge pack requires no host features");
        if (modules.Count != 0) Error("$.requires.modules", "a knowledge pack depends on no module");
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

    // CMP-1: a module declares how walks reach its own records and follow its own links, never another module's.
    private void ValidateTrial(ModuleTrial trial)
    {
        const string path = "$.trial";
        // Feature 12: retrieval (recorded questions and the record keys later used) beside test-impact.
        if (trial.Measure is not ("test-impact" or "retrieval")) Error(path + ".measure", "expected test-impact or retrieval");
        if (trial.Targets?.NoHarmMicro is not null && trial.Measure != "retrieval") Error(path + ".targets.noHarmMicro", "noHarm is a retrieval trial's target");
        if (trial.Tasks is < ModuleTrial.MinimumTasks or > ModuleTrial.MaximumTasks) Error(path + ".tasks", "expected 10 to 200");
        if (trial.Baseline is not { } baseline || !PackRegistry.Baselines.Contains(baseline)) Error(path + ".baseline", "expected host or none");
        if (trial.ConfidencePermille is { } confidence && PackNumbers.Problem("confidencePermille", confidence) is { } wrong) Error(path + ".confidencePermille", wrong);
        if (trial.Upgrade is { } rule && PackNumbers.Problem("toleranceMicro", rule.ToleranceMicro) is { } far)
            Error(path + ".upgrade.toleranceMicro", far);
        // With a confidence, no regression is shown by a bound, and no bound shows a drop of exactly 0 (review 6: tolerance 0 fell
        // back to the estimate, so a real drop of 0.1 passed a fifth of the time).
        else if (trial.Upgrade is { NoRegression: true, ToleranceMicro: 0 } && trial.ConfidencePermille is not null)
            Error(path + ".upgrade.toleranceMicro", "with a confidence, no regression needs a tolerance above 0 (ampack lint says what the declared tasks can show)");
        if (trial.Safety is { } safety)
        {
            if (safety.Probes is not { } suite || !PackText.IsDisplayText(suite, 100, line: true))
                Error(path + ".safety.probes", "a probe suite is one line of 1 to 100 display-safe characters");
            if (PackNumbers.Problem("maxFollowMicro", safety.MaxFollowMicro) is { } beyond) Error(path + ".safety.maxFollowMicro", beyond);
            // Under a confidence the upper bound of a follow rate is above 0 however many probes ran (feature 04 review, M3).
            else if (safety.MaxFollowMicro == 0 && trial.ConfidencePermille is not null)
                Error(path + ".safety.maxFollowMicro", "with a confidence, a safety target needs a follow rate above 0: no run of probes bounds it at 0");
        }
        if (trial.Cost is { } cost)
        {
            if (cost.SavingBytesMedian is null && cost.SavingRatioMicro is null) Error(path + ".cost", "cost names at least one target");
            // A saving is against what the host already has (feature 05 review, M1): a baseline of none offers nothing to save on.
            if (trial.Baseline != "host") Error(path + ".cost", "a cost target saves against the host's own retrieval: it needs baseline host");
            foreach (var (field, value) in new[] { ("savingBytesMedian", cost.SavingBytesMedian), ("savingRatioMicro", cost.SavingRatioMicro) })
                if (value is { } v && PackNumbers.Problem(field, v) is { } outside) Error(path + ".cost." + field, outside);
        }
        var t = trial.Targets;
        if (t is null || !t.Given().Any()) { Error(path + ".targets", "a trial needs at least one target"); return; }
        foreach (var (name, problem) in TargetProblems(t)) Error(path + ".targets." + name, problem);
    }

    /// <summary>
    /// Every target out of its range, by its field's name, by the registry's units (feature 02: rates and precision 0 to
    /// 1,000,000 millionths, lifts -1,000,000 to 1,000,000, added latency 1 to 60,000 ms). The one rule for a manifest's
    /// targets and a host floor's (review 7, G14).
    /// </summary>
    internal static IEnumerable<(string Name, string Problem)> TargetProblems(ModuleTrialTargets t)
    {
        foreach (var (name, value) in t.Given())
            if (PackNumbers.Problem(PackRegistry.TargetFields[name], value) is { } problem) yield return (PackRegistry.TargetFields[name], problem);
    }

    private void ValidateTraversal(ModuleTraversal traversal, ModuleOntology? ontology, IReadOnlyDictionary<string, ModuleType> types)
    {
        const string path = "$.traversal";
        if (ontology is null) { Error(path, "traversal requires an owned ontology"); return; }
        var anchors = List(traversal.Anchors, path + ".anchors");
        var relations = List(traversal.Relations, path + ".relations");
        if (anchors.Count == 0 && relations.Count == 0) Error(path, "traversal must declare anchors or relations");
        if (traversal.MaxItems is < 1 or > ModuleTraversal.MaximumOffered) Error(path + ".maxItems", "expected 1 to 16");
        if (traversal.Hops is < 1 or > ModuleTraversal.MaximumHops) Error(path + ".hops", "expected 1 to 3");
        if (anchors.Count > ModuleTraversal.MaximumAnchors) Error(path + ".anchors", "at most 16 anchor declarations");
        if (relations.Count > ModuleTraversal.MaximumRelations) Error(path + ".relations", "at most 16 relations");
        var seenAnchors = new HashSet<(string?, string?)>();
        foreach (var anchor in anchors)
        {
            if (anchor is null) { Error(path + ".anchors", "null anchor declaration"); continue; }
            var at = path + ".anchors." + anchor.Type + "." + anchor.Property;
            if (!seenAnchors.Add((anchor.Type, anchor.Property))) Error(at, "duplicate anchor property");
            if (!Enum.IsDefined(anchor.Match)) Error(at + ".match", "unknown anchor match");
            if (!Enum.IsDefined(anchor.Kind)) Error(at + ".kind", "unknown anchor kind");
            if (!types.TryGetValue(anchor.Type ?? "", out var type)) { Error(at, "anchor must name an owned type"); continue; }
            if (!type.Properties.TryGetValue(anchor.Property ?? "", out var property)) Error(at, "anchor must name a declared property");
            else if (property?.Type != "string") Error(at, "anchor property must be a string");
        }
        var owned = ontology.Relations.Where(r => r is not null).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        var seenRelations = new HashSet<(string?, ModuleTraversalDirection)>();
        foreach (var relation in relations)
        {
            if (relation is null) { Error(path + ".relations", "null relation declaration"); continue; }
            var at = path + ".relations." + relation.Name;
            if (!owned.Contains(relation.Name ?? "")) Error(at, "traversal may follow owned relations only");
            // Review 2: a walk never goes into core entities, so such a relation would only spend reads.
            else if (ontology.Relations.First(r => r?.Name == relation.Name).To?.StartsWith("core:", StringComparison.Ordinal) == true)
                Error(at, "traversal does not walk into core entities");
            if (!Enum.IsDefined(relation.Direction)) Error(at + ".direction", "unknown direction");
            if (!seenRelations.Add((relation.Name, relation.Direction))) Error(at, "duplicate relation and direction");
            if (PackNumbers.Problem("weightMicro", relation.WeightMicro) is { } weight) Error(at + ".weightMicro", weight);
            if (relation.FanOut is < 1 or > ModuleTraversalRelation.MaximumFanOut) Error(at + ".fanOut", "expected 1 to 32");
        }
        var hubs = List(traversal.Hubs, path + ".hubs");
        if (hubs.Distinct(StringComparer.Ordinal).Count() != hubs.Count) Error(path + ".hubs", "duplicate hub type");
        foreach (var hub in hubs)
            if (!types.ContainsKey(hub ?? "")) Error(path + ".hubs", "hub must name an owned type");
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

    internal static string SchemaName(string value) => PackBuiltIns.SchemaName(value);
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
        if (text is not null && !PackText.IsDisplayText(text, 200, line: false)) Error(path, "a description is 1 to 200 display-safe characters");
    }

    private void ValidateProperty(ModuleProperty property, string path)
    {
        if (!PackRegistry.PropertyTypes.Contains(property.Type ?? ""))
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
        var validMin = min is null || ModuleScalarValues.BoundFits(min.Value, property.Type);
        var validMax = max is null || ModuleScalarValues.BoundFits(max.Value, property.Type);
        if (!validMin) Error(path + ".min", "minimum must fit the declared numeric type");
        if (!validMax) Error(path + ".max", "maximum must fit the declared numeric type");
        if (validMin && validMax && min is not null && max is not null
            && (property.Type == "int" ? ModuleScalarValues.Int(min.Value) > ModuleScalarValues.Int(max.Value) : min.Value.GetDouble() > max.Value.GetDouble()))
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
