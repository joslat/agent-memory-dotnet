using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Memory;
using AgentMemory.Core.Resolution;
using AgentMemory.Core.Services;

namespace AgentMemory.Core.Extraction;

/// <summary>Opt-in planning and atomic persistence; original observations never become target upserts.</summary>
internal sealed partial class IdentityIngestion
{
    private readonly IdentityConfiguration _configuration;
    private readonly IdentityPlanner? _planner;
    private readonly IIdentityDecisionRepository? _repository;
    private readonly IIdentityContextProvider? _hostContext;
    private readonly IdentityContextualAdjudication? _contextual;
    private readonly TimeProvider _clock;
    public bool Enabled => _configuration.Enabled;
    public int MaxMentions => _configuration.MaxMentionsPerIngestion;

    public IdentityIngestion(IdentityConfiguration configuration, TimeProvider clock,
        IIdentityCandidateProvider? candidates = null, IIdentityDecisionRepository? repository = null,
        IEmbeddingOrchestrator? embeddings = null, IIdGenerator? ids = null, IIdentityContextProvider? hostContext = null,
        IIdentityAdjudicator? adjudicator = null, IIdentitySourceProvider? sources = null)
    {
        _configuration = configuration; _clock = clock;
        if (!Enabled) return;
        if (candidates is null || repository is not { IsEnabled: true, SupportsAtomicIngestion: true } || embeddings is null || ids is null)
            throw new InvalidOperationException("Evidence-based identity requires its enabled schema, bounded candidates and atomic ingestion storage.");
        _repository = repository;
        _hostContext = hostContext;
        if (adjudicator is not null && configuration.MaxAdjudicationsPerIngestion > 0)
            _contextual = new(adjudicator, configuration, clock, sources);
        _planner = new(candidates, embeddings, ids, clock, configuration);
    }

    public void AssertOwnsTransactionBoundary()
    {
        if (Enabled && _repository!.HasActiveWriteTransaction)
            throw new NotSupportedException("Evidence-based ingestion must start outside a caller-owned write transaction.");
    }

    internal IdentityAdjudicationBudget CreateAdjudicationBudget() => new(_configuration.MaxAdjudicationsPerIngestion);

    public async Task<IdentityDecisionWrite> PlanAsync(ExtractedEntity mention, ExtractionWindow window,
        MemoryScope? scope, CancellationToken cancellationToken, IdentityAdjudicationBudget? budget = null)
    {
        if (!Enabled) throw new InvalidOperationException("Evidence-based identity is disabled.");
        cancellationToken.ThrowIfCancellationRequested();
        AssertOwnsTransactionBoundary();
        var owner = SharedScopes.WriteOwner(scope);
        var roots = window.Targets.Select(message => message.MessageId).Distinct(StringComparer.Ordinal).ToArray();
        var fragments = new List<IdentitySourceFragment>();
        var usedBytes = 0;
        var omitted = false;
        foreach (var message in window.Targets.Concat(window.Context).DistinctBy(message => message.MessageId))
        {
            var bytes = Encoding.UTF8.GetByteCount(message.Content ?? "");
            if (fragments.Count >= 32 || bytes > _configuration.MaxContextUtf8Bytes - usedBytes)
            { omitted = true; continue; }
            fragments.Add(new() { SourceId = message.MessageId, Text = message.Content ?? "", OriginIds = [message.MessageId] });
            usedBytes += bytes;
        }
        var now = _clock.GetUtcNow();
        var original = new Entity
        {
            EntityId = "preflight", Name = mention.Name, CanonicalName = mention.Name, Type = mention.Type,
            Subtype = mention.Subtype, Description = mention.Description, Confidence = mention.Confidence,
            Attributes = mention.Attributes, Aliases = mention.Aliases, OwnerId = owner,
            SourceMessageIds = roots, CreatedAtUtc = now
        };
        // Validate and freeze JSON data before the first candidate/embedding await. Host getters cannot run.
        var initial = IdentityDecisionPayload.Capture(new()
        {
            Decision = new()
            {
                DecisionId = "preflight", ObservationKey = "preflight", Observation = original,
                Policy = _configuration.PolicyFor(mention.Type), Status = IdentityDecisionKind.New,
                Context = new() { SourceMessageIds = roots, Sources = fragments }, CreatedAtUtc = now, UpdatedAtUtc = now
            }, IncludeShared = scope?.IncludeShared ?? false
        }, owner);
        if (_hostContext is not null)
        {
            var evidence = await _hostContext.GetContextAsync(IdentityHostContext.FreezeMention(initial.Decision.Observation),
                IdentityPlanner.FreezeContext(initial.Decision.Context),
                owner is null ? SharedScopes.SharedOnly : MemoryScope.For(owner, scope?.IncludeShared ?? false),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            initial = IdentityHostContext.Apply(initial, evidence, owner);
        }
        original = initial.Decision.Observation;
        var key = ObservationKey(owner, roots, original);
        original = original with { EntityId = "identity-observation-" + key };
        mention = mention with { Name = original.Name, Type = original.Type, Subtype = original.Subtype,
            Description = original.Description, Attributes = original.Attributes, Aliases = original.Aliases };
        var plan = await _planner!.PlanAsync(mention, initial.Decision.Context, scope, cancellationToken).ConfigureAwait(false);
        // A writer's checked stored-entity reference first: it needs no call; whatever it refuses reaches the adjudicator.
        (mention, plan) = await ResolveReferenceAsync(mention, plan, owner, cancellationToken).ConfigureAwait(false);
        plan = IdentityWriterReference.Apply(mention, plan, _configuration.PolicyFor(mention.Type));
        if (_contextual is not null)
            plan = await _contextual.ApplyAsync(mention, plan, budget ?? CreateAdjudicationBudget(), cancellationToken,
                owner, scope?.IncludeShared ?? false).ConfigureAwait(false);
        return IdentityDecisionPayload.Capture(initial with
        {
            Decision = initial.Decision with
            {
                DecisionId = "identity-decision-" + key, ObservationKey = key, Observation = original,
                // The context actually offered: current-turn roots plus any hydrated candidate sources (ILC-09).
                Context = plan.Context,
                Status = plan.Result.Decision, ConfirmedEntityId = plan.Result.ConfirmedEntityId,
                PendingEntityIds = plan.Result.PendingEntityIds,
                ContextualEvidence = plan.ContextualEvidence is { } contextual ? new()
                {
                    Verdict = contextual.Verdict switch
                    {
                        IdentityContextualVerdict.Same => IdentityAdjudicationVerdict.Same,
                        IdentityContextualVerdict.Different => IdentityAdjudicationVerdict.Different,
                        _ => IdentityAdjudicationVerdict.Uncertain
                    },
                    CandidateId = contextual.CandidateId,
                    Citations = contextual.Evidence.Select(quote => new IdentityAdjudicationCitation
                        { SourceId = quote.SourceId, Quote = quote.Quote }).ToArray()
                } : null,
                Diagnostics = omitted ? [.. plan.Diagnostics, "identity_sources_omitted_budget"] : plan.Diagnostics
            }, ExpectedCandidates = plan.Candidates
        }, owner);
    }

    private const string ObservationPrefix = "identity-observation-";

    /// <summary>
    /// An observation's key: its owner, its source messages and its own words. One derivation for ingestion and for a
    /// restore into the same owner, which keeps an original key only when it derives it again here (REV3-8).
    /// </summary>
    internal static string ObservationKey(string? owner, IEnumerable<string> roots, Entity observation) =>
        Convert.ToHexString(SHA256.HashData(IdentityDecisionPayload.Serialize(new
        {
            owner, roots = roots.Order(StringComparer.Ordinal).ToArray(), observation.Name, observation.Type,
            observation.Subtype, observation.Description,
            attributes = observation.Attributes.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new { pair.Key, Value = CanonicalHashValue(pair.Value) }).ToArray()
        }))).ToLowerInvariant();
    private const string DecisionPrefix = "identity-decision-";

    /// <summary>
    /// Every observation is stored as an entity, so a writer can be shown one that a decision already confirmed into
    /// another identity; such an observation is no candidate itself. Its reference is resolved through that decision,
    /// read in the decision owner's scope: one bounded read, only for an unmatched reference on a decision still open.
    /// </summary>
    private async Task<(ExtractedEntity Mention, IdentityResolutionPlan Plan)> ResolveReferenceAsync(ExtractedEntity mention,
        IdentityResolutionPlan plan, string? owner, CancellationToken cancellationToken)
    {
        if (mention.Reference is not { } reference || plan.Result.Decision != IdentityDecisionKind.Pending ||
            !reference.EntityId.StartsWith(ObservationPrefix, StringComparison.Ordinal) ||
            plan.Candidates.Any(candidate => candidate.Entity.EntityId == reference.EntityId)) return (mention, plan);
        var decision = await _repository!.GetAsync(DecisionPrefix + reference.EntityId[ObservationPrefix.Length..], owner, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (decision is not { Status: IdentityDecisionKind.Confirmed, ConfirmedEntityId: { } target } ||
            !string.Equals(decision.Observation.EntityId, reference.EntityId, StringComparison.Ordinal)) return (mention, plan);
        return (mention with { Reference = reference with { EntityId = target } },
            plan with { Diagnostics = [.. plan.Diagnostics, "writer_reference_resolved"] });
    }

    // Input is already frozen/validated bounded JSON. Only object ordering is normalized for the key;
    // arrays, strings, numbers and the stored original observation retain their exact data.
    private static object? CanonicalHashValue(object? value)
    {
        if (value is not JsonElement element) return value;
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal)
                .ToDictionary(property => property.Name, property => CanonicalHashValue(property.Value), StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray().Select(item => CanonicalHashValue(item)).ToArray(),
            _ => element.Clone()
        };
    }

    internal static IdentityDecisionWrite WithOccurrence(IdentityDecisionWrite plan, int occurrence)
    {
        if (occurrence < 1) throw new ArgumentOutOfRangeException(nameof(occurrence));
        var key = plan.Decision.ObservationKey + ":" + occurrence.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return plan with { Decision = plan.Decision with
        {
            ObservationKey = key, DecisionId = "identity-decision-" + key,
            Observation = plan.Decision.Observation with { EntityId = "identity-observation-" + key },
            Diagnostics = [.. plan.Decision.Diagnostics, "identity_ambiguous_repeated_mention"]
        } };
    }

    public Task<T> ExecuteAsync<T>(string? ownerId, bool includeShared, IReadOnlyList<string> sourceMessageIds,
        Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken) =>
        _repository!.ExecuteScopedAsync(ownerId, includeShared, sourceMessageIds, work, cancellationToken);

    public async Task<EntityPersistenceResult> PersistObservationsAsync(IReadOnlyList<IdentityDecisionWrite> plans,
        IReadOnlyDictionary<string, Entity> prepared, string? ownerId, MemoryTrustLevel trust,
        List<IngestionItemOutcome> outcomes, CancellationToken cancellationToken)
    {
        var saved = new List<(IdentityDecision Decision, Entity Entity)>();
        foreach (var plan in plans.DistinctBy(plan => plan.Decision.ObservationKey))
        {
            var observation = prepared.GetValueOrDefault(plan.Decision.Observation.EntityId) ?? plan.Decision.Observation;
            observation = observation with { Metadata = observation.Metadata.WithTrustLevel(
                PersistenceMetadata.MaxTrustLevel(observation.Metadata.GetTrustLevel(), trust)) };
            var decision = await _repository!.SaveAsync(plan with
            { Decision = plan.Decision with { Observation = observation } }, ownerId, cancellationToken).ConfigureAwait(false);
            var entity = decision.Observation with { EntityId = decision.ConfirmedEntityId ?? decision.Observation.EntityId };
            saved.Add((decision, entity));
            PersistenceOutcomes.RecordSuccess(outcomes, MemoryItemKind.Entity, decision.Observation.Name, entity.EntityId,
                decision.Status == IdentityDecisionKind.Confirmed ? MemoryWriteEffect.AlreadyStored : MemoryWriteEffect.Unreported);
        }
        // A bare surface cannot disambiguate homonyms, even if a dictionary could choose one by order.
        var map = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in saved.GroupBy(item => MemoryTripleCanonicalizer.CanonicalValue(item.Decision.Observation.Name), StringComparer.Ordinal))
            if (group.Count() == 1) map.Add(group.Single().Decision.Observation.Name, group.Single().Entity);
        return new() { ByName = map, Count = saved.Select(item => item.Entity.EntityId).Distinct(StringComparer.Ordinal).Count() };
    }

    public async Task BindFactsAsync(IReadOnlyList<IdentityDecisionWrite> plans, FactPersistenceResult persisted,
        string? ownerId, CancellationToken cancellationToken)
    {
        foreach (var group in plans.DistinctBy(plan => plan.Decision.ObservationKey)
            .GroupBy(plan => MemoryTripleCanonicalizer.CanonicalValue(plan.Decision.Observation.Name), StringComparer.Ordinal))
        {
            if (group.Count() != 1) continue;
            var plan = group.Single(); var bindings = new List<IdentityFactBinding>();
            foreach (var fact in persisted.WrittenFacts.DistinctBy(fact => fact.FactId))
            {
                if (!fact.SourceMessageIds.Intersect(plan.Decision.Context.SourceMessageIds, StringComparer.Ordinal).Any()) continue;
                if (MemoryTripleCanonicalizer.CanonicalValue(fact.Subject) == group.Key)
                    bindings.Add(new() { FactId = fact.FactId, Role = IdentityFactRole.Subject });
                if (MemoryTripleCanonicalizer.CanonicalValue(fact.Object) == group.Key)
                    bindings.Add(new() { FactId = fact.FactId, Role = IdentityFactRole.Object });
            }
            if (bindings.Count > 0)
                await _repository!.SaveAsync(plan with { Bindings = bindings }, ownerId, cancellationToken).ConfigureAwait(false);
        }
    }
}
