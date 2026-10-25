using System.Diagnostics.CodeAnalysis;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Ingestion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentMemory.Extensibility.Modules;

/// <summary>Validates composition against the effective service bindings of each store.</summary>
[Experimental("AMEXT001")]
public static class ModuleHostProfileResolver
{
    /// <summary>Checks host capabilities without loading module implementations or issuing database queries.</summary>
    /// <remarks>Structural validation runs first. Services, options and adapter probes are trusted host
    /// inputs. A successful result is still metadata: it does not prove connectivity, migrations, trust,
    /// successful service construction or activation. Repeat the check if host bindings change.</remarks>
    public static ModuleProfileResolution Resolve(ModuleCompositionProfile profile,
        IReadOnlyList<ModuleManifest> registrations, ModuleVersion sdkVersion,
        IReadOnlyDictionary<string, ModuleHostStore> stores)
    {
        ArgumentNullException.ThrowIfNull(stores);
        // Normalize identity comparisons; a caller's case-insensitive dictionary must not grant a binding.
        var bindings = stores.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        foreach (var binding in bindings.Values)
        {
            ArgumentNullException.ThrowIfNull(binding);
            ArgumentNullException.ThrowIfNull(binding.Services);
        }
        // All known features here permit structural validation only. Every actual requirement is
        // checked below against its own bound store before this result can escape.
        var metadata = ModuleProfileResolver.Resolve(profile, registrations, sdkVersion,
            ModuleHostFeatures.Known.ToHashSet(StringComparer.Ordinal),
            bindings.ToDictionary(p => p.Key, p => p.Value.Provider, StringComparer.Ordinal));
        if (!metadata.Succeeded) return metadata;

        var errors = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var group in metadata.Modules.GroupBy(m => m.Binding.Store, StringComparer.Ordinal))
        {
            var store = bindings[group.Key];
            var requirements = group.SelectMany(m => m.Manifest.Host.Concat(
                m.Manifest.Ontology is null && m.Manifest.Views.Count == 0 ? [] : new[] { "ModuleSchema" }))
                .Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal);
            foreach (var feature in requirements)
                foreach (var problem in Problems(store, feature))
                    errors.Add($"Store '{group.Key}' needs {feature}: {problem}");
        }
        return errors.Count == 0 ? metadata
            : new ModuleProfileResolution([], errors.ToList().AsReadOnly(), null, []);
    }

    private static IReadOnlyList<string> Problems(ModuleHostStore store, string feature)
    {
        var services = store.Services;
        var unkeyed = store.ServiceKey is null ? services.GetService<IServiceProviderIsService>() : null;
        var keyed = store.ServiceKey is not null ? services.GetService<IServiceProviderIsKeyedService>() : null;
        if (unkeyed is null && keyed is null)
            return ["The container cannot inspect registrations at the selected store key."];
        bool Registered(Type type) => store.ServiceKey is null
            ? unkeyed!.IsService(type) : keyed!.IsKeyedService(type, store.ServiceKey);

        switch (feature)
        {
            case "History":
                return Registered(typeof(IMemoryHistoryService)) ? []
                    : ["IMemoryHistoryService is not registered at the selected store key."];
            case "Reminders":
                if (!Registered(typeof(IOptions<MemoryOptions>)))
                    return ["Effective IOptions<MemoryOptions> is not registered at the selected store key."];
                try
                {
                    var options = store.ServiceKey is null ? services.GetRequiredService<IOptions<MemoryOptions>>()
                        : services.GetRequiredKeyedService<IOptions<MemoryOptions>>(store.ServiceKey);
                    var recall = options.Value.Recall;
                    return recall.ProspectiveFiring && recall.ValidTime == ValidTimeMode.Current ? []
                        : ["Effective Recall options require ProspectiveFiring=true and ValidTime=Current."];
                }
                catch (OptionsValidationException)
                {
                    return ["Effective MemoryOptions failed host validation."];
                }
            case ModuleHostFeatures.Inference:
                return store.ChatClient is not null ? []
                    : ["No chat model is granted to modules (ModuleSetBuilder.GrantChatClient)."];
            case "ModuleSchema":
                var problems = new List<string>();
                if (!Registered(typeof(IModuleStore)))
                    problems.Add("IModuleStore is not registered at the selected store key.");
                if (store.SchemaProbe is null)
                    problems.Add("No storage adapter capability probe is supplied.");
                else if (!string.Equals(store.SchemaProbe.Provider, store.Provider, StringComparison.Ordinal))
                    problems.Add("Storage adapter probe does not match the bound provider.");
                else
                    problems.AddRange(store.SchemaProbe.GetProblems(services, store.ServiceKey));
                return problems.AsReadOnly();
            default:
                throw new InvalidOperationException("Structural validation admitted an unknown host requirement.");
        }
    }
}
