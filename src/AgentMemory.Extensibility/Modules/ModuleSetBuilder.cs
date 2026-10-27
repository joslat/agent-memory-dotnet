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
    internal IPackActivations? Activations { get; private set; }
    internal IReadOnlyList<string> MandatoryTrials => _mandatoryTrials;
    private readonly List<string> _mandatoryTrials = [];

    /// <summary>Adds a module from its assembly: the embedded manifest and its <see cref="IAgentMemoryModule"/>.</summary>
    public ModuleSetBuilder Add<TModule>() where TModule : IAgentMemoryModule, new() =>
        Add(ModuleManifest.Load(typeof(TModule).Assembly), new TModule().Configure);

    /// <summary>
    /// Adds a composite pack (KP-composite): every member its manifest names, each with its priority, the ones switched off
    /// left out. <paramref name="configure"/> lets the host switch members and move them in the stack.
    /// </summary>
    /// <exception cref="InvalidOperationException">The composite adds a module it does not name, a version outside its range,
    /// or leaves a named member out; or the host names a member it does not have.</exception>
    public ModuleSetBuilder AddComposite<TComposite>(Action<CompositeOptions>? configure = null) where TComposite : IAgentMemoryComposite, new() =>
        AddComposite(CompositeManifest.Load(typeof(TComposite).Assembly), members => new TComposite().AddMembers(members), configure);

    /// <summary>
    /// Adds a composite from its manifest and a delegate that adds its members: the same checks as a composite assembly, for a
    /// composite that arrives as data, such as a v1.2 composite memory pack whose members are packs.
    /// </summary>
    /// <exception cref="InvalidOperationException">As <see cref="AddComposite{TComposite}"/>.</exception>
    public ModuleSetBuilder AddComposite(CompositeManifest manifest, Action<CompositeMembers> addMembers, Action<CompositeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(addMembers);
        if (manifest.Validate() is { Count: > 0 } problems) throw new InvalidOperationException(string.Join(" ", problems));
        var options = new CompositeOptions();
        configure?.Invoke(options);
        var members = new CompositeMembers(this, manifest, options);
        addMembers(members);
        members.Complete();
        return this;
    }

    /// <summary>Adds a module from its manifest and a configuration delegate, the same path as a module entry.</summary>
    public ModuleSetBuilder Add(ModuleManifest manifest, Action<IModuleBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return Add(manifest, configure, Identity(configure.Method.Module.Assembly));
    }

    /// <summary>
    /// Adds a verified knowledge pack (Extensibility 0.13 §K5): its manifest, and a contributor for each of its search views,
    /// which searches with the turn's question. The pack runs no code; the fingerprint names its bundle digest and signer, and a
    /// trial decision must name the same digest. Its records are imported separately, with <see cref="Packs.KnowledgePackImporter"/>.
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
        }, "knowledge-pack; digest=" + pack.Digest + "; signer=" + pack.Bundle.Signer.ToString().ToLowerInvariant(), priority, pack.Digest);
    }

    /// <summary>
    /// Adds a verified definition pack: its declarations, and no code. The fingerprint names the pack's digest and signers, not
    /// the assembly that loaded it (PACK-review 2.19), and a trial decision must name the same digest (PACK-bind).
    /// </summary>
    internal ModuleSetBuilder AddDefinitionPack(ModuleManifest manifest, string digest, string signers, int traversalPriority = 100)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(digest);
        return Add(manifest, _ => { }, "definition-pack; digest=" + digest + "; signer=" + signers, traversalPriority, digest);
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

    private ModuleSetBuilder Add(ModuleManifest manifest, Action<IModuleBuilder> configure, string implementation, int traversalPriority = 100,
        string? packDigest = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (_modules.Any(m => string.Equals(m.Manifest.Id, manifest.Id, StringComparison.Ordinal)))
            throw new ArgumentException($"Module '{manifest.Id}' is already added.", nameof(manifest));
        var registration = new ModuleRegistration(manifest, implementation, packDigest);
        configure(registration);
        // CMP-1: a module that declares traversal gets its share of the turn's walk, with no code of its own.
        if (manifest.Traversal is not null)
            registration.AddContextContributor(c => new Contributors.TraversalContributor(c.Module.Manifest, c.ModuleSet.Traversal(c.Services), traversalPriority));
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

    /// <summary>
    /// Switches a pack on only when it passed its trial on this host (pack format v1.1 §4, trial-gated activation): a module
    /// whose manifest declares a <see cref="ModuleManifest.Trial"/> composes only with a passing decision for exactly its
    /// manifest, and a module that needs one left off is left off too. <see cref="ModuleSet.SwitchedOff"/> says which and why.
    /// </summary>
    public ModuleSetBuilder RequireTrials(IPackActivations activations)
    {
        Activations = activations ?? throw new ArgumentNullException(nameof(activations));
        return this;
    }

    /// <summary>
    /// Makes a trial mandatory in these namespaces (PACK-require): a module whose id is a prefix or lies under it, segment by
    /// segment as a trust policy scopes keys, and that declares no trial is left off, so leaving the trial out cannot skip the
    /// gate. Needs <see cref="RequireTrials"/>. Without this a pack that declares no trial composes, as the format allows.
    /// </summary>
    /// <param name="idPrefixes">Pack id prefixes such as <c>io.example</c>; <c>io.example</c> covers <c>io.example.dojo</c>, not <c>io.examples</c>.</param>
    public ModuleSetBuilder RequireTrialsFor(params string[] idPrefixes)
    {
        ArgumentNullException.ThrowIfNull(idPrefixes);
        if (idPrefixes.Length == 0) throw new ArgumentException("Name at least one pack id prefix.", nameof(idPrefixes));
        foreach (var prefix in idPrefixes)
        {
            if (string.IsNullOrWhiteSpace(prefix) || prefix.Any(char.IsWhiteSpace) || prefix.StartsWith('.') || prefix.Contains("..", StringComparison.Ordinal))
                throw new ArgumentException($"'{prefix}' is not a pack id prefix.", nameof(idPrefixes));
            _mandatoryTrials.Add(prefix);
        }
        return this;
    }

    /// <summary>
    /// Whether <paramref name="prefix"/> covers <paramref name="id"/>: equal, or a whole leading run of its segments. The same
    /// rule as a trust policy's <c>idPrefixes</c> (pack format v1.1 H3).
    /// </summary>
    internal static bool Covers(string prefix, string id) =>
        id == prefix || id.StartsWith(prefix.EndsWith('.') ? prefix : prefix + ".", StringComparison.Ordinal);

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
internal sealed class ModuleRegistration(ModuleManifest manifest, string implementation, string? packDigest = null) : IModuleBuilder
{
    private readonly List<Func<ModuleActivationContext, IContextContributor>> _contributors = [];
    private readonly List<Func<ModuleActivationContext, IIngestionProvider>> _providers = [];
    private bool _frozen;

    public ModuleManifest Manifest { get; } = manifest;
    public string Implementation { get; } = implementation;
    /// <summary>For a module that came from a pack, the pack's digest, which its trial decision must name; null for compiled code.</summary>
    public string? PackDigest { get; } = packDigest;
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
