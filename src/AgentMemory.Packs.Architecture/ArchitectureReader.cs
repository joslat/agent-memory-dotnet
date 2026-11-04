// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Packs;
using YamlDotNet.RepresentationModel;

namespace AgentMemory.Packs.Architecture;

/// <summary>
/// Reads a repository's architecture files into the architecture pack's records (CMP-5), deterministically: Backstage
/// catalogs (systems, components, resources, APIs, groups and users, and the links between them), OpenAPI and AsyncAPI
/// documents (an API and its operations, merged into the catalog's API when the catalog points at the document), CycloneDX
/// and SPDX SBOMs (the packages a component ships), and Structurizr workspaces (C4 systems, containers and the relationships
/// between them). Names are matched as Backstage writes them, lower case with dashes, so one component described in two files
/// is one record. Anything a record links to and no file describes is still a record, named only, so no link is left out.
/// </summary>
[Experimental("AMEXT001")]
public static class ArchitectureReader
{
    /// <summary>Largest file read: architecture documents are small; a larger one is left out and reported.</summary>
    public const int MaximumFileBytes = 8 << 20;
    /// <summary>Most JSON values one YAML file may expand to: aliases can make a small file enormous.</summary>
    public const int MaximumNodes = 200_000;
    /// <summary>Most packages one SBOM contributes.</summary>
    public const int MaximumPackages = 5_000;

    /// <summary>The files worth reading, by name: catalogs, API documents, SBOMs and Structurizr workspaces.</summary>
    public static IReadOnlyList<string> ArchitectureFiles(IEnumerable<string> repositoryFiles)
    {
        ArgumentNullException.ThrowIfNull(repositoryFiles);
        return [.. repositoryFiles.Select(Normalize).Where(IsCandidate).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    private static bool IsCandidate(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        var yaml = name.EndsWith(".yaml", StringComparison.Ordinal) || name.EndsWith(".yml", StringComparison.Ordinal);
        var json = name.EndsWith(".json", StringComparison.Ordinal);
        if (!yaml && !json) return false;
        return name.StartsWith("catalog-info.", StringComparison.Ordinal) || name.EndsWith(".catalog-info.yaml", StringComparison.Ordinal)
            || name.Contains("openapi", StringComparison.Ordinal) || name.Contains("swagger", StringComparison.Ordinal) || name.Contains("asyncapi", StringComparison.Ordinal)
            || json && (name is "bom.json" or "sbom.json" or "workspace.json" || name.EndsWith(".cdx.json", StringComparison.Ordinal)
                || name.EndsWith(".spdx.json", StringComparison.Ordinal) || name.StartsWith("sbom", StringComparison.Ordinal) || name.EndsWith("-bom.json", StringComparison.Ordinal));
    }

    /// <summary>Reads the files into one snapshot; <paramref name="skipped"/> names each file left out and why.</summary>
    public static ModuleSnapshot Read(string root, IReadOnlyList<string> files, string source, out IReadOnlyList<string> skipped)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(files);
        var model = new Model();
        var left = new List<string>();
        var documents = new List<(string File, JsonNode Node)>();
        foreach (var file in files.Select(Normalize).Distinct(StringComparer.Ordinal))
            foreach (var document in Load(root, file, left)) documents.Add((file, document));

        // Catalogs first: they name the components and the API documents; then the documents, the SBOMs and the workspaces.
        foreach (var (file, node) in documents.Where(d => IsCatalog(d.Node))) model.Catalog(file, node);
        var specs = documents.Where(d => d.Node is JsonObject o && (o["openapi"] is not null || o["swagger"] is not null || o["asyncapi"] is not null)).ToList();
        foreach (var definition in model.Definitions.Keys.Where(f => specs.All(s => s.File != f)))
            foreach (var document in Load(root, definition, left)) specs.Add((definition, document));
        foreach (var (file, node) in specs) model.Spec(file, (JsonObject)node);
        foreach (var (file, node) in documents.Where(d => d.Node is JsonObject o && (o["bomFormat"]?.GetValue<string>() == "CycloneDX" || o["spdxVersion"] is not null)))
            model.Sbom(file, (JsonObject)node);
        foreach (var (file, node) in documents.Where(d => d.Node is JsonObject o && o["model"]?["softwareSystems"] is JsonArray)) model.Workspace(file, (JsonObject)node);

        skipped = left;
        return new ModuleSnapshot { Module = ArchitecturePack.Manifest, Source = source, Records = model.Records() };
    }

    private static bool IsCatalog(JsonNode node) =>
        node is JsonObject o && o["apiVersion"] is JsonValue v && v.TryGetValue<string>(out var api) && api.StartsWith("backstage.io/", StringComparison.Ordinal) && o["kind"] is not null;

    // One file as JSON values: a JSON file is one; a YAML file one per document.
    private static IEnumerable<JsonNode> Load(string root, string file, List<string> skipped)
    {
        var full = Path.Combine(root, file);
        if (!File.Exists(full)) { skipped.Add($"{file}: not found"); return []; }
        if (new FileInfo(full).Length > MaximumFileBytes) { skipped.Add($"{file}: larger than {MaximumFileBytes >> 20} MiB"); return []; }
        var text = File.ReadAllText(full);
        try
        {
            if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return JsonNode.Parse(text) is { } node ? [node] : [];
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            var budget = MaximumNodes;
            return [.. stream.Documents.Select(d => Convert(d.RootNode, ref budget)).OfType<JsonNode>()];
        }
        catch (Exception e) when (e is JsonException or YamlDotNet.Core.YamlException or InvalidDataException)
        {
            skipped.Add($"{file}: {e.Message}");
            return [];
        }
    }

    // A YAML node as JSON, scalars as strings, within a budget of values: an alias repeats a whole subtree.
    private static JsonNode? Convert(YamlNode node, ref int budget)
    {
        if (--budget < 0) throw new InvalidDataException($"it expands to more than {MaximumNodes} values");
        switch (node)
        {
            case YamlMappingNode map:
                var o = new JsonObject();
                foreach (var (key, value) in map.Children)
                    if (key is YamlScalarNode { Value: { } name }) o[name] = Convert(value, ref budget);
                return o;
            case YamlSequenceNode sequence:
                var a = new JsonArray();
                foreach (var item in sequence.Children) a.Add(Convert(item, ref budget));
                return a;
            case YamlScalarNode scalar:
                return scalar.Value is null ? null : JsonValue.Create(scalar.Value);
            default:
                return null;
        }
    }

    // Repository-relative with forward slashes, "." and ".." resolved: "./api/../api/orders.yaml" is "api/orders.yaml".
    internal static string Normalize(string path)
    {
        var segments = new List<string>();
        foreach (var segment in path.Trim().Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == ".." && segments.Count > 0 && segments[^1] != "..") segments.RemoveAt(segments.Count - 1);
            else segments.Add(segment);
        }
        return string.Join('/', segments);
    }

    // Backstage writes names lower case with dashes; a C4 name "Orders Service" is the same component as "orders-service".
    internal static string Slug(string name) =>
        string.Join('-', name.Trim().ToLowerInvariant().Split([' ', '_', '/', '\\', '.'], StringSplitOptions.RemoveEmptyEntries));

    // A Backstage entity reference: [kind:][namespace/]name, kind defaulting by where it appears.
    internal static (string Kind, string Key, string Name) Ref(string reference, string defaultKind)
    {
        var kind = defaultKind;
        var rest = reference.Trim();
        var colon = rest.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0) { kind = rest[..colon].ToLowerInvariant(); rest = rest[(colon + 1)..]; }
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var ns = slash > 0 ? rest[..slash].ToLowerInvariant() : "default";
        var name = slash > 0 ? rest[(slash + 1)..] : rest;
        return (kind, $"{kind}:{ns}/{name.ToLowerInvariant()}", name);
    }

    /// <summary>The records as they are found, then linked: each key once, the first source of each property kept.</summary>
    private sealed class Model
    {
        private readonly Dictionary<string, (string Type, Dictionary<string, object?> Properties, string Source)> _records = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<(string Relation, string Target)>> _links = new(StringComparer.Ordinal);
        /// <summary>API documents a catalog points at, by repository path, with the catalog API they define.</summary>
        internal Dictionary<string, string> Definitions { get; } = new(StringComparer.Ordinal);

        private void Put(string key, string type, string source, Dictionary<string, object?> properties)
        {
            if (!_records.TryGetValue(key, out var known)) { _records[key] = (type, properties, source); return; }
            foreach (var (name, value) in properties)
                if (value is not null && (!known.Properties.TryGetValue(name, out var had) || had is null || had is string { Length: 0 })) known.Properties[name] = value;
        }

        private void Link(string from, string relation, string to)
        {
            if (!_links.TryGetValue(from, out var links)) _links[from] = links = [];
            if (!links.Contains((relation, to))) links.Add((relation, to));
        }

        // A record something links to: named, so the link has a target, even when no file describes it.
        private string Named(string key, string type, string name, string source, string? kind = null)
        {
            var properties = new Dictionary<string, object?> { ["name"] = name };
            if (kind is not null) properties["kind"] = kind;
            Put(key, type, source, properties);
            return key;
        }

        private string Owner(string reference, string source)
        {
            var (kind, key, name) = Ref(reference, "group");
            return Named(key, ArchitecturePack.OwnerType, name, source, kind is "group" or "user" ? kind : "unknown");
        }

        private string SystemRef(string reference, string source) => Named(Ref(reference, "system").Key, ArchitecturePack.SystemType, Ref(reference, "system").Name, source);

        private string Component(string reference, string source)
        {
            var (kind, key, name) = Ref(reference, "component");
            return Named(key, ArchitecturePack.ComponentType, name, source, kind == "resource" ? "resource" : "component");
        }

        private string Api(string reference, string source) => Named(Ref(reference, "api").Key, ArchitecturePack.ApiType, Ref(reference, "api").Name, source, "other");

        // One Backstage entity.
        internal void Catalog(string file, JsonNode node)
        {
            var kind = Text(node["kind"])?.ToLowerInvariant();
            var metadata = node["metadata"];
            var spec = node["spec"];
            if (Text(metadata?["name"]) is not { Length: > 0 } name) return;
            var ns = (Text(metadata?["namespace"]) ?? "default").ToLowerInvariant();
            var key = $"{kind}:{ns}/{name.ToLowerInvariant()}";
            var source = $"{file}#{kind}:{name}";
            var description = Text(metadata?["description"]);
            var owner = Text(spec?["owner"]);
            switch (kind)
            {
                case "system":
                    Put(key, ArchitecturePack.SystemType, source, new() { ["name"] = name, ["description"] = description, ["domain"] = Text(spec?["domain"]), ["owner"] = owner });
                    if (owner is not null) Link(key, ArchitecturePack.SystemOwnedBy, Owner(owner, source));
                    break;
                case "component" or "resource":
                    var folder = Path.GetDirectoryName(file)?.Replace('\\', '/');
                    var located = Text(metadata?["annotations"]?["backstage.io/source-location"]);
                    Put(key, ArchitecturePack.ComponentType, source, new()
                    {
                        ["name"] = name, ["kind"] = Text(spec?["type"]) ?? kind, ["description"] = description, ["lifecycle"] = Text(spec?["lifecycle"]),
                        ["owner"] = owner, ["path"] = SourceGlobs(folder, located),
                    });
                    if (owner is not null) Link(key, ArchitecturePack.ComponentOwnedBy, Owner(owner, source));
                    if (Text(spec?["system"]) is { } system) Link(key, ArchitecturePack.PartOf, SystemRef(system, source));
                    foreach (var api in Texts(spec?["providesApis"])) Link(key, ArchitecturePack.Provides, Api(api, source));
                    foreach (var api in Texts(spec?["consumesApis"])) Link(key, ArchitecturePack.Consumes, Api(api, source));
                    foreach (var other in Texts(spec?["dependsOn"])) Link(key, ArchitecturePack.DependsOn, Component(other, source));
                    break;
                case "api":
                    var definition = spec?["definition"] is JsonObject d && Text(d["$text"]) is { } target && !target.Contains("://", StringComparison.Ordinal)
                        ? Normalize(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(file) ?? "", target)) : null;
                    Put(key, ArchitecturePack.ApiType, source, new()
                    {
                        ["name"] = name, ["kind"] = Text(spec?["type"]) ?? "other", ["description"] = description, ["owner"] = owner, ["file"] = definition,
                    });
                    if (definition is not null) Definitions[definition] = key;
                    if (owner is not null) Link(key, ArchitecturePack.ApiOwnedBy, Owner(owner, source));
                    if (Text(spec?["system"]) is { } apiSystem) Link(key, ArchitecturePack.ApiOf, SystemRef(apiSystem, source));
                    break;
                case "group" or "user":
                    Put(key, ArchitecturePack.OwnerType, source, new()
                    {
                        ["name"] = name, ["kind"] = kind, ["description"] = description ?? Text(spec?["profile"]?["displayName"]),
                    });
                    break;
            }
        }

        // An OpenAPI (or Swagger) or AsyncAPI document: the API, and each operation.
        internal void Spec(string file, JsonObject document)
        {
            var async = document["asyncapi"] is not null;
            var kind = async ? "asyncapi" : "openapi";
            var info = document["info"];
            var title = Text(info?["title"]);
            var key = Definitions.GetValueOrDefault(file) ?? $"{kind}:{file}";
            var source = file;
            var operations = new List<(string Method, string Path, string? Id, string? Summary)>();
            if (!async && document["paths"] is JsonObject paths)
                foreach (var (path, item) in paths)
                    if (item is JsonObject methods)
                        foreach (var (method, operation) in methods)
                            if (method is "get" or "put" or "post" or "delete" or "options" or "head" or "patch" or "trace")
                                operations.Add((method.ToUpperInvariant(), path, Text(operation?["operationId"]), Text(operation?["summary"]) ?? Text(operation?["description"])));
            if (async && document["channels"] is JsonObject channels)
                foreach (var (channel, item) in channels)
                {
                    // AsyncAPI 2: publish and subscribe under each channel; AsyncAPI 3 lists operations apart, naming the channel.
                    foreach (var verb in new[] { "publish", "subscribe" })
                        if (item?[verb] is JsonObject operation) operations.Add((verb, Text(item["address"]) ?? channel, Text(operation["operationId"]), Text(operation["summary"])));
                }
            if (async && document["operations"] is JsonObject asyncOperations)
                foreach (var (id, operation) in asyncOperations)
                {
                    var channel = Text(operation?["channel"]?["$ref"])?.Split('/').LastOrDefault() ?? "";
                    var address = Text(document["channels"]?[channel]?["address"]) ?? channel;
                    operations.Add((Text(operation?["action"]) ?? "send", address, id, Text(operation?["summary"])));
                }
            Put(key, ArchitecturePack.ApiType, source, new()
            {
                ["name"] = title ?? System.IO.Path.GetFileNameWithoutExtension(file), ["kind"] = kind, ["title"] = title, ["version"] = Text(info?["version"]),
                ["description"] = Text(info?["description"]), ["file"] = file, ["operations"] = operations.Count,
            });
            // A catalog API named only by reference takes its kind from the document it points at.
            if (_records[key].Properties.TryGetValue("kind", out var known) && known is "other") _records[key].Properties["kind"] = kind;
            foreach (var (method, path, id, summary) in operations.DistinctBy(o => (o.Method, o.Path)))
            {
                var operationKey = $"{key}#{method} {path}";
                Put(operationKey, ArchitecturePack.OperationType, $"{file}#{method} {path}", new()
                {
                    ["name"] = $"{method} {path}", ["method"] = method, ["path"] = path, ["operationId"] = id, ["summary"] = summary, ["file"] = file,
                });
                Link(operationKey, ArchitecturePack.OperationOf, key);
            }
        }

        // A CycloneDX or SPDX SBOM: its packages, shipped by the component it describes when the catalog knows it.
        internal void Sbom(string file, JsonObject document)
        {
            var packages = new List<(string Key, Dictionary<string, object?> Properties)>();
            string? subject;
            if (document["bomFormat"] is not null)
            {
                subject = Text(document["metadata"]?["component"]?["name"]);
                foreach (var component in (document["components"] as JsonArray ?? []).OfType<JsonObject>().Take(MaximumPackages))
                {
                    if (Text(component["name"]) is not { } name) continue;
                    var version = Text(component["version"]);
                    var purl = Text(component["purl"]);
                    var licenses = string.Join(", ", (component["licenses"] as JsonArray ?? []).OfType<JsonObject>()
                        .Select(l => Text(l["license"]?["id"]) ?? Text(l["license"]?["name"]) ?? Text(l["expression"])).OfType<string>().Distinct());
                    packages.Add((purl ?? $"pkg:{name}@{version}", new()
                    {
                        ["name"] = name, ["version"] = version, ["purl"] = purl, ["licenses"] = licenses.Length == 0 ? null : licenses, ["kind"] = Text(component["type"]),
                    }));
                }
            }
            else
            {
                subject = Text(document["name"]);
                foreach (var package in (document["packages"] as JsonArray ?? []).OfType<JsonObject>().Take(MaximumPackages))
                {
                    if (Text(package["name"]) is not { } name) continue;
                    var version = Text(package["versionInfo"]);
                    var purl = (package["externalRefs"] as JsonArray ?? []).OfType<JsonObject>().Where(r => Text(r["referenceType"]) == "purl").Select(r => Text(r["referenceLocator"])).FirstOrDefault();
                    var license = Text(package["licenseConcluded"]) is { } concluded && concluded != "NOASSERTION" ? concluded : Text(package["licenseDeclared"]);
                    packages.Add((purl ?? $"pkg:{name}@{version}", new()
                    {
                        ["name"] = name, ["version"] = version, ["purl"] = purl, ["licenses"] = license is null or "NOASSERTION" ? null : license, ["kind"] = "package",
                    }));
                }
            }
            var owner = subject is null ? null : _records.Keys.FirstOrDefault(k => _records[k].Type == ArchitecturePack.ComponentType && Slug(_records[k].Properties["name"] as string ?? "") == Slug(subject));
            foreach (var (key, properties) in packages)
            {
                Put(key, ArchitecturePack.DependencyType, file, properties);
                if (owner is not null) Link(owner, ArchitecturePack.UsesPackage, key);
            }
        }

        // A Structurizr workspace: C4 software systems and their containers, and the relationships between containers.
        internal void Workspace(string file, JsonObject document)
        {
            var byId = new Dictionary<string, string>(StringComparer.Ordinal);
            var relationships = new List<(string From, string To)>();
            void Relations(JsonNode? element)
            {
                foreach (var r in (element?["relationships"] as JsonArray ?? []).OfType<JsonObject>())
                    if (Text(r["sourceId"]) is { } from && Text(r["destinationId"]) is { } to) relationships.Add((from, to));
            }
            foreach (var system in (document["model"]?["softwareSystems"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (Text(system["name"]) is not { } systemName) continue;
                var systemKey = $"system:default/{Slug(systemName)}";
                Put(systemKey, ArchitecturePack.SystemType, $"{file}#{systemName}", new() { ["name"] = Slug(systemName), ["description"] = Text(system["description"]) });
                if (Text(system["id"]) is { } systemId) byId[systemId] = systemKey;
                Relations(system);
                foreach (var container in (system["containers"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    if (Text(container["name"]) is not { } name) continue;
                    var key = $"component:default/{Slug(name)}";
                    Put(key, ArchitecturePack.ComponentType, $"{file}#{systemName}/{name}", new()
                    {
                        ["name"] = Slug(name), ["kind"] = "container", ["description"] = Text(container["description"]), ["technology"] = Text(container["technology"]),
                    });
                    Link(key, ArchitecturePack.PartOf, systemKey);
                    if (Text(container["id"]) is { } id) byId[id] = key;
                    Relations(container);
                }
            }
            foreach (var (from, to) in relationships)
                if (byId.TryGetValue(from, out var a) && byId.TryGetValue(to, out var b) && a != b
                    && _records[a].Type == ArchitecturePack.ComponentType && _records[b].Type == ArchitecturePack.ComponentType)
                    Link(a, ArchitecturePack.DependsOn, b);
        }

        internal IReadOnlyList<ModuleProposal> Records()
        {
            // A relation with at most one target keeps its first.
            var single = new HashSet<string>(StringComparer.Ordinal)
            {
                ArchitecturePack.PartOf, ArchitecturePack.OperationOf, ArchitecturePack.ApiOf, ArchitecturePack.SystemOwnedBy, ArchitecturePack.ComponentOwnedBy, ArchitecturePack.ApiOwnedBy,
            };
            return [.. _records.OrderBy(r => r.Value.Type, StringComparer.Ordinal).ThenBy(r => r.Key, StringComparer.Ordinal).Select(record =>
            {
                var links = (_links.GetValueOrDefault(record.Key) ?? []).GroupBy(l => l.Relation, StringComparer.Ordinal)
                    .SelectMany(g => single.Contains(g.Key) ? g.Take(1) : g)
                    .Select(l => new ProposedLink { Relation = l.Relation, Target = l.Target }).ToList();
                return new ModuleProposal
                {
                    Type = record.Value.Type, Key = Clip(record.Key, 600),
                    Properties = record.Value.Properties.Where(p => p.Value is not null and not (string { Length: 0 }))
                        .ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value is string s ? Clip(s, Limit(record.Value.Type, p.Key)) : p.Value), StringComparer.Ordinal)
                        .Concat([new KeyValuePair<string, JsonElement>("source", JsonSerializer.SerializeToElement(Clip(record.Value.Source, 512)))])
                        .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal).AsReadOnly(),
                    Links = links.AsReadOnly(),
                    Source = new ProposalSource { Excerpt = Clip(record.Value.Source, 512) },
                };
            })];
        }

        // The manifest's limit for a property, so a long description is shortened rather than refused.
        private static int Limit(string type, string property) =>
            ArchitecturePack.Manifest.Ontology!.Types.First(t => t.Name == type).Properties.TryGetValue(property, out var p) && p.MaxLength is { } max ? max : 512;
    }

    private static string? SourceGlobs(string? folder, string? located)
    {
        var globs = new List<string>();
        if (folder is { Length: > 0 }) globs.Add(folder.TrimEnd('/') + "/**");
        // url:https://host/org/repo/tree/main/src/orders/ or dir:./src: only a folder in this repository is a path here.
        if (located is { } l && l.StartsWith("dir:", StringComparison.Ordinal)) globs.Add(Normalize(System.IO.Path.Combine(folder ?? "", l[4..])).TrimEnd('/') + "/**");
        return globs.Count == 0 ? null : string.Join(' ', globs.Distinct(StringComparer.Ordinal));
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node is JsonValue n ? n.ToJsonString() : null;

    private static IEnumerable<string> Texts(JsonNode? node) => (node as JsonArray ?? []).Select(Text).OfType<string>();

    private static string Clip(string value, int length) => value.Length <= length ? value : value[..(length - 1)].TrimEnd() + "…";
}
