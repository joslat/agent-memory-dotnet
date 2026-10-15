using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;

namespace AgentMemory.Extensibility.Modules;

/// <summary>Reconciles a profile with declared registrations using the shared metadata resolver.</summary>
[Experimental("AMEXT001")]
public static class ModuleProfileResolver
{
    /// <summary>Returns a pure composition candidate; no options, services or source files are opened.</summary>
    /// <remarks>The host must derive features/store bindings from effective registrations. This result
    /// does not prove service availability, publisher trust, configuration validity or source identity.</remarks>
    public static ModuleProfileResolution Resolve(ModuleCompositionProfile profile,
        IReadOnlyList<ModuleManifest> registrations, ModuleVersion sdkVersion,
        IReadOnlySet<string> availableFeatures, IReadOnlyDictionary<string, string> storeProviders)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(storeProviders);
        var metadata = ModuleResolver.Resolve(registrations, sdkVersion, availableFeatures);
        var errors = new SortedSet<string>(metadata.Errors, StringComparer.Ordinal);
        // Caller comparers must not weaken ordinal identity matching.
        var stores = storeProviders.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var entries = profile.Modules.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        var registered = registrations.Where(m => m is not null && ModuleIdentity.IsValidModuleId(m.Id))
            .GroupBy(m => m.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        foreach (var id in registered.Keys)
            if (!entries.ContainsKey(id)) errors.Add($"Module '{id}' is registered but not composed.");
        foreach (var store in profile.Stores)
        {
            if (!stores.TryGetValue(store.Key, out var provider)) errors.Add($"Profile store '{store.Key}' is not registered.");
            else if (provider != store.Value.Provider)
                errors.Add($"Profile store '{store.Key}' needs provider '{store.Value.Provider}'; registered provider is '{provider}'.");
        }
        foreach (var entry in profile.Modules)
        {
            if (!profile.Stores.ContainsKey(entry.Store)) errors.Add($"Module '{entry.Id}' names undeclared store '{entry.Store}'.");
            if (!registered.TryGetValue(entry.Id, out var modules)) errors.Add($"Profile module '{entry.Id}' is not registered.");
            else if (modules.Length == 1 && modules[0].Version is { } version && !entry.Version.Contains(version))
                errors.Add($"Profile module '{entry.Id}' needs version {entry.Version}; registered version is {version}.");
        }
        foreach (var module in metadata.Modules)
        {
            if (!entries.TryGetValue(module.Id, out var source) || module.Ontology is null) continue;
            foreach (var relation in module.Ontology.Relations)
            {
                var target = relation.To.Split(':');
                if (target.Length == 2 && entries.TryGetValue(target[0], out var destination) && source.Store != destination.Store)
                    errors.Add($"Module '{module.Id}' relationship '{relation.Name}' is cross-store: '{source.Store}' to '{destination.Store}'.");
            }
        }
        if (errors.Count != 0) return new ModuleProfileResolution(System.Array.AsReadOnly(System.Array.Empty<ComposedModule>()), errors.ToList().AsReadOnly(), null, []);
        var composed = metadata.Modules.Select(manifest =>
        {
            var binding = entries[manifest.Id];
            return new ComposedModule
            {
                Manifest = manifest, Binding = binding, Provider = profile.Stores[binding.Store].Provider,
                ConfigurationSection = binding.Configuration ?? "AgentMemory:Modules:" + manifest.Id
            };
        }).ToList().AsReadOnly();
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "agentmemory.composition-metadata/v1alpha1", profile = profile.Sha256,
            metadata = metadata.ManifestFingerprint
        }))).ToLowerInvariant();
        return new ModuleProfileResolution(composed, errors.ToList().AsReadOnly(), fingerprint, profile.Stores.Keys.ToList().AsReadOnly());
    }
}

/// <summary>Frozen metadata associated with a profile binding; not a running module.</summary>
[Experimental("AMEXT001")]
public sealed record ComposedModule
{
    /// <summary>Deep-frozen validated module declaration.</summary>
    public required ModuleManifest Manifest { get; init; }
    /// <summary>Immutable parsed profile binding, including store and source declarations.</summary>
    public required ModuleProfileEntry Binding { get; init; }
    /// <summary>Matched store provider identity.</summary>
    public required string Provider { get; init; }
    /// <summary>Effective configuration section path; no option values are bound here.</summary>
    public required string ConfigurationSection { get; init; }
}

/// <summary>Pure host/CLI composition result. Failure exposes no partial module set.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleProfileResolution
{
    private readonly IReadOnlyList<string> _stores;
    internal ModuleProfileResolution(IReadOnlyList<ComposedModule> modules, IReadOnlyList<string> errors,
        string? fingerprint, IReadOnlyList<string> stores)
    { Modules = modules; Errors = errors; CompositionFingerprint = fingerprint; _stores = stores; }

    /// <summary>Projects a validated composition onto one declared store, preserving dependency order.</summary>
    /// <remarks>Whole-profile validation must succeed first. Ordinary dependencies in another store remain
    /// declared; they are not re-resolved as missing from this slice. This does not connect to a store or
    /// establish host capabilities. The fingerprint binds the complete composition and selected alias.</remarks>
    public ModuleResolution ForStore(string store)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(store);
        if (!Succeeded) throw new InvalidOperationException("Cannot select a store from an invalid module composition.");
        if (!_stores.Contains(store, StringComparer.Ordinal)) throw new ArgumentException("Unknown profile store: " + store, nameof(store));
        var modules = Modules.Where(m => m.Binding.Store == store).Select(m => m.Manifest).ToList().AsReadOnly();
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "agentmemory.composition-store/v1alpha1", composition = CompositionFingerprint, store
        }))).ToLowerInvariant();
        return new ModuleResolution(modules, System.Array.Empty<string>(), fingerprint);
    }
    /// <summary>Validated metadata bindings in dependency order, or empty on failure.</summary>
    public IReadOnlyList<ComposedModule> Modules { get; }
    /// <summary>All diagnosed problems in ordinal order.</summary>
    public IReadOnlyList<string> Errors { get; }
    /// <summary>Whether metadata/profile checks succeeded. Not proof of activation.</summary>
    public bool Succeeded => Errors.Count == 0;
    /// <summary>Metadata plus exact profile bytes. Null on failure.</summary>
    /// <remarks>Actual option values, migrations, trust and implementation identities are not verified
    /// or included here. Activation must incorporate those in the eventual active-set fingerprint.</remarks>
    public string? CompositionFingerprint { get; }
}
