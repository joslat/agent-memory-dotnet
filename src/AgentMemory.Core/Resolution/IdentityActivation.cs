using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentMemory.Core.Resolution;

/// <summary>
/// Whether evidence-based identity runs in this host, decided once and read by everything that follows it (ingestion, the
/// writer's event identities, alias capture, context turns, event details at recall and the Neo4j identity schema), so
/// none of them can disagree. <see cref="IdentityResolutionOptions.Enabled"/> null runs it where the store-aware writer
/// runs on a backend with identity storage, which is the configuration that was measured; everywhere else the previous
/// resolver runs and the tiers line says why.
/// </summary>
internal sealed class IdentityActivation : IMemoryTierSource
{
    internal const string SwitchedOff = "switched off";
    internal const string NoStorage = "this backend has no identity storage";
    internal const string NoWriter = "the store-aware writer is off";
    internal const string KnownIssue = "it can merge different things that share a name";

    private readonly IOptions<IdentityResolutionOptions> _options;
    private readonly IServiceScopeFactory _scopes;
    private readonly bool _storage;
    private readonly bool _adjudicator;
    private readonly Lazy<string?> _fallback;

    public IdentityActivation(IOptions<IdentityResolutionOptions> options, IServiceScopeFactory scopes, IServiceProviderIsService services)
    {
        _options = options;
        _scopes = scopes;
        _storage = services.IsService(typeof(IIdentityDecisionRepository)) && services.IsService(typeof(IIdentityCandidateProvider));
        _adjudicator = services.IsService(typeof(IIdentityAdjudicator));
        _fallback = new(() => Fallback(_options.Value.Enabled is null && _storage && WriterRuns()), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Whether evidence-based identity runs; false means the previous resolver.</summary>
    public bool Active => _fallback.Value is null;

    /// <summary>
    /// The same decision for a writer whose switch is already known, without building the writer: what the extraction
    /// options use while they are being built.
    /// </summary>
    public bool ActiveWith(bool storeAwareWriter) => Fallback(storeAwareWriter) is null;

    /// <summary>Context turns read before each extraction: identity's own while it runs, unless more are configured.</summary>
    public int ContextTurns(int configured) => Active ? Math.Max(configured, _options.Value.ContextTurns) : configured;

    /// <summary>Event details at recall: an explicit choice, or whether identity runs.</summary>
    public bool EventDetails(bool? requested) => requested ?? Active;

    /// <summary>Person details at recall: an explicit choice, or whether identity runs.</summary>
    // Opt-in. REV2-2b bounded its read, but a person named only inside facts still gets no identity decision (ILC-35), so
    // a namesake can hide there and recall for people could add the wrong person's facts. It follows identity once that
    // gap is closed.
    public bool PersonDetails(bool? requested) => requested == true;

    public MemoryTier Describe() => _fallback.Value is { } why
        ? new MemoryTier(MemoryTiers.Identity, "previous resolver", $"{why}; {KnownIssue}")
        : new MemoryTier(MemoryTiers.Identity, "evidence-based", NothingConfirms() is { } none ? $"{none}; {EveryMentionNew}" : null);

    internal const string NoConfirmingPolicy = "nothing can confirm a repeat: no type may be confirmed on context and none has identity keys";
    internal const string NoConfirmer = "nothing can confirm a repeat: neither the store-aware writer nor an identity adjudicator runs";
    internal const string EveryMentionNew = "each mention is stored as a new one, for review";

    /// <summary>
    /// IBP-7: switched on explicitly (<see cref="IdentityResolutionOptions.Enabled"/> true), identity may have nothing that
    /// confirms a repeat: no type a writer's reference or the adjudicator may confirm and no identity keys, or such types but
    /// neither the store-aware writer nor an adjudicator to confirm them. Three mentions of a person are then three people
    /// (the battery's rules arm). Automatic mode always has the writer and the measured policies.
    /// </summary>
    private string? NothingConfirms()
    {
        if (_options.Value.Enabled != true) return null;
        IdentityConfiguration configuration;
        try
        {
            configuration = IdentityConfiguration.Capture(_options.Value);
        }
        catch (OptionsValidationException)
        {
            return null; // invalid options fail at the first write, which says why
        }
        if (configuration.HasIdentityProperties || configuration.HasUniqueNameNamespace) return null;
        if (!configuration.AllowsContextualConfirmation) return NoConfirmingPolicy;
        var adjudicates = _adjudicator && configuration.MaxAdjudicationsPerIngestion > 0;
        return adjudicates || WriterRuns() ? null : NoConfirmer;
    }

    private string? Fallback(bool storeAwareWriter) => _options.Value.Enabled switch
    {
        false => SwitchedOff,
        true => null,
        null when !_storage => NoStorage,
        null when !storeAwareWriter => NoWriter,
        _ => null,
    };

    private bool WriterRuns()
    {
        using var scope = _scopes.CreateScope();
        try
        {
            return scope.ServiceProvider.GetServices<IMemoryWriter>().Any(writer => writer.IsEnabled);
        }
        catch (InvalidOperationException)
        {
            // A writer that cannot be built (no chat model registered) cannot run either.
            return false;
        }
    }
}
