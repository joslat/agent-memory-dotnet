using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using AgentMemory.Extensibility.Capabilities;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Reading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentMemory.Extensibility.Modules;

/// <summary>
/// The active modules for the process lifetime: composed against the host, options bound and validated, and
/// fingerprinted (Extensibility 0.12, static composition). Immutable; there is no reload.
/// </summary>
/// <remarks>
/// <see cref="Fingerprint"/> covers what actually runs: the composition (profile and manifests), each module's
/// implementation assembly and build, and a hash of each module's bound configuration. Trust is NuGet's restore-time
/// trust in statically referenced packages; nothing is loaded at run time.
/// </remarks>
[Experimental("AMEXT001")]
public sealed class ModuleSet
{
    private const string ImplicitStore = "memory";
    private readonly IReadOnlyList<ModuleRegistration> _registrations;

    private ModuleSet(ModuleResolution resolution, IReadOnlyList<ActiveModule> modules, string compositionFingerprint,
        string fingerprint, IModuleStoreAdapter? store, IReadOnlyList<ModuleRegistration> registrations)
    {
        Resolution = resolution; Modules = modules; CompositionFingerprint = compositionFingerprint;
        Fingerprint = fingerprint; Store = store; _registrations = registrations;
    }

    /// <summary>The validated metadata of the active modules, in dependency order, for the bound store.</summary>
    public ModuleResolution Resolution { get; }

    /// <summary>The active modules, in dependency order.</summary>
    public IReadOnlyList<ActiveModule> Modules { get; }

    /// <summary>Profile plus manifests, as <see cref="ModuleProfileResolution.CompositionFingerprint"/> computes it.</summary>
    public string CompositionFingerprint { get; }

    /// <summary>SHA-256 of the composition, implementations, configuration hashes and trust mode.</summary>
    public string Fingerprint { get; }

    internal IModuleStoreAdapter? Store { get; }

    internal Func<IServiceProvider, Microsoft.Extensions.AI.IChatClient>? ChatClient { get; private init; }

    /// <summary>The SDK version modules are resolved against: this assembly's.</summary>
    internal static ModuleVersion SdkVersion { get; } = Sdk();

    internal static ModuleSet Activate(ModuleSetBuilder builder, IServiceProvider services)
    {
        var registrations = builder.Modules;
        var store = builder.Store;
        var provider = store?.Provider ?? "none";
        var profile = builder.Profile ?? ImplicitProfile(registrations, provider);
        if (profile.Stores.Count != 1)
            throw new ModuleActivationException([$"Static composition binds one module store; the profile declares {profile.Stores.Count}."]);
        var alias = profile.Stores.Keys.Single();
        var composition = ModuleHostProfileResolver.Resolve(profile, [.. registrations.Select(r => r.Manifest)], SdkVersion,
            new Dictionary<string, ModuleHostStore>(StringComparer.Ordinal)
            {
                [alias] = new() { Provider = provider, Services = services, SchemaProbe = store, ChatClient = builder.ChatClient },
            });
        if (!composition.Succeeded) throw new ModuleActivationException(composition.Errors);

        var configuration = builder.Configuration ?? services.GetService<IConfiguration>();
        var errors = new SortedSet<string>(StringComparer.Ordinal);
        var modules = new List<ActiveModule>();
        foreach (var composed in composition.Modules)
        {
            var registration = registrations.Single(r => string.Equals(r.Manifest.Id, composed.Manifest.Id, StringComparison.Ordinal));
            object? options = null;
            string? configurationHash = null;
            if (registration.Options is { } binding)
            {
                if (configuration is null)
                {
                    errors.Add($"Module '{composed.Manifest.Id}' has options but no IConfiguration is registered or supplied.");
                    continue;
                }
                var section = configuration.GetSection(composed.ConfigurationSection);
                var where = $"Module '{composed.Manifest.Id}' configuration '{composed.ConfigurationSection}'";
                try
                {
                    var (value, failures) = binding.Bind(section, services);
                    foreach (var failure in failures) errors.Add($"{where}: {failure}");
                    options = value;
                }
                catch (Exception exception) when (exception is InvalidOperationException or FormatException or ArgumentException)
                {
                    // The binder reports a value it cannot convert by throwing; it is one more listed problem.
                    errors.Add($"{where} does not bind: {exception.Message}");
                    continue;
                }
                configurationHash = Hash(section);
            }
            modules.Add(new ActiveModule(composed.Manifest, composed.ConfigurationSection, configurationHash,
                registration.Implementation, options));
        }
        if (errors.Count != 0) throw new ModuleActivationException([.. errors]);

        var resolution = composition.ForStore(alias);
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "agentmemory.module-set/v1alpha1",
            composition = composition.CompositionFingerprint,
            store = resolution.ManifestFingerprint,
            trust = "static-reference",
            modules = modules.Select(m => new
            {
                id = m.Manifest.Id, version = m.Manifest.Version.ToString(), implementation = m.Implementation,
                configuration = m.ConfigurationSha256,
            }),
        }))).ToLowerInvariant();
        var set = new ModuleSet(resolution, modules.AsReadOnly(), composition.CompositionFingerprint!, fingerprint, store,
            registrations) { ChatClient = builder.ChatClient };
        services.GetService<ILoggerFactory>()?.CreateLogger<ModuleSet>().LogInformation(
            "Module set {Fingerprint} active: {Modules}.", fingerprint,
            string.Join(", ", modules.Select(m => m.Manifest.Id + "@" + m.Manifest.Version)));
        return set;
    }

    /// <summary>Creates every module's contributors for one scope, refusing any identity outside its own module.</summary>
    internal IReadOnlyList<IContextContributor> CreateContributors(IServiceProvider services) =>
        Create(services, r => r.Contributors, (module, contributor) =>
        {
            var descriptor = contributor.Descriptor
                ?? throw new InvalidOperationException($"Module '{module}' added a contributor without a descriptor.");
            Owned(module, "contributor", descriptor.Id);
            foreach (var section in descriptor.SectionTypes) Owned(module, "section", section);
        });

    /// <summary>Creates every module's ingestion providers for one scope, refusing any identity outside its own module.</summary>
    internal IReadOnlyList<IIngestionProvider> CreateProviders(IServiceProvider services) =>
        Create(services, r => r.Providers, (module, provider) => Owned(module, "ingestion provider", provider.Descriptor?.Id));

    /// <summary>Creates a reader bound to one active module, through the bound store.</summary>
    internal IModuleReader CreateReader(IServiceProvider services, string moduleId) =>
        (Store ?? throw new InvalidOperationException($"Module '{moduleId}' reads, but no module store is bound (UseStore)."))
        .CreateReader(services, Resolution, moduleId);

    /// <summary>The modules that add ingestion providers, which need a store binding.</summary>
    internal IEnumerable<string> ModulesWithProviders => _registrations.Where(r => r.Providers.Count > 0).Select(r => r.Manifest.Id);

    /// <summary>Contributor factories in a fixed order (registration order), so each can be registered by index.</summary>
    internal static int CountContributors(ModuleSetBuilder builder) => builder.Modules.Sum(m => m.Contributors.Count);

    private IReadOnlyList<T> Create<T>(IServiceProvider services,
        Func<ModuleRegistration, IReadOnlyList<Func<ModuleActivationContext, T>>> factories, Action<string, T> check)
        where T : class
    {
        var created = new List<T>();
        // Registration order, not dependency order: the host registered contributors by this index.
        foreach (var registration in _registrations)
        {
            var module = Modules.Single(m => string.Equals(m.Manifest.Id, registration.Manifest.Id, StringComparison.Ordinal));
            var context = new ModuleActivationContext(this, module, services);
            foreach (var create in factories(registration))
            {
                var value = create(context) ?? throw new InvalidOperationException($"Module '{module.Manifest.Id}' created nothing.");
                check(module.Manifest.Id, value);
                created.Add(value);
            }
        }
        return created.AsReadOnly();
    }

    private static void Owned(string module, string what, string? id)
    {
        if (!ModuleIdentity.IsOwnedId(module, id))
            throw new InvalidOperationException($"Module '{module}' added {what} '{id}', which is outside its own namespace '{module}:'.");
    }

    private static ModuleCompositionProfile ImplicitProfile(IReadOnlyList<ModuleRegistration> registrations, string provider) =>
        ModuleCompositionProfile.Parse(JsonSerializer.Serialize(new
        {
            format = "agentmemory.modules/v1alpha1",
            stores = new Dictionary<string, object> { [ImplicitStore] = new { provider } },
            modules = registrations.Select(r => new
            {
                id = r.Manifest.Id, version = "[" + r.Manifest.Version + "]", store = ImplicitStore, manifest = "static",
            }),
        }));

    private static string Hash(IConfiguration section) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
        section.AsEnumerable(makePathsRelative: true).Where(p => p.Value is not null)
            .OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new[] { p.Key, p.Value })))).ToLowerInvariant();

    private static ModuleVersion Sdk()
    {
        var version = typeof(ModuleSet).Assembly.GetName().Version!;
        return ModuleVersion.Parse($"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}");
    }
}

/// <summary>One active module: its manifest, configuration and the implementation that configured it.</summary>
[Experimental("AMEXT001")]
public sealed class ActiveModule
{
    private readonly object? _options;

    internal ActiveModule(ModuleManifest manifest, string configurationSection, string? configurationSha256,
        string implementation, object? options)
    {
        Manifest = manifest; ConfigurationSection = configurationSection; ConfigurationSha256 = configurationSha256;
        Implementation = implementation; _options = options;
    }

    /// <summary>The module's validated manifest.</summary>
    public ModuleManifest Manifest { get; }

    /// <summary>The configuration section its options bind from.</summary>
    public string ConfigurationSection { get; }

    /// <summary>SHA-256 of the bound section's keys and values; null when the module has no options.</summary>
    public string? ConfigurationSha256 { get; }

    /// <summary>The assembly that configured it, with its build id.</summary>
    public string Implementation { get; }

    /// <summary>The module's bound, validated options.</summary>
    /// <exception cref="InvalidOperationException">The module did not configure <typeparamref name="TOptions"/>.</exception>
    public TOptions GetOptions<TOptions>() where TOptions : class =>
        _options as TOptions ?? throw new InvalidOperationException($"Module '{Manifest.Id}' does not configure {typeof(TOptions).Name}.");
}

/// <summary>What a module's factories receive: the active set, the module, the scope's services and a bound reader.</summary>
/// <remarks>Static composition trusts module code as any referenced package; <see cref="Services"/> is the host's scope.</remarks>
[Experimental("AMEXT001")]
public sealed class ModuleActivationContext
{
    private IModuleReader? _reader;

    internal ModuleActivationContext(ModuleSet set, ActiveModule module, IServiceProvider services)
    {
        ModuleSet = set; Module = module; Services = services;
    }

    /// <summary>The active module set.</summary>
    public ModuleSet ModuleSet { get; }

    /// <summary>The module being created.</summary>
    public ActiveModule Module { get; }

    /// <summary>The active resolution, as contributors and providers take it.</summary>
    public ModuleResolution Resolution => ModuleSet.Resolution;

    /// <summary>The scope's services.</summary>
    public IServiceProvider Services { get; }

    /// <summary>A reader bound to this module, through the bound store.</summary>
    public IModuleReader Reader => _reader ??= ModuleSet.CreateReader(Services, Module.Manifest.Id);

    /// <summary>This module's bound, validated options.</summary>
    public TOptions GetOptions<TOptions>() where TOptions : class => Module.GetOptions<TOptions>();

    /// <summary>The chat model the host granted, for a module that declares host feature <see cref="ModuleHostFeatures.Inference"/>.</summary>
    /// <exception cref="InvalidOperationException">The module does not declare the feature.</exception>
    public Microsoft.Extensions.AI.IChatClient ChatClient => Module.Manifest.Host.Contains(ModuleHostFeatures.Inference, StringComparer.Ordinal)
        ? (ModuleSet.ChatClient ?? throw new InvalidOperationException("No chat model is granted to modules."))(Services)
        : throw new InvalidOperationException($"Module '{Module.Manifest.Id}' does not declare host feature Inference; it gets no chat model.");
}
