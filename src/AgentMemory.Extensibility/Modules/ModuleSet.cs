using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using AgentMemory.Extensibility.Capabilities;
using AgentMemory.Extensibility.Context;
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
    // One walk service per service scope: the store's traversal reader may depend on scoped services (CMP-1).
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<IServiceProvider, Context.Traversal.TraversalService> _traversal = new();

    private ModuleSet(ModuleResolution resolution, IReadOnlyList<ActiveModule> modules, string compositionFingerprint,
        string fingerprint, IModuleStoreAdapter? store, IReadOnlyList<ModuleRegistration> registrations, IReadOnlyList<SwitchedOffModule> switchedOff)
    {
        Resolution = resolution; Modules = modules; CompositionFingerprint = compositionFingerprint;
        Fingerprint = fingerprint; Store = store; _registrations = registrations; SwitchedOff = switchedOff;
    }

    /// <summary>The added modules a trial left off (<see cref="ModuleSetBuilder.RequireTrials"/>), and why. Empty when trials are not required.</summary>
    public IReadOnlyList<SwitchedOffModule> SwitchedOff { get; }

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
        if (builder.MandatoryTrials.Count != 0 && builder.Activations is null)
            throw new ModuleActivationException(["RequireTrialsFor makes trials mandatory, so it needs the host's decisions: call RequireTrials too."]);
        if (builder.CertificateTrust.Count != 0 && builder.Activations is null)
            throw new ModuleActivationException(["TrustCertificatesFor accepts decisions made from certificates, so it needs the host's decisions: call RequireTrials too."]);
        var (registrations, switchedOff) = PackTrialGate.Apply(builder.Modules, builder.Activations, builder.MandatoryTrials, builder.CertificateTrust);
        var store = builder.Store;
        var provider = store?.Provider ?? "none";
        var profile = builder.Profile?.Without(switchedOff.Select(o => o.ModuleId).ToHashSet(StringComparer.Ordinal)) ?? ImplicitProfile(registrations, provider);
        if (profile.Stores.Count != 1)
            throw new ModuleActivationException([$"Static composition binds one module store; the profile declares {profile.Stores.Count}."]);
        var alias = profile.Stores.Keys.Single();
        var composition = ModuleHostProfileResolver.Resolve(profile, [.. registrations.Select(r => r.Manifest)], SdkVersion,
            new Dictionary<string, ModuleHostStore>(StringComparer.Ordinal)
            {
                [alias] = new() { Provider = provider, Services = services, SchemaProbe = store, ChatClient = builder.ChatClient },
            });
        if (!composition.Succeeded) throw new ModuleActivationException(composition.Errors);

        // A host without configuration (a console host, a test, the pack runner) gets each module's defaults, validated
        // like any bound values: the defaults are the module's own choice, and its validator still decides.
        var configuration = builder.Configuration ?? services.GetService<IConfiguration>() ?? new ConfigurationBuilder().Build();
        var errors = new SortedSet<string>(StringComparer.Ordinal);
        var modules = new List<ActiveModule>();
        foreach (var composed in composition.Modules)
        {
            var registration = registrations.Single(r => string.Equals(r.Manifest.Id, composed.Manifest.Id, StringComparison.Ordinal));
            object? options = null;
            string? configurationHash = null;
            if (registration.Options is { } binding)
            {
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
        foreach (var prioritized in builder.Priorities.Keys.Where(id => modules.All(m => m.Manifest.Id != id) && switchedOff.All(o => o.ModuleId != id))
                     .Order(StringComparer.Ordinal))
            errors.Add($"Priority set for module '{prioritized}', which is not added.");
        if (errors.Count != 0) throw new ModuleActivationException([.. errors]);

        var resolution = composition.ForStore(alias) with { ConsentGrants = [.. builder.ConsentGrants] };
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
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
        });
        // Host-set priorities change the order contributors run in, so they join the fingerprint; without any, it is unchanged.
        var priorities = builder.Priorities.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        if (priorities.Count != 0) canonical = [.. canonical, .. JsonSerializer.SerializeToUtf8Bytes(new { priorities })];
        // Consent grants change what may link and walk where, so they join it too; without any, it is unchanged.
        if (builder.ConsentGrants.Count != 0)
            canonical = [.. canonical, .. JsonSerializer.SerializeToUtf8Bytes(new
            {
                consentGrants = builder.ConsentGrants.OrderBy(g => g.Module, StringComparer.Ordinal).ThenBy(g => g.Into, StringComparer.Ordinal)
                    .Select(g => new[] { g.Module, g.Into }),
            })];
        var fingerprint = Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
        var set = new ModuleSet(resolution, modules.AsReadOnly(), composition.CompositionFingerprint!, fingerprint, store,
            registrations, switchedOff)
        {
            ChatClient = builder.ChatClient, Priorities = priorities.AsReadOnly(),
            Slots = [.. builder.Modules.SelectMany(m => Enumerable.Repeat(m.Manifest.Id, m.Contributors.Count))],
        };
        var log = services.GetService<ILoggerFactory>()?.CreateLogger<ModuleSet>();
        log?.LogInformation("Module set {Fingerprint} active: {Modules}.", fingerprint,
            string.Join(", ", modules.Select(m => m.Manifest.Id + "@" + m.Manifest.Version)));
        foreach (var off in switchedOff) log?.LogInformation("Module {Module}@{Version} is off: {Reason}.", off.ModuleId, off.Version, off.Reason);
        return set;
    }

    /// <summary>Creates every module's contributors for one scope, refusing any identity outside its own module.</summary>
    internal IReadOnlyList<IContextContributor> CreateContributors(IServiceProvider services) =>
        [.. Create(services, r => r.Contributors, (module, contributor) =>
        {
            var descriptor = contributor.Descriptor
                ?? throw new InvalidOperationException($"Module '{module}' added a contributor without a descriptor.");
            Owned(module, "contributor", descriptor.Id);
            foreach (var section in descriptor.SectionTypes) Owned(module, "section", section);
        }).Select(c => Priorities.FirstOrDefault(p => ModuleIdentity.IsOwnedId(p.Key, c.Descriptor.Id)) is { Key: not null } p
            ? new PrioritizedContributor(c, p.Value) : c)
          .Select(c => _registrations.FirstOrDefault(r => r.ValidUntil is not null && ModuleIdentity.IsOwnedId(r.Manifest.Id, c.Descriptor.Id)) is { } expiring
            ? new ExpiringContributor(c, expiring.ValidUntil!.Value, services.GetService<TimeProvider>() ?? TimeProvider.System) : c)];

    /// <summary>
    /// The module of each contributor slot the host registered (<see cref="CountContributors"/>: every contributor of every
    /// module added, gated on or not), in registration order.
    /// </summary>
    internal IReadOnlyList<string> Slots { get; private init; } = [];

    /// <summary>
    /// This scope's contributor for every slot: a module the trial gate switched off keeps its slots, each an inert contributor
    /// the compiler drops, so every other slot stays on its own module's contributor (review 2: the slots indexed a list of
    /// the gated-on modules' contributors only, so one pack switched off shifted the rest and every compile threw).
    /// </summary>
    internal IReadOnlyList<IContextContributor> CreateSlots(IServiceProvider services)
    {
        var created = CreateContributors(services);
        var byModule = new Dictionary<string, Queue<IContextContributor>>(StringComparer.Ordinal);
        var next = 0;
        foreach (var registration in _registrations)
        {
            var mine = byModule.TryGetValue(registration.Manifest.Id, out var queue) ? queue : byModule[registration.Manifest.Id] = new();
            for (var i = 0; i < registration.Contributors.Count; i++) mine.Enqueue(created[next++]);
        }
        var inert = new Dictionary<string, int>(StringComparer.Ordinal);
        return [.. Slots.Select(module => byModule.TryGetValue(module, out var queue) && queue.Count > 0 ? queue.Dequeue()
            : new SwitchedOffContributor(module, inert[module] = inert.GetValueOrDefault(module) + 1,
                SwitchedOff.FirstOrDefault(o => o.ModuleId == module)?.Reason ?? "the module is not active"))];
    }

    /// <summary>Host-set contributor priorities by module (<see cref="ModuleSetBuilder.Prioritize"/>).</summary>
    internal IReadOnlyDictionary<string, int> Priorities { get; init; } = new Dictionary<string, int>().AsReadOnly();

    /// <summary>A module's contributor at the priority its host set; everything else is the module's own.</summary>
    private sealed class PrioritizedContributor(IContextContributor inner, int priority) : IContextContributor
    {
        public ContextContributorDescriptor Descriptor { get; } = inner.Descriptor with { Priority = priority };
        public ValueTask<bool> AppliesAsync(ContextRequest request, CancellationToken cancellationToken) => inner.AppliesAsync(request, cancellationToken);
        public Task<ContextSection?> ContributeAsync(ContextRequest request, CancellationToken cancellationToken) => inner.ContributeAsync(request, cancellationToken);
        public Task<ContextContributionResult> ContributeResultAsync(ContextRequest request, CancellationToken cancellationToken = default) =>
            inner.ContributeResultAsync(request, cancellationToken);
    }

    /// <summary>
    /// A contributor of a pack that declares <c>validUntil</c> (PACK-staleness): once the host's clock passes it, the pack
    /// contributes nothing, and says why, even in a host that loaded it while it was valid.
    /// </summary>
    private sealed class ExpiringContributor(IContextContributor inner, DateTimeOffset validUntil, TimeProvider clock) : IContextContributor
    {
        public ContextContributorDescriptor Descriptor => inner.Descriptor;
        public ValueTask<bool> AppliesAsync(ContextRequest request, CancellationToken cancellationToken) => inner.AppliesAsync(request, cancellationToken);
        public Task<ContextSection?> ContributeAsync(ContextRequest request, CancellationToken cancellationToken) =>
            Expired ? Task.FromResult<ContextSection?>(null) : inner.ContributeAsync(request, cancellationToken);
        public Task<ContextContributionResult> ContributeResultAsync(ContextRequest request, CancellationToken cancellationToken = default) =>
            Expired
                ? Task.FromResult(new ContextContributionResult
                {
                    OmissionReason = ContextOmissionReason.Denied,
                    Detail = $"The pack was valid until {validUntil.ToUniversalTime():yyyy-MM-dd HH:mm:ss}Z; this host switched it off.",
                })
                : inner.ContributeResultAsync(request, cancellationToken);
        private bool Expired => clock.GetUtcNow() > validUntil;
    }

    /// <summary>Creates every module's ingestion providers for one scope, refusing any identity outside its own module.</summary>
    internal IReadOnlyList<IIngestionProvider> CreateProviders(IServiceProvider services) =>
        Create(services, r => r.Providers, (module, provider) => Owned(module, "ingestion provider", provider.Descriptor?.Id));

    /// <summary>Creates a reader bound to one active module, through the bound store.</summary>
    internal IModuleReader CreateReader(IServiceProvider services, string moduleId)
    {
        var reader = (Store ?? throw new InvalidOperationException($"Module '{moduleId}' reads, but no module store is bound (UseStore)."))
            .CreateReader(services, Resolution, moduleId);
        // A view may read its dependencies' types: an expired dependency's records leave its answers too (review 2, M2's twin).
        return Live(services) is { } live ? new LiveReader(reader, live) : reader;
    }

    // Drops the records of modules that are not live now, whatever the store returned.
    private sealed class LiveReader(IModuleReader inner, Func<string, bool> live) : IModuleReader
    {
        public string CallerModuleId => inner.CallerModuleId;

        public async Task<ModuleReadResult> FindAsync(ModuleQuery query, ModuleReadContext context, CancellationToken cancellationToken = default)
        {
            var result = await inner.FindAsync(query, context, cancellationToken).ConfigureAwait(false);
            return result.Records.All(r => live(r.ModuleId)) ? result : result with { Records = [.. result.Records.Where(r => live(r.ModuleId))] };
        }
    }

    /// <summary>The traversal walk shared by every module's traversal contributor in one service scope (CMP-1).</summary>
    internal Context.Traversal.TraversalService Traversal(IServiceProvider services) =>
        _traversal.GetValue(services, s => new Context.Traversal.TraversalService(Resolution, Store?.CreateTraversalReader(s, Resolution), Live(s)));

    // Whether a module may take part in a walk now: a pack past its validUntil goes silent in the walk as its contributors
    // do (review 1, M2), so it neither spends the walk's budgets nor leads it to other modules' records.
    private Func<string, bool>? Live(IServiceProvider services)
    {
        var expiring = _registrations.Where(r => r.ValidUntil is not null).ToDictionary(r => r.Manifest.Id, r => r.ValidUntil!.Value, StringComparer.Ordinal);
        if (expiring.Count == 0) return null;
        var clock = services.GetService<TimeProvider>() ?? TimeProvider.System;
        // Valid through the instant itself, off after it, as every twin reads validUntil (review 2: the walk dropped a pack at
        // the instant its contributors and serve still kept).
        return id => !expiring.TryGetValue(id, out var until) || clock.GetUtcNow() <= until;
    }

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
