using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using AgentMemory.Extensibility.Capabilities;
using AgentMemory.Extensibility.Ingestion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentMemory.Extensibility.Modules;

/// <summary>
/// The modules a host composes statically, with their store and optional profile (Extensibility 0.12: static
/// composition for 2.0). Used inside <see cref="ServiceCollectionExtensions.AddAgentMemoryModules"/>.
/// </summary>
/// <remarks>
/// Each module's <see cref="IAgentMemoryModule.Configure"/> runs when it is added. Activation, on the first resolve of
/// <see cref="ModuleSet"/>, resolves the composition against the host, binds and validates options, and freezes one
/// module set for the process lifetime. There is no reload: a changed module or configuration needs a restart.
/// </remarks>
[Experimental("AMEXT001")]
public sealed class ModuleSetBuilder
{
    private readonly List<ModuleRegistration> _modules = [];
    private readonly Dictionary<string, int> _priorities = new(StringComparer.Ordinal);

    internal ModuleSetBuilder() { }

    internal IReadOnlyList<ModuleRegistration> Modules => _modules;
    internal IReadOnlyDictionary<string, int> Priorities => _priorities;
    internal ModuleCompositionProfile? Profile { get; private set; }
    internal IModuleStoreAdapter? Store { get; private set; }
    internal IConfiguration? Configuration { get; private set; }
    internal Func<IServiceProvider, Microsoft.Extensions.AI.IChatClient>? ChatClient { get; private set; }

    /// <summary>Adds a module from its assembly: the embedded manifest and its <see cref="IAgentMemoryModule"/>.</summary>
    public ModuleSetBuilder Add<TModule>() where TModule : IAgentMemoryModule, new() =>
        Add(ModuleManifest.Load(typeof(TModule).Assembly), new TModule().Configure);

    /// <summary>Adds a module from its manifest and a configuration delegate, the same path as a module entry.</summary>
    public ModuleSetBuilder Add(ModuleManifest manifest, Action<IModuleBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return Add(manifest, configure, Identity(configure.Method.Module.Assembly));
    }

    /// <summary>
    /// Adds a verified knowledge pack (Extensibility 0.13 §K5): its manifest, and a contributor for each of its search views,
    /// which searches with the turn's question. The pack runs no code; the fingerprint names its bundle digest and signer.
    /// Its records are imported separately, with <see cref="Packs.KnowledgePackImporter"/>.
    /// </summary>
    /// <param name="pack">The verified pack.</param>
    /// <param name="priority">Its search contributors' priority: lower comes first in the candidate budget and wins ties.</param>
    public ModuleSetBuilder AddKnowledgePack(Packs.KnowledgePack pack, int priority = 100)
    {
        ArgumentNullException.ThrowIfNull(pack);
        return Add(pack.Manifest, builder =>
        {
            foreach (var view in pack.Manifest.Views.Where(v => v.Search is not null))
                builder.AddContextContributor(c => new Contributors.ViewContributor(c.Resolution, c.Reader, view.Id, priority));
        }, "knowledge-pack; digest=" + pack.Digest + "; signer=" + pack.Bundle.Signer.ToString().ToLowerInvariant());
    }

    /// <summary>
    /// Sets the priority of every contributor a module adds, whatever the module chose (Extensibility 0.13 §D): a host such as
    /// DemoBrain orders its memory packs, and the order becomes priority. Lower comes first in the candidate budget and wins
    /// ties between equal scores; every contributor still runs. 0 to 10,000; the module must be added by activation.
    /// </summary>
    public ModuleSetBuilder Prioritize(string moduleId, int priority)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ArgumentOutOfRangeException.ThrowIfNegative(priority);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(priority, 10_000);
        _priorities[moduleId] = priority;
        return this;
    }

    private ModuleSetBuilder Add(ModuleManifest manifest, Action<IModuleBuilder> configure, string implementation)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (_modules.Any(m => string.Equals(m.Manifest.Id, manifest.Id, StringComparison.Ordinal)))
            throw new ArgumentException($"Module '{manifest.Id}' is already added.", nameof(manifest));
        var registration = new ModuleRegistration(manifest, implementation);
        configure(registration);
        registration.Freeze();
        _modules.Add(registration);
        return this;
    }

    /// <summary>Composes from a profile: its store alias, version ranges and configuration sections.</summary>
    /// <remarks>Every added module must be in the profile and every profile module added. Static composition binds one store.</remarks>
    public ModuleSetBuilder UseProfile(ModuleCompositionProfile profile)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        return this;
    }

    /// <summary>Binds the module store. Modules with an ontology or views need one; contributors and providers read and write through it.</summary>
    public ModuleSetBuilder UseStore(IModuleStoreAdapter adapter)
    {
        Store = adapter ?? throw new ArgumentNullException(nameof(adapter));
        return this;
    }

    /// <summary>
    /// Grants a chat model to the modules whose manifest declares host feature <see cref="ModuleHostFeatures.Inference"/>,
    /// resolved per scope. Model access is composition: without a grant such a module does not activate, and a module that
    /// does not declare the feature never receives it.
    /// </summary>
    public ModuleSetBuilder GrantChatClient(Func<IServiceProvider, Microsoft.Extensions.AI.IChatClient> chatClient)
    {
        ChatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        return this;
    }

    /// <summary>The configuration module options bind from; by default the host's registered <see cref="IConfiguration"/>.</summary>
    public ModuleSetBuilder UseConfiguration(IConfiguration configuration)
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        return this;
    }

    // The code that configured the module, by assembly identity and build: a rebuilt module changes the set fingerprint.
    private static string Identity(Assembly assembly) =>
        assembly.GetName().FullName + "; mvid=" + assembly.ManifestModule.ModuleVersionId.ToString("N");
}

/// <summary>One added module's declarations, recorded at registration.</summary>
[Experimental("AMEXT001")]
internal sealed class ModuleRegistration(ModuleManifest manifest, string implementation) : IModuleBuilder
{
    private readonly List<Func<ModuleActivationContext, IContextContributor>> _contributors = [];
    private readonly List<Func<ModuleActivationContext, IIngestionProvider>> _providers = [];
    private bool _frozen;

    public ModuleManifest Manifest { get; } = manifest;
    public string Implementation { get; } = implementation;
    public ModuleOptionsBinding? Options { get; private set; }
    public IReadOnlyList<Func<ModuleActivationContext, IContextContributor>> Contributors => _contributors;
    public IReadOnlyList<Func<ModuleActivationContext, IIngestionProvider>> Providers => _providers;

    /// <summary>Ends declaration: the set is fixed once Configure returns.</summary>
    public void Freeze() => _frozen = true;

    public void Configure<TOptions>(IValidateOptions<TOptions>? validator = null) where TOptions : class, new()
    {
        Open();
        if (Options is not null) throw new InvalidOperationException($"Module '{Manifest.Id}' already configures {Options.Type.Name}.");
        Options = new ModuleOptionsBinding(typeof(TOptions), (section, services) =>
        {
            var value = new TOptions();
            section.Bind(value);
            var failures = new List<string>();
            IEnumerable<IValidateOptions<TOptions>> validators = validator is null ? [] : [validator];
            foreach (var check in validators.Concat(services.GetServices<IValidateOptions<TOptions>>()))
            {
                var result = check.Validate(Microsoft.Extensions.Options.Options.DefaultName, value);
                if (result.Failed) failures.AddRange(result.Failures ?? [result.FailureMessage]);
            }
            return (value, failures);
        });
    }

    public void AddContextContributor(Func<ModuleActivationContext, IContextContributor> create)
    {
        Open();
        _contributors.Add(create ?? throw new ArgumentNullException(nameof(create)));
    }

    public void AddIngestionProvider(Func<ModuleActivationContext, IIngestionProvider> create)
    {
        Open();
        _providers.Add(create ?? throw new ArgumentNullException(nameof(create)));
    }

    private void Open()
    {
        if (_frozen) throw new InvalidOperationException($"Module '{Manifest.Id}' declares only while it is being added.");
    }
}

/// <summary>Binds and validates one module's options type from a configuration section.</summary>
[Experimental("AMEXT001")]
internal sealed record ModuleOptionsBinding(Type Type,
    Func<IConfiguration, IServiceProvider, (object Value, IReadOnlyList<string> Failures)> Bind);
