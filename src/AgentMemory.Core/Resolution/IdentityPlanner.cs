using System.Collections.ObjectModel;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using Microsoft.Extensions.Options;

namespace AgentMemory.Core.Resolution;

/// <summary>Builds an identity plan with bounded candidate reads; it performs no persistence.</summary>
internal sealed class IdentityPlanner
{
    private readonly IIdentityCandidateProvider _provider;
    private readonly IEmbeddingOrchestrator _embeddings;
    private readonly IIdGenerator _ids;
    private readonly TimeProvider _clock;
    private readonly IdentityConfiguration _configuration;
    private readonly bool _semantic;
    private readonly bool _fuzzy;

    public IdentityPlanner(IIdentityCandidateProvider provider, IEmbeddingOrchestrator embeddings,
        IIdGenerator ids, TimeProvider clock, IdentityConfiguration configuration,
        bool useSemanticCandidates = true, bool useFuzzyCandidates = true)
    {
        _provider = provider;
        _embeddings = embeddings;
        _ids = ids;
        _clock = clock;
        _configuration = configuration;
        _semantic = useSemanticCandidates;
        _fuzzy = useFuzzyCandidates;
        if (!configuration.Enabled) return;
        var capabilities = provider.Capabilities;
        if (capabilities is null || !capabilities.BoundedReads || !capabilities.CalendarValueLookup ||
            configuration.HasIdentityProperties && !capabilities.VerifiedKeyLookup ||
            configuration.RequiresCaseInsensitiveIdentityNames && !capabilities.CaseInsensitiveIdentityNameLookup ||
            configuration.RequiresContextRootedLookup && !capabilities.ContextRootedLookup)
            throw new OptionsValidationException(nameof(IdentityResolutionOptions), typeof(IdentityResolutionOptions),
                ["The selected identity backend must guarantee bounded reads, calendar lookup and configured verified-key lookup."]);
    }

    public async Task<IdentityResolutionPlan> PlanAsync(ExtractedEntity mention,
        EntityIdentityResolutionContext context, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        if (!_configuration.Enabled) throw new InvalidOperationException("Evidence-based identity is disabled.");
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(mention);
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(mention.Name) || string.IsNullOrWhiteSpace(mention.Type) ||
            !double.IsFinite(mention.Confidence) || mention.Confidence is < 0 or > 1)
            throw new ArgumentException("The mention needs a name, type and finite extraction confidence in [0,1].", nameof(mention));
        mention = mention with
        {
            Aliases = Array.AsReadOnly(mention.Aliases.ToArray()),
            Attributes = new ReadOnlyDictionary<string, object>(mention.Attributes.ToDictionary(pair => pair.Key, pair => pair.Value))
        };
        context = FreezeContext(context);
        var owner = SharedScopes.WriteOwner(scope);
        if (string.IsNullOrEmpty(owner)) owner = null;
        var effectiveScope = owner is null ? SharedScopes.SharedOnly : MemoryScope.For(owner, scope?.IncludeShared ?? true);
        var policy = _configuration.PolicyFor(mention.Type);
        var literal = IdentityPolicy.IsCalendarLiteral(mention, context, policy, out var value);
        var candidates = new List<IdentityCandidate>();
        var diagnostics = new List<string>();
        IdentityPolicyResult Evaluate() => IdentityPolicy.Evaluate(mention, candidates, context, policy, effectiveScope);
        if (literal && value?.IsComplete != true)
            return MakePlan(mention, context, owner, Evaluate(), candidates, diagnostics);

        var query = new IdentityCandidateQuery
        {
            Name = mention.Name, Type = mention.Type, OwnerId = owner,
            CompatibleTypes = policy.CompatibleTypes, UseFuzzyCandidates = _fuzzy,
            IdentityNameComparison = policy.IdentityNameComparison,
            IncludeShared = owner is null || effectiveScope.IncludeShared, Limit = _configuration.MaxCandidates,
            VerifiedKeys = IdentityPolicy.CompleteKeys(context.VerifiedKeys, policy),
            IdentityProperties = policy.IdentityProperties, NameNamespace = context.NameNamespace,
            CalendarKey = literal ? value!.CanonicalKey : null,
            // ILC-13: the window's read-only context turns, never its roots; only for a type that opts in.
            ContextSourceMessageIds = policy.ReviewContextRootedCandidates && !literal
                ? Array.AsReadOnly(context.Sources.Select(source => source.SourceId).Where(id => !context.SourceMessageIds.Contains(id))
                    .Distinct(StringComparer.Ordinal).Take(32).ToArray())
                : [],
        };
        var exact = await _provider.FindExactCandidatesAsync(query, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        AddCandidates(exact, query.Limit, candidates, effectiveScope);
        var result = Evaluate();
        if (!exact.ExactIdentityLookupComplete)
        {
            diagnostics.Add("exact_lookup_incomplete");
            if (result.Decision == IdentityDecisionKind.Confirmed)
                result = result with { Decision = IdentityDecisionKind.Pending, PendingEntityIds = [result.ConfirmedEntityId!],
                    ConfirmedEntityId = null, CanAdjudicate = false };
            else result = result with { CanAdjudicate = false };
            return MakePlan(mention, context, owner, result, candidates, diagnostics);
        }
        if (literal || result.Decision == IdentityDecisionKind.Confirmed ||
            result.Decision == IdentityDecisionKind.Pending && !result.CanAdjudicate)
            return MakePlan(mention, context, owner, result, candidates, diagnostics);

        var remaining = _configuration.MaxCandidates - exact.Candidates.Count;
        if (remaining > 0 && (_fuzzy || _semantic || query.ContextSourceMessageIds.Count > 0))
        {
            float[]? vector = null;
            if (_semantic)
            {
                try
                {
                    var generated = await _embeddings.EmbedEntityAsync(mention.Name, cancellationToken).ConfigureAwait(false);
                    if (generated.Length > 0 && generated.All(float.IsFinite)) vector = generated;
                    else diagnostics.Add("embedding_unavailable");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    diagnostics.Add("embedding_unavailable");
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (_fuzzy || vector is not null || query.ContextSourceMessageIds.Count > 0)
            {
                var approximate = await _provider.FindApproximateCandidatesAsync(query with { Limit = remaining }, vector, cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                AddCandidates(approximate, remaining, candidates, effectiveScope);
                result = Evaluate();
            }
        }
        return MakePlan(mention, context, owner, result, candidates, diagnostics);
    }

    private IdentityResolutionPlan MakePlan(ExtractedEntity mention, EntityIdentityResolutionContext context,
        string? owner, IdentityPolicyResult result, IReadOnlyList<IdentityCandidate> candidates, IReadOnlyList<string> diagnostics)
    {
        var entity = result.Decision == IdentityDecisionKind.Confirmed
            ? candidates.First(candidate => candidate.Entity.EntityId == result.ConfirmedEntityId).Entity
            : new Entity
            {
                EntityId = _ids.GenerateId(), Name = mention.Name, CanonicalName = mention.Name,
                Type = mention.Type, Subtype = mention.Subtype, Description = mention.Description,
                OwnerId = owner, Confidence = mention.Confidence, Aliases = mention.Aliases,
                Attributes = mention.Attributes, SourceMessageIds = context.SourceMessageIds, CreatedAtUtc = _clock.GetUtcNow()
            };
        return new(entity, result.Decision != IdentityDecisionKind.Confirmed, result,
            candidates.ToArray(), context, diagnostics.ToArray());
    }

    private static void AddCandidates(IdentityCandidateSet set, int limit, List<IdentityCandidate> target, MemoryScope scope)
    {
        if (set is null || set.Candidates is null || set.Candidates.Count > limit)
            throw new InvalidOperationException("The identity candidate provider exceeded or violated its bounded result contract.");
        var routeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in set.Candidates)
        {
            if (candidate is null || candidate.Entity is null || string.IsNullOrWhiteSpace(candidate.Entity.EntityId))
                throw new InvalidOperationException("The identity candidate provider returned an invalid entity.");
            if (!routeIds.Add(candidate.Entity.EntityId))
                throw new InvalidOperationException("The identity provider returned duplicate entity IDs within one route.");
            var owner = candidate.Entity.OwnerId;
            var visible = SharedScopes.IsSharedOnly(scope) ? owner is null :
                string.Equals(owner, scope.OwnerId, StringComparison.Ordinal) || scope.IncludeShared && owner is null;
            if (!visible) continue;
            // An approximate hit may rediscover an exact candidate; it cannot replace that authority snapshot.
            if (target.Any(previous => previous.Entity.EntityId == candidate.Entity.EntityId)) continue;
            target.Add(candidate with
            {
                VerifiedKeys = Array.AsReadOnly(candidate.VerifiedKeys.Select(Freeze).ToArray()),
                VerifiedAliases = Array.AsReadOnly(candidate.VerifiedAliases.ToArray())
            });
        }
    }

    private static VerifiedIdentityKey Freeze(VerifiedIdentityKey key) => key with
    { Values = new ReadOnlyDictionary<string, string>(key.Values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)) };

    internal static EntityIdentityResolutionContext FreezeContext(EntityIdentityResolutionContext context) => context with
    {
        SourceMessageIds = Array.AsReadOnly(context.SourceMessageIds.ToArray()),
        VerifiedKeys = Array.AsReadOnly(context.VerifiedKeys.Select(Freeze).ToArray()),
        Sources = Array.AsReadOnly(context.Sources.Select(source => source with
        { OriginIds = Array.AsReadOnly(source.OriginIds.ToArray()) }).ToArray())
    };
}

internal sealed record IdentityResolutionPlan(Entity Entity, bool Created, IdentityPolicyResult Result,
    IReadOnlyList<IdentityCandidate> Candidates, EntityIdentityResolutionContext Context, IReadOnlyList<string> Diagnostics)
{
    public IdentityContextualResult? ContextualEvidence { get; init; }
}
