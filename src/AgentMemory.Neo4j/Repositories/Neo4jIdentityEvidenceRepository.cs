using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Resolution;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using AgentMemory.Neo4j.Retrieval.Internal;
using Microsoft.Extensions.Options;
using Neo4j.Driver;

namespace AgentMemory.Neo4j.Repositories;

/// <summary>Explicit host assertions and bounded reads; constructors never contact the database.</summary>
internal sealed class Neo4jIdentityEvidenceRepository : IIdentityEvidenceRepository, IIdentityCandidateProvider, IIdentitySourceProvider
{
    /// <summary>The most source messages one identity source read may request.</summary>
    internal const int MaxSourceReads = 32;

    internal const int MaxEvidence = 64;
    private readonly INeo4jTransactionRunner _tx;
    private readonly IOptions<Neo4jOptions> _options;

    public Neo4jIdentityEvidenceRepository(INeo4jTransactionRunner tx, IOptions<Neo4jOptions> options)
    { _tx = tx; _options = options; }

    public bool IsEnabled => _options.Value.Extensions.Contains("identity-evidence");
    public IdentityCandidateCapabilities Capabilities => new()
    { BoundedReads = IsEnabled, VerifiedKeyLookup = IsEnabled, CalendarValueLookup = IsEnabled, ContextRootedLookup = IsEnabled };

    public Task AssertKeyAsync(string entityId, VerifiedIdentityKey key, IReadOnlyList<string> identityProperties,
        string? ownerId = null, CancellationToken cancellationToken = default)
    {
        CheckEnabled(cancellationToken);
        ArgumentNullException.ThrowIfNull(key);
        var payload = KeyPayload(key, identityProperties);
        return AssertAsync(entityId, "key", key.Namespace, key.Authority, key.SourceId,
            new() { ["payload"] = payload, ["lookup_key"] = KeyLookup(key.Namespace, payload) }, ownerId, cancellationToken);
    }

    public Task AssertAliasAsync(string entityId, VerifiedIdentityAlias alias, string authority,
        string? ownerId = null, CancellationToken cancellationToken = default)
    {
        CheckEnabled(cancellationToken);
        ArgumentNullException.ThrowIfNull(alias); Text(alias.Surface, nameof(alias.Surface));
        return AssertAsync(entityId, "alias", alias.Namespace, authority, alias.SourceId,
            new() { ["surface"] = alias.Surface }, ownerId, cancellationToken);
    }

    public Task AssertKindAsync(string entityId, VerifiedIdentityKind kind,
        string? ownerId = null, CancellationToken cancellationToken = default)
    {
        CheckEnabled(cancellationToken);
        ArgumentNullException.ThrowIfNull(kind);
        if (kind.Kind is not (IdentitySubjectKind.Reference or IdentitySubjectKind.CalendarDate or IdentitySubjectKind.CalendarInterval))
            throw new ArgumentException("A verified kind must be reference, calendar date or calendar interval.", nameof(kind));
        return AssertAsync(entityId, "kind", "semantic-kind", kind.Authority, kind.SourceId,
            new() { ["subject_kind"] = kind.Kind.ToString() }, ownerId, cancellationToken);
    }

    public Task AssertNameNamespaceAsync(string entityId, string nameNamespace, string authority, string sourceId,
        string? ownerId = null, CancellationToken cancellationToken = default)
    {
        CheckEnabled(cancellationToken);
        return AssertAsync(entityId, "namespace", nameNamespace, authority, sourceId, new(), ownerId, cancellationToken);
    }

    private async Task AssertAsync(string entityId, string kind, string ns, string authority, string source,
        Dictionary<string, object?> details, string? ownerId, CancellationToken cancellationToken)
    {
        Text(entityId, nameof(entityId)); Text(ns, nameof(ns)); Text(authority, nameof(authority)); Text(source, nameof(source));
        if (ownerId is not null) Text(ownerId, nameof(ownerId));
        var reuseScope = IdentityWriteScope.Reuse(_tx, [Scope(ownerId)]);
        await _tx.WriteAsync(async runner =>
        {
            if (!reuseScope)
                await runner.RunAsync(IdentityDecisionQueries.LockScope, new { ownerScope = Scope(ownerId) }).ConfigureAwait(false);
            await AssertOnRunner(runner, entityId, kind, ns, authority, source, details, ownerId, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    // The caller owns the exact scope locks and outer transaction; no nested managed write starts here.
    private static async Task AssertOnRunner(IAsyncQueryRunner runner, string entityId, string kind, string ns,
        string authority, string source, Dictionary<string, object?> details, string? ownerId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Text(entityId, nameof(entityId)); Text(ns, nameof(ns)); Text(authority, nameof(authority)); Text(source, nameof(source));
        if (ownerId is not null) Text(ownerId, nameof(ownerId));
        var cursor = await runner.RunAsync(IdentityEvidenceQueries.LockEntity, new { entityId, ownerId }).ConfigureAwait(false);
        var rows = await cursor.ToListAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (rows.Count != 1) throw new InvalidOperationException("The entity does not exist in the exact requested write scope.");
        var entity = rows[0]["entity"].As<IReadOnlyDictionary<string, object>>();
        var kinds = rows[0]["kinds"].As<List<Dictionary<string, object>>>();
        if (kind == "namespace") details["surface"] = entity["name"].As<string>();
        string? calendarKey = null;
        if (kind == "kind")
        {
            var value = details["subject_kind"]!.ToString()!;
            if (kinds.Any(existing => existing["subject_kind"].As<string>() != value))
                throw new InvalidOperationException("An active verified semantic kind contradicts this assertion.");
            if (value != nameof(IdentitySubjectKind.Reference))
            {
                var type = value == nameof(IdentitySubjectKind.CalendarDate) ? "DATE" : "DATE_RANGE";
                var name = entity["name"].As<string>();
                if (!CalendarValues.TryRead(name, type, out var parsed) || parsed is null ||
                    value == nameof(IdentitySubjectKind.CalendarDate) && parsed.End is not null ||
                    value == nameof(IdentitySubjectKind.CalendarInterval) && parsed.End is null ||
                    !CalendarValues.HasValue(new Entity { EntityId = entityId, Name = name, Type = type, Confidence = 1,
                        CanonicalName = entity.GetValueOrDefault("canonical_name")?.As<string>(), CreatedAtUtc = DateTimeOffset.UnixEpoch }, parsed))
                    throw new InvalidOperationException("The entity's primary and canonical names do not support that calendar kind.");
                calendarKey = parsed.CanonicalKey;
                if (kinds.Any(existing => existing.GetValueOrDefault("calendar_key") is string previous && previous != calendarKey))
                    throw new InvalidOperationException("An active verified calendar value contradicts this assertion.");
                details["calendar_key"] = calendarKey;
            }
            await runner.RunAsync(IdentityEvidenceQueries.SetCalendarKey, new { entityId, calendarKey }).ConfigureAwait(false);
        }
        var properties = new SortedDictionary<string, object?>(details, StringComparer.Ordinal)
        { ["assertion_kind"] = kind, ["namespace"] = ns, ["authority"] = authority,
          ["source_id"] = source, ["owner_scope"] = Scope(ownerId) };
        var assertionId = Hash(JsonSerializer.Serialize(new { entityId, properties }));
        await runner.RunAsync(IdentityEvidenceQueries.Assert, new { entityId, assertionId, properties, ownerId }).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Initial original-observation assertions, on the same runner as its decision and facts.</summary>
    internal static async Task AssertObservationContextAsync(IAsyncQueryRunner runner, IdentityDecision decision,
        string? ownerId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = decision.Context; var entityId = decision.Observation.EntityId;
        foreach (var key in IdentityPolicy.CompleteKeys(context.VerifiedKeys, decision.Policy))
        {
            var payload = KeyPayload(key, decision.Policy.IdentityProperties);
            await AssertOnRunner(runner, entityId, "key", key.Namespace, key.Authority, key.SourceId,
                new() { ["payload"] = payload, ["lookup_key"] = KeyLookup(key.Namespace, payload) }, ownerId, cancellationToken).ConfigureAwait(false);
        }
        if (context.VerifiedKind is { } kind)
        {
            Text(kind.Authority, nameof(kind.Authority)); Text(kind.SourceId, nameof(kind.SourceId));
            if (kind.Kind is not (IdentitySubjectKind.Reference or IdentitySubjectKind.CalendarDate or IdentitySubjectKind.CalendarInterval))
                throw new ArgumentException("A host semantic-kind assertion needs a concrete supported kind.");
            var type = kind.Kind == IdentitySubjectKind.CalendarDate ? "DATE" : "DATE_RANGE";
            // A known calendar role does not supply a missing year/value. Keep its receipt, without an active calendar index.
            if (kind.Kind == IdentitySubjectKind.Reference ||
                CalendarValues.TryRead(decision.Observation.Name, type, out var value) && value?.IsComplete == true)
                await AssertOnRunner(runner, entityId, "kind", "semantic-kind", kind.Authority, kind.SourceId,
                    new() { ["subject_kind"] = kind.Kind.ToString() }, ownerId, cancellationToken).ConfigureAwait(false);
        }
        if (context.NameNamespaceEvidence is { } ns)
            await AssertOnRunner(runner, entityId, "namespace", ns.Namespace, ns.Authority, ns.SourceId,
                new(), ownerId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IdentitySourceFragment>> GetSourcesAsync(IReadOnlyList<string> messageIds, string? ownerId,
        bool includeShared, int limit, CancellationToken cancellationToken = default)
    {
        CheckEnabled(cancellationToken);
        ArgumentNullException.ThrowIfNull(messageIds);
        if (limit is < 1 or > MaxSourceReads || messageIds.Count is 0 or > MaxSourceReads ||
            messageIds.Any(string.IsNullOrWhiteSpace) || ownerId is { Length: 0 })
            throw new ArgumentException("An identity source read needs 1 to 32 message IDs, a limit in that range and a null or non-empty owner.");
        var ids = messageIds.Distinct(StringComparer.Ordinal).ToArray();
        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(IdentityEvidenceQueries.CandidateSources,
                new { ids, ownerId, includeShared, limit = Math.Min(limit, ids.Length) }).ConfigureAwait(false);
            var rows = await cursor.ToListAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return (IReadOnlyList<IdentitySourceFragment>)[.. rows.Select(row => row["id"].As<string>()).Zip(rows.Select(row => row["content"].As<string>()))
                .Select(pair => new IdentitySourceFragment { SourceId = pair.First, Text = pair.Second, OriginIds = [pair.First] })];
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IdentityCandidateSet> FindExactCandidatesAsync(IdentityCandidateQuery query, CancellationToken cancellationToken = default)
    {
        Validate(query, cancellationToken);
        return await _tx.ReadAsync(runner => FindExact(runner, query, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<IdentityCandidateSet> FindExact(IAsyncQueryRunner runner, IdentityCandidateQuery query, CancellationToken cancellationToken)
    {
        var parameters = Parameters(query);
        parameters["keyLookups"] = query.VerifiedKeys.Select(key => KeyLookup(key.Namespace, KeyPayload(key, query.IdentityProperties))).ToArray();
        var cursor = await runner.RunAsync(IdentityEvidenceQueries.ExactIds, parameters).ConfigureAwait(false);
        var rows = await cursor.ToListAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var ids = rows.Take(query.Limit).Select(row => row["id"].As<string>()).ToArray();
        var candidates = await Hydrate(runner, query, ids, new Dictionary<string, double>(), cancellationToken).ConfigureAwait(false);
        return new IdentityCandidateSet { Candidates = candidates, ExactIdentityLookupComplete = rows.Count <= query.Limit };
    }

    public async Task<IdentityCandidateSet> FindApproximateCandidatesAsync(IdentityCandidateQuery query, float[]? embedding,
        CancellationToken cancellationToken = default)
    {
        Validate(query, cancellationToken);
        if (embedding is not null && (embedding.Length == 0 || embedding.Length > 65536 ||
            embedding.Any(value => !float.IsFinite(value)) || !embedding.Any(value => value != 0)))
            throw new ArgumentException("The embedding must be a finite, nonzero vector.", nameof(embedding));
        if (!query.UseFuzzyCandidates && embedding is null && query.ContextSourceMessageIds.Count == 0) return new();
        var parameters = Parameters(query);
        parameters["lucene"] = LuceneQueryEscaper.Escape(query.Name);
        parameters["poolLimit"] = Math.Min(1024, query.Limit * 8);
        parameters["embedding"] = embedding;
        parameters["contextIds"] = query.ContextSourceMessageIds.Take(32).ToArray();
        return await _tx.ReadAsync(async runner =>
        {
            var rank = new Dictionary<string, double>(StringComparer.Ordinal);
            var cosine = new Dictionary<string, double>(StringComparer.Ordinal);
            var rooted = new HashSet<string>(StringComparer.Ordinal);
            if (query.ContextSourceMessageIds.Count > 0)
            {
                var cursor = await runner.RunAsync(IdentityEvidenceQueries.ContextRootedIds, parameters).ConfigureAwait(false);
                var rows = await cursor.ToListAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                for (var i = 0; i < rows.Count; i++)
                {
                    var id = rows[i]["id"].As<string>();
                    rooted.Add(id);
                    rank[id] = rank.GetValueOrDefault(id) + 1.0 / (60 + i + 1);
                }
            }
            foreach (var route in new[] { (query.UseFuzzyCandidates, IdentityEvidenceQueries.LexicalIds, false),
                (embedding is not null, IdentityEvidenceQueries.VectorIds, true) })
            {
                if (!route.Item1) continue;
                var cursor = await runner.RunAsync(route.Item2, parameters).ConfigureAwait(false);
                var rows = await cursor.ToListAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                for (var i = 0; i < rows.Count; i++)
                {
                    var id = rows[i]["id"].As<string>();
                    rank[id] = rank.GetValueOrDefault(id) + 1.0 / (60 + i + 1);
                    if (route.Item3) cosine[id] = Math.Clamp(2 * rows[i]["score"].As<double>() - 1, -1, 1);
                }
            }
            var ids = rank.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Take(query.Limit).Select(pair => pair.Key).ToArray();
            var candidates = await Hydrate(runner, query, ids, cosine, cancellationToken).ConfigureAwait(false);
            return new IdentityCandidateSet { Candidates = [.. candidates.Select(candidate => rooted.Contains(candidate.Entity.EntityId)
                ? candidate with { Signals = candidate.Signals with { ContextRooted = true } } : candidate)] };
        }, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<IReadOnlyList<IdentityCandidate>> Hydrate(IAsyncQueryRunner runner, IdentityCandidateQuery query,
        string[] ids, IReadOnlyDictionary<string, double> cosine, CancellationToken cancellationToken)
    {
        if (ids.Length == 0) return [];
        var parameters = Parameters(query); parameters["ids"] = ids; parameters["evidenceLimit"] = MaxEvidence + 1;
        var cursor = await runner.RunAsync(IdentityEvidenceQueries.Hydrate, parameters).ConfigureAwait(false);
        var rows = await cursor.ToListAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return rows.Select(row =>
        {
            var entity = Neo4jEntityRepository.MapToEntity(row["entity"].As<IReadOnlyDictionary<string, object>>(), null);
            var evidence = row["evidence"].As<List<Dictionary<string, object>>>();
            var bounded = evidence.Take(MaxEvidence).ToArray();
            var kinds = bounded.Where(item => item["assertion_kind"].As<string>() == "kind").ToArray();
            var namespaces = bounded.Where(item => item["assertion_kind"].As<string>() == "namespace" &&
                item["surface"].As<string>() == entity.Name).Select(item => item["namespace"].As<string>()).Distinct(StringComparer.Ordinal).ToArray();
            return new IdentityCandidate
            {
                Entity = entity, EvidenceComplete = evidence.Count <= MaxEvidence && kinds.Select(item => item["subject_kind"].As<string>()).Distinct().Count() <= 1,
                EvidenceVersion = row["version"].As<long>(),
                NameNamespace = namespaces.Contains(query.NameNamespace, StringComparer.Ordinal) ? query.NameNamespace : null,
                VerifiedKind = kinds.Length == 0 ? null : new VerifiedIdentityKind
                { Kind = Enum.Parse<IdentitySubjectKind>(kinds[0]["subject_kind"].As<string>()), Authority = kinds[0]["authority"].As<string>(), SourceId = kinds[0]["source_id"].As<string>() },
                VerifiedKeys = bounded.Where(item => item["assertion_kind"].As<string>() == "key").Select(item => new VerifiedIdentityKey
                { Namespace = item["namespace"].As<string>(), Authority = item["authority"].As<string>(), SourceId = item["source_id"].As<string>(),
                  Values = JsonSerializer.Deserialize<Dictionary<string, string>>(item["payload"].As<string>()) ?? throw new InvalidOperationException("Invalid stored identity key.") }).ToArray(),
                VerifiedAliases = bounded.Where(item => item["assertion_kind"].As<string>() == "alias").Select(item => new VerifiedIdentityAlias
                { Namespace = item["namespace"].As<string>(), SourceId = item["source_id"].As<string>(), Surface = item["surface"].As<string>() }).ToArray(),
                Signals = new() { FuzzySimilarity = query.UseFuzzyCandidates ? FuzzyMatchEntityMatcher.BestScoreAgainst(query.Name, entity) / 100.0 : null,
                    SemanticSimilarity = cosine.TryGetValue(entity.EntityId, out var score) ? score : null }
            };
        }).ToArray();
    }

    private void Validate(IdentityCandidateQuery query, CancellationToken cancellationToken)
    {
        CheckEnabled(cancellationToken); ArgumentNullException.ThrowIfNull(query);
        Text(query.Name, nameof(query.Name)); Text(query.Type, nameof(query.Type));
        if (query.OwnerId is not null) Text(query.OwnerId, nameof(query.OwnerId));
        if (query.Limit is < 1 or > 128 || query.CompatibleTypes.Count > 8 || query.VerifiedKeys.Count > 32)
            throw new ArgumentException("The query exceeds the bounded identity lookup contract.", nameof(query));
        foreach (var type in query.CompatibleTypes) Text(type, nameof(query.CompatibleTypes));
        if (query.IdentityProperties.Count > 16) throw new ArgumentException("The backend supports at most sixteen key components.", nameof(query));
        foreach (var key in query.VerifiedKeys)
        {
            ArgumentNullException.ThrowIfNull(key);
            _ = KeyPayload(key, query.IdentityProperties);
        }
        if (query.IdentityNameComparison != StringComparison.Ordinal)
            throw new NotSupportedException("This backend guarantees exact identity names using Ordinal comparison only.");
        if (query.NameNamespace is not null) Text(query.NameNamespace, nameof(query.NameNamespace));
        if (query.CalendarKey is not null) Text(query.CalendarKey, nameof(query.CalendarKey));
    }

    private void CheckEnabled(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsEnabled) throw new InvalidOperationException("Enable and migrate the identity-evidence schema extension before using identity evidence.");
    }

    private static Dictionary<string, object?> Parameters(IdentityCandidateQuery query) => new()
    {
        ["name"] = query.Name, ["types"] = query.CompatibleTypes.Prepend(query.Type).Select(type => type.ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToArray(),
        ["ownerId"] = query.OwnerId, ["includeShared"] = query.OwnerId is not null && query.IncludeShared,
        ["evidenceScopes"] = query.OwnerId is null ? new[] { Scope(null) } : query.IncludeShared ? new[] { Scope(query.OwnerId), Scope(null) } : new[] { Scope(query.OwnerId) },
        ["limit"] = query.Limit, ["sentinelLimit"] = query.Limit + 1, ["namespace"] = query.NameNamespace, ["calendarKey"] = query.CalendarKey
    };

    private static string KeyPayload(VerifiedIdentityKey key, IReadOnlyList<string> properties)
    {
        Text(key.Namespace, nameof(key.Namespace)); Text(key.Authority, nameof(key.Authority)); Text(key.SourceId, nameof(key.SourceId));
        ArgumentNullException.ThrowIfNull(properties);
        if (properties.Count is < 1 or > 16 || properties.Distinct(StringComparer.Ordinal).Count() != properties.Count)
            throw new ArgumentException("A composite key needs 1–16 distinct ordinal component names.", nameof(properties));
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in properties)
        {
            Text(name, nameof(properties));
            var pair = key.Values.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.Ordinal));
            Text(pair.Value, nameof(key.Values)); values[name] = pair.Value;
        }
        return JsonSerializer.Serialize(values);
    }

    private static string KeyLookup(string ns, string payload) => Hash(JsonSerializer.Serialize(new[] { ns, payload }));
    private static string Scope(string? owner) => owner is null ? "shared" : "owned:" + owner;
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void Text(string? value, string name)
    { if (string.IsNullOrWhiteSpace(value) || value.Length > 4096) throw new ArgumentException("Identity text must contain 1–4096 characters.", name); }
}
