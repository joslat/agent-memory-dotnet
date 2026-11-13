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
    private readonly Lazy<string?> _fallback;

    public IdentityActivation(IOptions<IdentityResolutionOptions> options, IServiceScopeFactory scopes, IServiceProviderIsService services)
    {
        _options = options;
        _scopes = scopes;
        _storage = services.IsService(typeof(IIdentityDecisionRepository)) && services.IsService(typeof(IIdentityCandidateProvider));
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
    public bool PersonDetails(bool? requested) => requested ?? Active;

    public MemoryTier Describe() => _fallback.Value is { } why
        ? new MemoryTier(MemoryTiers.Identity, "previous resolver", $"{why}; {KnownIssue}")
        : new MemoryTier(MemoryTiers.Identity, "evidence-based");

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
