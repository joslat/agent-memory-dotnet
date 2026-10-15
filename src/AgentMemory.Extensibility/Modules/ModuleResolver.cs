using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using AgentMemory.Shared;

namespace AgentMemory.Extensibility.Modules;

/// <summary>Checks a complete declaration set without DI, module execution, schema writes or model calls.</summary>
[Experimental("AMEXT001")]
public static class ModuleResolver
{
    /// <summary>Freezes declarations, validates local/set semantics and returns all diagnosed errors.</summary>
    /// <remarks>Features and target SDK are explicit planning inputs. An activating host must derive
    /// them from real effective registrations and options, then apply profile/trust checks. Inputs must
    /// not be concurrently modified during this call; subsequent caller mutation cannot alter a result.</remarks>
    public static ModuleResolution Resolve(IReadOnlyList<ModuleManifest> manifests, ModuleVersion sdkVersion,
        IReadOnlySet<string> availableFeatures)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        ArgumentNullException.ThrowIfNull(sdkVersion);
        ArgumentNullException.ThrowIfNull(availableFeatures);
        var errors = new SortedSet<string>(StringComparer.Ordinal);
        var features = availableFeatures.ToHashSet(StringComparer.Ordinal);
        foreach (var feature in features)
            if (feature is not ("History" or "Reminders" or "ModuleSchema")) errors.Add($"Unknown available host feature '{feature}'.");
        var frozen = new List<ModuleManifest>();
        var identityCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var manifest in manifests)
        {
            if (manifest is null) { errors.Add("Null module declaration."); continue; }
            if (ModuleIdentity.IsValidModuleId(manifest.Id))
                identityCounts[manifest.Id] = identityCounts.GetValueOrDefault(manifest.Id) + 1;
            try
            {
                var copy = ModuleManifestSnapshot.Freeze(manifest);
                var local = copy.Validate();
                foreach (var problem in local) errors.Add($"Module '{copy.Id}': {problem}");
                if (local.Count == 0) frozen.Add(copy);
            }
            catch (ObjectDisposedException) { errors.Add($"Module '{manifest.Id}': declaration JSON document has been disposed."); }
        }
        var byId = new Dictionary<string, ModuleManifest>(StringComparer.Ordinal);
        foreach (var identity in identityCounts.Where(pair => pair.Value > 1))
            errors.Add($"Found duplicate module identity '{identity.Key}'.");
        foreach (var module in frozen)
            if (identityCounts[module.Id] == 1) byId.Add(module.Id, module);
        var claims = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var module in byId.Values.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            if (!module.Sdk.Contains(sdkVersion)) errors.Add($"Module '{module.Id}' needs SDK {module.Sdk}; target is {sdkVersion}.");
            foreach (var dependency in module.Modules)
            {
                if (!byId.TryGetValue(dependency.Key, out var target))
                    errors.Add($"Module '{module.Id}' needs missing, invalid or ambiguous dependency '{dependency.Key}'.");
                else if (!dependency.Value.Contains(target.Version))
                    errors.Add($"Module '{module.Id}' needs '{target.Id}' {dependency.Value}; supplied version is {target.Version}.");
            }
            foreach (var feature in module.Host.Concat(module.Ontology is null && module.Views.Count == 0 ? [] : new[] { "ModuleSchema" }))
                if (!features.Contains(feature)) errors.Add($"Module '{module.Id}' needs host feature {feature}.");
            if (module.Ontology is not { } ontology) continue;
            Claim("prefix", ontology.LabelPrefix, module.Id, claims, errors);
            foreach (var type in ontology.Types)
            {
                Claim("label", type.Name, module.Id, claims, errors);
                Claim("schema name", ModuleManifestValidator.SchemaName(type.Name), module.Id, claims, errors);
                foreach (var shape in ModuleSchemaObjects.ForType(type))
                    Claim("schema object", shape.Name, module.Id, claims, errors);
            }
            foreach (var relation in ontology.Relations)
            {
                Claim("relationship", relation.Name, module.Id, claims, errors);
                var target = relation.To.Split(':');
                if (target.Length != 2 || target[0] == "core") continue;
                if (!byId.TryGetValue(target[0], out var dependency)
                    || dependency.Ontology?.Types.Any(t => t.Name == target[1]) != true)
                    errors.Add($"Module '{module.Id}' relationship '{relation.Name}' names absent dependency target '{relation.To}'.");
            }
        }
        IReadOnlyList<ModuleManifest> ordered = [];
        try
        {
            // Filter missing edges only for cycle diagnosis: their errors were already collected above.
            // This also discovers an independent cycle when some other dependency is unavailable.
            ordered = DependencyOrder.Resolve(byId, byId.Keys.OrderBy(id => id, StringComparer.Ordinal),
                m => m.Modules.Keys.Where(byId.ContainsKey),
                (parent, id) => new InvalidOperationException($"Unexpected missing dependency '{id}' in frozen set."),
                remaining => new DependencyCycleException(remaining));
        }
        catch (DependencyCycleException e)
        { errors.Add("Dependency cycle or descendants blocked by it: " + string.Join(", ", e.Ids) + "."); }
        if (errors.Count != 0) return new ModuleResolution(Array.AsReadOnly(Array.Empty<ModuleManifest>()), errors.ToList().AsReadOnly(), null);
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "agentmemory.module-metadata/v1alpha1", sdk = sdkVersion.Value,
            manifests = frozen.OrderBy(m => m.Id, StringComparer.Ordinal).ToArray()
        });
        var hash = Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
        return new ModuleResolution(ordered, errors.ToList().AsReadOnly(), hash);
    }

    private static void Claim(string kind, string name, string owner, Dictionary<string, string> claims, ISet<string> errors)
    {
        var key = kind + ":" + name;
        if (claims.TryGetValue(key, out var prior) && prior != owner)
            errors.Add($"Modules '{prior}' and '{owner}' claim the same {kind} '{name}'.");
        else claims[key] = owner;
    }

    private sealed class DependencyCycleException(IReadOnlyList<string> ids) : Exception
    {
        internal IReadOnlyList<string> Ids { get; } = ids;
    }
}
