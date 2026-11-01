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
    internal PackTrialFloor? TrialFloor { get; private set; }

    /// <summary>The verified erasures this host holds (feature 08): the records they name are never offered.</summary>
    internal List<Packs.ModuleErasure> Erasures { get; } = [];

    /// <summary>
    /// Holds verified erasures (feature 08, R-CORE-134): every view and walk leaves out the records they name, whatever the store
    /// holds, until and after an import applies them. A loaded pack's own verified erasures are added with the pack.
    /// </summary>
    public ModuleSetBuilder UseErasures(IEnumerable<Packs.ModuleErasure> erasures)
    {
        ArgumentNullException.ThrowIfNull(erasures);
        Erasures.AddRange(erasures);
        return this;
    }

    internal int? MinimumConfidenceMicro { get; private set; }

    internal ModelEndpoint Endpoint { get; private set; } = ModelEndpoint.External;

    /// <summary>
    /// States where the host's model runs (feature 10, R-CORE-139). A pack whose usage policy does not allow its text to reach a
    /// model of this class (<c>rehydrate:local</c>, <c>rehydrate:private</c> or <c>rehydrate:external</c>) is withheld from
    /// every view and walk. Unstated, the endpoint is external.
    /// </summary>
    public ModuleSetBuilder UseModelEndpoint(ModelEndpoint endpoint)
    {
        if (!Enum.IsDefined(endpoint)) throw new ArgumentOutOfRangeException(nameof(endpoint));
        Endpoint = endpoint;
        return this;
    }

    // The usage policies this host holds (feature 10), by their digest: whether a verb is allowed at an instant. Registered by
    // the pack loader for the packs it adds, their members, and the snapshot packs whose records the host imported. A record
    // governed by a policy not here is withheld.
    internal List<(string Digest, Func<string, DateTimeOffset, bool> Allows)> PackPolicies { get; } = [];

    /// <summary>
    /// Leaves out of every view and walk the records whose effective confidence (feature 09, R-CORE-137) is below
    /// <paramref name="micro"/> millionths. A record nothing caps has full confidence.
    /// </summary>
    public ModuleSetBuilder UseMinimumConfidence(int micro)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(micro);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(micro, Packs.ModuleConfidence.Full);
        MinimumConfidenceMicro = micro;
        return this;
    }
    internal IReadOnlyList<string> CertificateTrust => _certificateTrust;
    internal IReadOnlyList<ModuleConsentGrant> ConsentGrants => _consentGrants;
    private readonly List<ModuleConsentGrant> _consentGrants = [];
    private readonly List<string> _mandatoryTrials = [];
    private readonly List<string> _certificateTrust = [];

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
    public ModuleSetBuilder AddKnowledgePack(Packs.KnowledgePack pack, int priority = 100) => AddKnowledgePack(pack, priority, pack?.ValidUntil);

    /// <summary>A verified knowledge pack valid until <paramref name="validUntil"/>: after it, its contributors go silent (PACK-staleness).</summary>
    internal ModuleSetBuilder AddKnowledgePack(Packs.KnowledgePack pack, int priority, DateTimeOffset? validUntil)
    {
        ArgumentNullException.ThrowIfNull(pack);
        return Add(pack.Manifest, builder =>
        {
            foreach (var view in pack.Manifest.Views.Where(v => v.Search is not null))
                builder.AddContextContributor(c => new Contributors.ViewContributor(c.Resolution, c.Reader, view.Id, priority));
        }, "knowledge-pack; digest=" + pack.Digest + "; signer=" + pack.Bundle.Signer.ToString().ToLowerInvariant(), priority, pack.Digest, validUntil);
    }

    /// <summary>
    /// Adds a verified definition pack: its declarations, and no code. The fingerprint names the pack's digest and signers, not
    /// the assembly that loaded it (PACK-review 2.19), and a trial decision must name the same digest (PACK-bind).
    /// </summary>
    internal ModuleSetBuilder AddDefinitionPack(ModuleManifest manifest, string digest, string signers, int traversalPriority = 100,
        DateTimeOffset? validUntil = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(digest);
        return Add(manifest, _ => { }, "definition-pack; digest=" + digest + "; signer=" + signers, traversalPriority, digest, validUntil);
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
        string? packDigest = null, DateTimeOffset? validUntil = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (_modules.Any(m => string.Equals(m.Manifest.Id, manifest.Id, StringComparison.Ordinal)))
            throw new ArgumentException($"Module '{manifest.Id}' is already added.", nameof(manifest));
        var registration = new ModuleRegistration(manifest, implementation, packDigest) { ValidUntil = validUntil };
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
    /// Sets the least a trial must claim before this host counts it (review 6, M8): a confidence, a task count, and targets
    /// it must declare at least as strictly. A pack whose trial claims less is left off, with the reason. Needs
    /// <see cref="RequireTrials"/>; pair it with <see cref="RequireTrialsFor"/> so declaring no trial does not avoid it.
    /// </summary>
    /// <exception cref="ArgumentException">The floor names a confidence a trial cannot declare, or a task count outside 10 to 200.</exception>
    public ModuleSetBuilder RequireTrialFloor(PackTrialFloor floor)
    {
        ArgumentNullException.ThrowIfNull(floor);
        if (floor.ConfidencePermille is { } c && PackNumbers.Problem("confidencePermille", c) is { } wrong)
            throw new ArgumentException($"A floor's confidence: {wrong}.", nameof(floor));
        if (floor.Tasks is < ModuleTrial.MinimumTasks or > ModuleTrial.MaximumTasks)
            throw new ArgumentException($"A floor's task count is {ModuleTrial.MinimumTasks} to {ModuleTrial.MaximumTasks}.", nameof(floor));
        if (floor.ExpireAfterDays is < 1 or > 3650) throw new ArgumentException("A floor's expiry is 1 to 3,650 days.", nameof(floor));
        if (floor.Targets is { } targets && ModuleManifestValidator.TargetProblems(targets).FirstOrDefault() is { Name: not null } problem)
            throw new ArgumentException($"A floor's {problem.Name}: {problem.Problem}.", nameof(floor));
        if (floor.Safety is { } safety && (safety.Probes is not { } suite || !PackText.IsDisplayText(suite, 100, line: true)
                || PackNumbers.Problem("maxFollowMicro", safety.MaxFollowMicro) is not null))
            throw new ArgumentException("A floor's safety names a probe suite (one line of 1 to 100 display-safe characters) and a follow rate in millionths.", nameof(floor));
        TrialFloor = floor;
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
            if (!IsValidPrefix(prefix))
                throw new ArgumentException($"'{prefix}' is not a pack id prefix (lowercase segments such as io.example).", nameof(idPrefixes));
            _mandatoryTrials.Add(prefix);
        }
        return this;
    }

    /// <summary>
    /// Trusts evaluation certificates in these namespaces (pack format v1.2 §16, certificate trust): a pack whose id is a
    /// prefix or lies under it goes on with a decision this host made from a certificate it verified (<c>ampack admit</c>,
    /// a key the trust policy names as evaluator for the pack's id). Everywhere else only the host's own trial counts (proven
    /// here, the default), and a decision made from a certificate is set aside. Needs <see cref="RequireTrials"/>.
    /// </summary>
    /// <param name="idPrefixes">Pack id prefixes such as <c>io.example</c>; <c>io.example</c> covers <c>io.example.dojo</c>, not <c>io.examples</c>.</param>
    public ModuleSetBuilder TrustCertificatesFor(params string[] idPrefixes)
    {
        ArgumentNullException.ThrowIfNull(idPrefixes);
        if (idPrefixes.Length == 0) throw new ArgumentException("Name at least one pack id prefix.", nameof(idPrefixes));
        foreach (var prefix in idPrefixes)
        {
            if (!IsValidPrefix(prefix))
                throw new ArgumentException($"'{prefix}' is not a pack id prefix (lowercase segments such as io.example).", nameof(idPrefixes));
            _certificateTrust.Add(prefix);
        }
        return this;
    }

    /// <summary>
    /// Lets <paramref name="module"/> link into, and walk into, the records of <paramref name="into"/> although
    /// <paramref name="into"/> does not list it in containableBy (PACK-consent): the host overriding a publisher's consent
    /// for its own store. The set fingerprint names every grant.
    /// </summary>
    public ModuleSetBuilder GrantConsent(string module, string into)
    {
        if (!ModuleIdentity.IsValidModuleId(module)) throw new ArgumentException($"'{module}' is not a module id.", nameof(module));
        if (!ModuleIdentity.IsValidModuleId(into)) throw new ArgumentException($"'{into}' is not a module id.", nameof(into));
        if (_consentGrants.All(g => g.Module != module || g.Into != into)) _consentGrants.Add(new ModuleConsentGrant(module, into));
        return this;
    }

    /// <summary>
    /// Whether <paramref name="prefix"/> covers <paramref name="id"/>: equal, or a whole leading run of its segments. The same
    /// rule as a trust policy's <c>idPrefixes</c> (pack format v1.1 H3).
    /// </summary>
    internal static bool Covers(string prefix, string id) => PackIds.PackNamespace.Covers(prefix, id);

    /// <summary>
    /// Whether <paramref name="prefix"/> is a pack id prefix, by the same rule as a trust policy's (review 1, L17): one or more
    /// lowercase id segments, optionally ending with a dot, at most 64 characters. A prefix that could never cover an id (say,
    /// upper case) is refused rather than leaving its namespace silently ungated.
    /// </summary>
    internal static bool IsValidPrefix(string? prefix) => PackIds.PackNamespace.IsValidPrefix(prefix);

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
    /// <summary>For a pack that declares <c>validUntil</c>, when it expires: after it, its contributors go silent.</summary>
    public DateTimeOffset? ValidUntil { get; init; }
    /// <summary>When the trial decision that switched it on expires under the host's floor (feature 11), set by the gate.</summary>
    public DateTimeOffset? DecisionExpires { get; set; }
    /// <summary>
    /// For a pack its decision admitted in part (feature 11, R3), the parts admitted: relations as <c>NAME:out</c> or
    /// <c>NAME:in</c>, and <c>(anchor)</c> for the anchors' own records; null when the whole pack is admitted. Set by the gate.
    /// </summary>
    public IReadOnlySet<string>? AdmittedParts { get; set; }
    /// <summary>When it goes silent: its validUntil or its decision's expiry, whichever comes first.</summary>
    public DateTimeOffset? OffAfter => ValidUntil is { } until && DecisionExpires is { } expires ? (until < expires ? until : expires) : ValidUntil ?? DecisionExpires;
    /// <summary>
    /// For a pack added on its own, the digest of the latest evaluation certificate it carries that the host trusted as it
    /// loaded the pack (empty when none): a decision the host made from a certificate counts only if it names that one. Null for
    /// a family member or compiled code, where only the host's own trials count.
    /// </summary>
    public IReadOnlyList<string>? TrustedCertificates { get; set; }
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
