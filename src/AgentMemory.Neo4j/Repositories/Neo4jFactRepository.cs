using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Diagnostics;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Core.Memory;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using Neo4j.Driver;
using static AgentMemory.Neo4j.Repositories.Neo4jRecordMapper;

namespace AgentMemory.Neo4j.Repositories;

internal sealed partial class Neo4jFactRepository : IFactRepository, IUpsertPersistsProvenance,
    IBatchMemoryRepository<Fact>, IFusedBatchMemoryRepository<Fact>
{

    // Non-null sentinel for the shared/global owner, used only as the MERGE-pattern owner_key so that
    // a shared fact (owner_id null) stays distinct from owned facts with the same S/P/O triple.
    internal const string OwnerKeyShared = "*";

    private readonly INeo4jTransactionRunner _tx;
    private readonly ILogger<Neo4jFactRepository> _logger;
    private readonly MemoryRankingOptions _ranking;
    private readonly MemoryDecayOptions _decay;
    private readonly IMemoryRankingContext? _rankingContext;

    public Neo4jFactRepository(
        INeo4jTransactionRunner tx,
        ILogger<Neo4jFactRepository> logger,
        IOptions<MemoryRankingOptions>? ranking = null,
        IOptions<MemoryDecayOptions>? decay = null,
        IMemoryRankingContext? rankingContext = null)
    {
        _tx = tx;
        _logger = logger;
        _ranking = ranking?.Value ?? MemoryRankingOptions.Default;
        _decay = decay?.Value ?? MemoryDecayOptions.Default;
        _rankingContext = rankingContext;
    }

    public async Task<Fact> UpsertAsync(Fact fact, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Upserting fact {Id}", fact.FactId);

        var subjectKey = MemoryTripleCanonicalizer.CanonicalValue(fact.Subject);
        var predicateKey = MemoryTripleCanonicalizer.Canonical(fact.Predicate);
        var objectKey = MemoryTripleCanonicalizer.CanonicalValue(fact.Object);
        // L11. The fact merge key is the composite {subject_key, predicate_key, object_key,
        // owner_key}, and it is now backed by a range index — so an oversized value stops being a
        // slow scan and becomes a driver failure from inside the write. Reject it here, naming the
        // property and the fact, rather than surfacing an opaque message from mid-batch.
        IndexKeyBudget.EnsureCompositeIndexable(
            [
                ("subject_key", subjectKey),
                ("predicate_key", predicateKey),
                ("object_key", objectKey),
                ("owner_key", fact.OwnerId ?? OwnerKeyShared)
            ],
            fact.FactId);

        return await _tx.WriteAsync(async runner =>
        {
            var parameters = new Dictionary<string, object?>
            {
                ["id"] = fact.FactId,
                ["subject"] = fact.Subject,
                ["predicate"] = fact.Predicate,
                // Identity is the canonical trio; the raw strings above stay for display and audit.
                ["subjectKey"] = subjectKey,
                ["predicateKey"] = predicateKey,
                ["objectKey"] = objectKey,
                ["object"] = fact.Object,
                ["ownerId"] = fact.OwnerId,
                ["ownerKey"] = fact.OwnerId ?? OwnerKeyShared,
                ["category"] = fact.Category,
                ["confidence"] = fact.Confidence,
                ["validFrom"] = (object?)(fact.ValidFrom?.ToString("O")),
                ["validUntil"] = (object?)(fact.ValidUntil?.ToString("O")),
                ["sourceMessageIds"] = fact.SourceMessageIds.ToList(),
                ["createdAtUtc"] = fact.CreatedAtUtc.ToString("O"),
                ["updatedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["metadata"] = SerializeMetadata(fact.Metadata)
            };

            var cursor = await runner.RunAsync(FactQueries.Upsert, parameters).ConfigureAwait(false);
            var record = await cursor.SingleAsync().ConfigureAwait(false);
            var node = record["f"].As<INode>();

            // The MERGE is on the {subject,predicate,object,owner_key} triple and ON MATCH deliberately never
            // rewrites f.id, so a re-extracted triple keeps its ORIGINAL node id while fact.FactId is a now-
            // orphaned guid. The follow-up by-id sub-writes MUST target the surviving node, not the discarded
            // caller id — otherwise on re-extraction they MATCH nothing and the embedding/provenance is lost.
            var mergedId = node["id"].As<string>();

            // Only persist a real (non-empty) vector so a degraded empty embedding leaves `embedding`
            // NULL and re-queueable for the back-fill (mirrors the entity/preference repos).
            if (fact.Embedding is { Length: > 0 })
            {
                await runner.RunAsync(
                    SharedFragments.SetFactEmbedding,
                    new { id = mergedId, embedding = fact.Embedding.ToList() }).ConfigureAwait(false);
            }

            // Auto-create EXTRACTED_FROM relationships for all source messages
            if (fact.SourceMessageIds.Count > 0)
            {
                await runner.RunAsync(
                    SharedFragments.LinkFactExtractedFrom,
                    new { id = mergedId, sourceMessageIds = fact.SourceMessageIds.ToList() }).ConfigureAwait(false);
            }

            return MapToFact(node, fact.Embedding);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Fact>> UpsertBatchAsync(IReadOnlyList<Fact> facts, CancellationToken cancellationToken = default)
    {
        if (facts.Count == 0) return Array.Empty<Fact>();

        _logger.LogDebug("Batch upserting {Count} facts", facts.Count);

        // The query MERGEs on the {subject,predicate,object,owner_key} triple (parity with the single
        // Upsert path). Collapse same-triple inputs up front (last-writer-wins) so each surviving node is
        // fed by exactly one input: otherwise two inputs sharing a triple MERGE onto one node, leaving the
        // loser id naming nothing and silently dropping its embedding/EXTRACTED_FROM. After dedup the
        // returned list is 1:1 with distinct triples and provenance is deterministic (R5 #10).
        var deduped = facts
            .GroupBy(f => TripleKey(f.Subject, f.Predicate, f.Object, f.OwnerId ?? OwnerKeyShared))
            .Select(g => g.Last())
            .ToList();

        var updatedAt = DateTimeOffset.UtcNow.ToString("O");

        // L11. The fact merge key is the composite {subject_key, predicate_key, object_key,
        // owner_key}, and it is now backed by a range index — so an oversized value stops being a
        // slow scan and becomes a driver failure from inside the write. Reject it here, naming the
        // property and the fact, rather than surfacing an opaque message from mid-batch.
        foreach (var f in deduped)
        {
            IndexKeyBudget.EnsureCompositeIndexable(
                [
                    ("subject_key", MemoryTripleCanonicalizer.CanonicalValue(f.Subject)),
                    ("predicate_key", MemoryTripleCanonicalizer.Canonical(f.Predicate)),
                    ("object_key", MemoryTripleCanonicalizer.CanonicalValue(f.Object)),
                    ("owner_key", f.OwnerId ?? OwnerKeyShared)
                ],
                f.FactId);
        }

        var items = deduped.Select(f => new Dictionary<string, object?>
        {
            ["id"] = f.FactId,
            ["subject"] = f.Subject,
            ["predicate"] = f.Predicate,
            ["subject_key"] = MemoryTripleCanonicalizer.CanonicalValue(f.Subject),
            ["predicate_key"] = MemoryTripleCanonicalizer.Canonical(f.Predicate),
            ["object_key"] = MemoryTripleCanonicalizer.CanonicalValue(f.Object),
            ["object"] = f.Object,
            ["owner_id"] = f.OwnerId,
            ["owner_key"] = f.OwnerId ?? OwnerKeyShared,
            ["category"] = f.Category,
            ["confidence"] = f.Confidence,
            ["valid_from"] = (object?)(f.ValidFrom?.ToString("O")),
            ["valid_until"] = (object?)(f.ValidUntil?.ToString("O")),
            ["source_message_ids"] = f.SourceMessageIds.ToList(),
            ["created_at"] = f.CreatedAtUtc.ToString("O"),
            ["updated_at"] = updatedAt,
            ["metadata"] = SerializeMetadata(f.Metadata)
        }).ToList();

        return await _tx.WriteAsync(async runner =>
        {
            var cursor = await runner.RunAsync(FactQueries.UpsertBatch, new { items }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);

            // The MERGE returns the SURVIVING node per triple; for a pre-existing triple that id is the
            // ORIGINAL node id, not the caller's fresh FactId. Resolve the embedding/provenance sub-writes
            // by triple so they land on the real node — mirroring the single-path mergedId fix (R5-A);
            // keying them on fact.FactId would silently no-op whenever the triple already existed.
            var nodeIdByTriple = new Dictionary<(string, string, string, string), string>();
            foreach (var r in records)
            {
                var n = r["f"].As<INode>();
                nodeIdByTriple[TripleKey(n["subject"].As<string>(), n["predicate"].As<string>(),
                                         n["object"].As<string>(), n["owner_key"].As<string>())] = n["id"].As<string>();
            }

            string? NodeIdFor(Fact f) =>
                nodeIdByTriple.TryGetValue(
                    TripleKey(f.Subject, f.Predicate, f.Object, f.OwnerId ?? OwnerKeyShared), out var nodeId)
                    ? nodeId : null;

            // Set embeddings individually — only for nodes with a real (non-empty) vector.
            foreach (var fact in deduped.Where(f => f.Embedding is { Length: > 0 }))
            {
                if (NodeIdFor(fact) is not { } nodeId) continue;
                await runner.RunAsync(
                    SharedFragments.SetFactEmbedding,
                    new { id = nodeId, embedding = fact.Embedding!.ToList() }).ConfigureAwait(false);
            }

            // Auto-create EXTRACTED_FROM relationships on the surviving node.
            foreach (var fact in deduped.Where(f => f.SourceMessageIds.Count > 0))
            {
                if (NodeIdFor(fact) is not { } nodeId) continue;
                await runner.RunAsync(
                    SharedFragments.LinkFactExtractedFrom,
                    new { id = nodeId, sourceMessageIds = fact.SourceMessageIds.ToList() }).ConfigureAwait(false);
            }

            var embeddingByTriple = deduped.ToDictionary(
                f => TripleKey(f.Subject, f.Predicate, f.Object, f.OwnerId ?? OwnerKeyShared),
                f => f.Embedding);
            return records.Select(r =>
            {
                var node = r["f"].As<INode>();
                var key = TripleKey(node["subject"].As<string>(), node["predicate"].As<string>(),
                                     node["object"].As<string>(), node["owner_key"].As<string>());
                return MapToFact(node, embeddingByTriple.TryGetValue(key, out var emb) ? emb : null);
            }).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Fact?> GetByIdAsync(string factId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting fact {Id}", factId);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(FactQueries.GetById, new { id = factId }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            if (records.Count == 0) return null;
            var node = records[0]["f"].As<INode>();
            return MapToFact(node, ReadEmbedding(node));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Fact>> GetBySubjectAsync(
        string subject, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        _logger.LogDebug("Getting facts by subject '{Subject}', owner={Owner}", subject, scope?.OwnerId);

        var cypher = FactQueries.GetBySubject(hasOwner, includeShared);
        var parameters = new Dictionary<string, object?> { ["subject"] = subject };
        if (hasOwner) parameters["ownerId"] = scope!.OwnerId;

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, parameters).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Select(r =>
            {
                var node = r["f"].As<INode>();
                return MapToFact(node, ReadEmbedding(node));
            }).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(Fact Fact, double Score)>> SearchByVectorAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        // Boundary invariant: a zero-dimension (empty/degraded) query embedding has no semantic signal and
        // would throw a dimension mismatch at db.index.vector.queryNodes — short-circuit to an empty result.
        if (queryEmbedding is not { Length: > 0 }) return Array.Empty<(Fact, double)>();
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        int topK = OwnerVectorOverFetch.InitialTopK(limit, hasOwner);

        // Recall-yield signal. The mean-7-of-60 starvation documented on OwnerVectorOverFetch was found by
        // a one-off study; nothing in the running system reported it. This span makes it standing. Started
        // AFTER the degraded-embedding short-circuit above, so a search that never reached the index does
        // not publish a zero-yield reading it never earned.
        using var activity = AgentMemoryDiagnostics.Source.StartActivity("memory.recall.fact_vector");
        _logger.LogDebug("Vector search facts, limit={Limit}, owner={Owner}", limit, scope?.OwnerId);

        var ranking = _rankingContext?.Current ?? _ranking;   // per-request intent (D3) overrides the configured ranking
        bool recencyRerank = ranking.RecencyRerankEnabled;
        var parameters = new Dictionary<string, object?>
        {
            ["embedding"] = queryEmbedding.ToList(),
            ["limit"] = limit,
            ["minScore"] = minScore,
        };
        if (hasOwner) parameters["ownerId"] = scope!.OwnerId;
        if (recencyRerank) RerankParameters.Add(parameters, ranking, _decay);

        async Task<List<(Fact, double)>> QueryAsync(int width, CancellationToken ct)
        {
            var cypher = FactQueries.SearchByVector(hasOwner, includeShared, width, recencyRerank);
            return await _tx.ReadAsync(async runner =>
            {
                var cursor = await runner.RunAsync(cypher, parameters).ConfigureAwait(false);
                var records = await cursor.ToListAsync().ConfigureAwait(false);
                return records.Select(r =>
                {
                    var node = r["node"].As<INode>();
                    var score = r["score"].As<double>();
                    return (MapToFact(node, ReadEmbedding(node)), score);
                }).ToList();
            }, ct).ConfigureAwait(false) ?? [];
        }

        var results = await QueryAsync(topK, cancellationToken).ConfigureAwait(false);

        // The index is global, so the owner filter runs AFTER top-K and the owner's own rows can be
        // crowded out entirely by other tenants'. Measured on a 50-owner base, the owner received a
        // mean of 7 of 60 candidates and one question received none at all from a graph holding 504
        // of its own live, embedded, above-threshold facts. Empty is total failure, so it is worth
        // one wider query; anything non-empty is left alone, because escalating on "short" would tax
        // every small tenant forever.
        int? escalatedTopK = null;
        if (OwnerVectorOverFetch.ShouldEscalate(results.Count, hasOwner))
        {
            var widened = OwnerVectorOverFetch.EscalatedTopK(topK);
            if (widened > topK)
            {
                _logger.LogDebug(
                    "Owner-scoped fact vector search returned nothing at topK={TopK}; retrying at {Widened}.",
                    topK, widened);
                escalatedTopK = widened;
                results = await QueryAsync(widened, cancellationToken).ConfigureAwait(false);
            }
        }

        if (activity is not null)
        {
            // Explicit null check rather than `activity?.SetTag(...)` per AgentMemoryDiagnostics' remarks:
            // the tag block is the only work here that exists purely to produce telemetry, so it is skipped
            // whole (no boxing, no allocation) when nobody is listening.
            //
            // owner_scoped separates the two populations: unscoped searches have no post-filter and so no
            // starvation to report, and folding them into an aggregate would dilute the very signal this
            // exists to raise. limit is the denominator — `returned` is capped by the Cypher's LIMIT $limit,
            // so 7 rows means something different at limit 10 than at limit 7.
            //
            // Success path only: a search that threw is left untagged rather than tagged `returned = 0`,
            // because a failed query measured nothing and a false zero here reads as starvation.
            activity.SetTag("memory.vector.owner_scoped", hasOwner);
            activity.SetTag("memory.vector.limit", limit);
            activity.SetTag("memory.vector.requested_topk", topK);
            // The width that ACTUALLY produced `returned`. Without it a consumer computing
            // returned / requested_topk gets a wrong ratio whenever escalation fired, because
            // `returned` then came from the widened query and `requested_topk` is the first pass.
            // Deriving it requires knowing the escalation rule, so it is emitted rather than implied.
            activity.SetTag("memory.vector.effective_topk", escalatedTopK ?? topK);
            activity.SetTag("memory.vector.returned", results.Count);
            activity.SetTag("memory.vector.escalated", escalatedTopK is not null);
            // Absent, never defaulted, when no second pass was issued: a width nobody asked for is not a
            // measurement. Same rule as LimitBinding, which is null rather than false when unmeasured.
            if (escalatedTopK is { } widenedTopK)
                activity.SetTag("memory.vector.escalated_topk", widenedTopK);
        }

        return results.Select(r => (r.Item1, r.Item2)).ToList();
    }

    public async Task<Fact?> FindDuplicateAsync(
        string subject, string predicate, float[] embedding, string? ownerId, double threshold,
        CancellationToken cancellationToken = default)
    {
        // Boundary invariant: a zero-dimension (empty/degraded) embedding can't address the vector index;
        // there is no duplicate to find, so short-circuit (caller then creates a new node).
        if (embedding is not { Length: > 0 }) return null;
        var cypher = FactQueries.FindDuplicate();
        var parameters = new Dictionary<string, object?>
        {
            ["embedding"] = embedding.ToList(),
            ["threshold"] = threshold,
            ["subject"] = subject,
            ["predicate"] = predicate,
            ["ownerKey"] = ownerId ?? OwnerKeyShared,
        };

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, parameters).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            if (records.Count == 0) return null;
            var node = records[0]["node"].As<INode>();
            return MapToFact(node, ReadEmbedding(node));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Fact?> MarkDeduplicatedAsync(string factId, double confidence, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Reinforcing fact {FactId} via dedup (confidence={Confidence}).", factId, confidence);
        return await _tx.WriteAsync(async runner =>
        {
            var cursor = await runner.RunAsync(FactQueries.MarkDeduplicated, new { id = factId, confidence }).ConfigureAwait(false);
            // 0/1-row read (not SingleAsync): the node can be concurrently hard-deleted between the dedup
            // lookup and this write (e.g. a destructive decay prune), leaving an empty result.
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            if (records.Count == 0) return null;
            var node = records[0]["f"].As<INode>();
            return MapToFact(node, ReadEmbedding(node));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateExtractedFromRelationshipAsync(string factId, string messageId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Creating EXTRACTED_FROM: Fact {FactId} -> Message {MessageId}", factId, messageId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(
                FactQueries.CreateExtractedFrom,
                new { factId, messageId }).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateAboutRelationshipAsync(string factId, string entityId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Creating ABOUT: Fact {FactId} -> Entity {EntityId}", factId, entityId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(
                FactQueries.CreateAbout,
                new { factId, entityId }).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateConversationFactRelationshipAsync(string conversationId, string factId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Creating HAS_FACT: Conversation {ConversationId} -> Fact {FactId}", conversationId, factId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(
                FactQueries.CreateConversationFact,
                new { conversationId, factId }).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    // The Fact idempotency key: the SPO triple scoped by owner_key (shared vs owned facts stay distinct, R1).
    // String components compare ordinally — matching Neo4j's exact-string MERGE — so the same triple maps to
    // the same surviving node both in the database and in the repository's by-triple lookups.
    private static (string, string, string, string) TripleKey(
        string subject, string predicate, string @object, string ownerKey)
        => (subject, predicate, @object, ownerKey);

    private static Fact MapToFact(INode node, float[]? embedding) =>
        new()
        {
            FactId = node["id"].As<string>(),
            Subject = node["subject"].As<string>(),
            Predicate = node["predicate"].As<string>(),
            Object = node["object"].As<string>(),
            OwnerId = node.Properties.TryGetValue("owner_id", out var oid) ? oid.As<string>() : null,
            Category = node.Properties.TryGetValue("category", out var cat) ? cat.As<string>() : null,
            Confidence = node["confidence"].As<double>(),
            ValidFrom = node.Properties.TryGetValue("valid_from", out var vf)
                                ? Neo4jDateTimeHelper.ReadNullableDateTimeOffset(vf)
                                : null,
            ValidUntil = node.Properties.TryGetValue("valid_until", out var vu)
                                ? Neo4jDateTimeHelper.ReadNullableDateTimeOffset(vu)
                                : null,
            Embedding = embedding,
            SourceMessageIds = node.Properties.TryGetValue("source_message_ids", out var sm)
                                ? sm.As<IList<object>>().Select(v => v.ToString()!).ToList()
                                : Array.Empty<string>(),
            CreatedAtUtc = Neo4jDateTimeHelper.ReadDateTimeOffset(node["created_at"]),
            Metadata = DeserializeMetadata(node.Properties.TryGetValue("metadata", out var md) ? md.As<string>() : null)
        };

    private static float[]? ReadEmbedding(INode node)
    {
        if (!node.Properties.TryGetValue("embedding", out var ev) || ev is null) return null;
        return ev.As<IList<object>>().Select(v => Convert.ToSingle(v)).ToArray();
    }

    public async Task<PagedResult<Fact>> GetPageWithoutEmbeddingAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting up to {Limit} facts without embeddings", limit);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(FactQueries.GetPageWithoutEmbedding, new { limit = limit + 1 }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            var items = records.Select(r =>
            {
                var node = r["f"].As<INode>();
                return MapToFact(node, null);
            }).ToList();
            return PaginationHelper.ApplyPagination(items, limit);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateEmbeddingAsync(
        string factId,
        float[] embedding,
        CancellationToken cancellationToken = default)
    {
        // Never overwrite with a zero-length vector — keep `embedding` NULL so the node stays
        // re-queueable for the back-fill rather than being poisoned with `[]`.
        if (embedding.Length == 0)
        {
            _logger.LogDebug("Skipping empty embedding update for fact {Id}.", factId);
            return;
        }

        _logger.LogDebug("Updating embedding for fact {Id}", factId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(
                FactQueries.UpdateEmbedding,
                new { id = factId, embedding = embedding.ToList() }).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(string factId, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        _logger.LogDebug("Deleting fact {Id}, owner={Owner}", factId, scope?.OwnerId);

        var cypher = FactQueries.Delete(hasOwner);

        return await _tx.WriteAsync(async runner =>
        {
            var cursor = hasOwner
                ? await runner.RunAsync(cypher, new Dictionary<string, object> { ["factId"] = factId, ["ownerId"] = scope!.OwnerId! }).ConfigureAwait(false)
                : await runner.RunAsync(cypher, new { factId }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Count > 0 && records[0]["deleted"].As<bool>();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> InvalidateAsync(string factId, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        _logger.LogDebug("Invalidating fact {Id}, owner={Owner}", factId, scope?.OwnerId);

        var cypher = FactQueries.Invalidate(hasOwner);
        string now = DateTimeOffset.UtcNow.ToString("O");

        return await _tx.WriteAsync(async runner =>
        {
            var parameters = new Dictionary<string, object?> { ["id"] = factId, ["now"] = now };
            if (hasOwner) parameters["ownerId"] = scope!.OwnerId;
            var cursor = await runner.RunAsync(cypher, parameters).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Count > 0 && records[0]["invalidated"].As<bool>();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> SupersedeAsync(string loserFactId, string winnerFactId, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        _logger.LogDebug("Superseding fact {Loser} with {Winner}, owner={Owner}", loserFactId, winnerFactId, scope?.OwnerId);

        var cypher = FactQueries.Supersede(hasOwner);
        string now = DateTimeOffset.UtcNow.ToString("O");

        return await _tx.WriteAsync(async runner =>
        {
            var parameters = new Dictionary<string, object?> { ["loserId"] = loserFactId, ["winnerId"] = winnerFactId, ["now"] = now };
            if (hasOwner) parameters["ownerId"] = scope!.OwnerId;
            var cursor = await runner.RunAsync(cypher, parameters).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Count > 0 && records[0]["superseded"].As<bool>();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Fact?> FindByTripleAsync(string subject, string predicate, string @object, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        _logger.LogDebug("Finding fact by triple ({Subject}, {Predicate}, {Object}), owner={Owner}", subject, predicate, @object, scope?.OwnerId);

        var cypher = FactQueries.FindByTriple(hasOwner, includeShared);

        // Canonicalized in C#, exactly as the write path does. Cypher's toLower() and
        // ToLowerInvariant disagree on U+0130, so computing the key here is what keeps a lookup and a
        // MERGE talking about the same triple.
        // Dictionary<string, object>, matching the driver's IDictionary overload that every other
        // parameterized read here uses. A nullable value type binds to RunAsync(string, object)
        // instead and the query silently takes a different path.
        var parameters = new Dictionary<string, object>
        {
            ["subjectKey"] = MemoryTripleCanonicalizer.CanonicalValue(subject),
            ["predicateKey"] = MemoryTripleCanonicalizer.Canonical(predicate),
            ["objectKey"] = MemoryTripleCanonicalizer.CanonicalValue(@object),
        };
        if (hasOwner)
        {
            parameters["ownerKey"] = scope!.OwnerId!;
            if (includeShared) parameters["sharedOwnerKey"] = OwnerKeyShared;
        }

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, parameters).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            if (records.Count == 0) return null;
            var node = records[0]["f"].As<INode>();
            return MapToFact(node, ReadEmbedding(node));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(Fact Fact, double Score)>> SearchByVectorAsOfAsync(
        float[] queryEmbedding,
        DateTimeOffset asOf,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        DateTimeOffset? systemAsOf = null,
        CancellationToken cancellationToken = default)
    {
        // Boundary invariant: a zero-dimension (empty/degraded) query embedding short-circuits to empty.
        if (queryEmbedding is not { Length: > 0 }) return Array.Empty<(Fact, double)>();
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        int topK = OwnerVectorOverFetch.InitialTopK(limit, hasOwner);

        // Recall-yield signal, on a span of its own. This path queries the same global vector index and
        // applies the owner filter after top-K, so it is exposed to the same starvation documented on
        // OwnerVectorOverFetch — but it is a different population from the live search: it reads a corpus
        // as it stood at a past clock reading, which was legitimately smaller. Sharing the live span name
        // would let a collapse in either be averaged away by the other, so they are named apart.
        // Started AFTER the degraded-embedding short-circuit above, so a search that never reached the
        // index does not publish a zero-yield reading it never earned.
        using var activity = AgentMemoryDiagnostics.Source.StartActivity("memory.recall.fact_vector_as_of");

        // D6 bitemporal: asOf is the valid-time clock; systemAsOf is the transaction clock (defaults to asOf
        // for ordinary single-clock recall — identical to the previous behaviour).
        _logger.LogDebug("Temporal vector search facts valid@{ValidAsOf} system@{SystemAsOf}, limit={Limit}, owner={Owner}",
            asOf, systemAsOf ?? asOf, limit, scope?.OwnerId);

        var cypher = TemporalQueries.SearchFactsAsOf(hasOwner, includeShared, topK);
        var parameters = new Dictionary<string, object?>
        {
            ["embedding"] = queryEmbedding.ToList(),
            ["limit"] = limit,
            ["minScore"] = minScore,
            ["validAsOf"] = asOf.UtcDateTime.ToString("O"),
            ["systemAsOf"] = (systemAsOf ?? asOf).UtcDateTime.ToString("O")
        };
        if (hasOwner) parameters["ownerId"] = scope!.OwnerId;

        var results = await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, parameters).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Select(r =>
            {
                var node = r["node"].As<INode>();
                var score = r["score"].As<double>();
                return (MapToFact(node, ReadEmbedding(node)), score);
            }).ToList();
        }, cancellationToken).ConfigureAwait(false) ?? [];

        if (activity is not null)
        {
            // Explicit null check rather than `activity?.SetTag(...)` per AgentMemoryDiagnostics' remarks:
            // the tag block is the only work here that exists purely to produce telemetry, so it is skipped
            // whole (no boxing, no allocation) when nobody is listening.
            //
            // Success path only: a search that threw measured nothing, and is left untagged rather than
            // tagged `returned = 0`, because a false zero here is indistinguishable from real starvation.
            activity.SetTag("memory.vector.owner_scoped", hasOwner);
            activity.SetTag("memory.vector.limit", limit);
            activity.SetTag("memory.vector.requested_topk", topK);
            activity.SetTag("memory.vector.returned", results.Count);
            // Constant false, and emitted anyway. This path issues exactly one query — the empty-result
            // widening belongs to the live search alone — so `escalated` records what happened, not a
            // choice that was weighed. It is emitted rather than omitted because omission is ambiguous:
            // an absent flag reads as an instrumentation gap, while a measured `false` lets a consumer
            // counting "total-failure recalls that got no second chance" find these rather than miss them.
            //
            // `effective_topk` is deliberately NOT emitted. On the live path it exists only because a
            // second, wider query can produce `returned` while `requested_topk` still names the first
            // pass. Here one width ever runs, so `requested_topk` IS the width that produced `returned`
            // and is a correct denominator by itself. Echoing it under a second name would publish a
            // distinction that does not exist and invite an alert on `effective_topk > requested_topk`,
            // a condition this path cannot reach. `escalated_topk` is absent for the same reason it is
            // absent on the live path's first pass: no second query was issued, so there is no width to
            // report, and a width nobody asked for is not a measurement.
            // effective_topk is emitted here too, even though it can only equal requested_topk on a
            // path that never escalates. The earlier reasoning - that a redundant tag "invites an
            // alert on effective_topk > requested_topk" - optimised for one consumer at the cost of
            // every consumer: omitting it means returned/effective_topk works on six spans and
            // silently breaks on this one, and an absent tag is indistinguishable from a site that
            // emits nothing. Uniform vocabulary across all eight spans beats a locally tidier one.
            activity.SetTag("memory.vector.effective_topk", topK);
            activity.SetTag("memory.vector.escalated", false);
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Fact>> SearchByCanonicalPredicatesAsync(
        IReadOnlyList<string> canonicalPredicates,
        int limit,
        MemoryScope scope,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? priorityPredicates = null)
    {
        ArgumentNullException.ThrowIfNull(canonicalPredicates);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        if (canonicalPredicates.Count == 0)
            return Array.Empty<Fact>();

        // Same scope semantics as every other fact read: an owner filter only when one was asked
        // for, and shared facts included unless explicitly excluded.
        var hasOwner = scope?.HasOwnerFilter == true;
        var includeShared = scope?.IncludeShared ?? true;
        var parameters = new Dictionary<string, object?>
        {
            ["predicateKeys"] = canonicalPredicates.ToArray(),
            ["limit"] = limit
        };
        // Only bind the parameter when it is actually used, so the un-prioritised query stays
        // byte-identical and its plan cache entry is unchanged.
        var priorityKeys = priorityPredicates?.Where(p => !string.IsNullOrEmpty(p)).ToArray() ?? [];
        if (priorityKeys.Length > 0) parameters["priorityKeys"] = priorityKeys;
        if (hasOwner) parameters["ownerId"] = scope!.OwnerId;

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                FactQueries.SearchByCanonicalPredicates(hasOwner, includeShared, priorityKeys.Length > 0),
                parameters).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return (IReadOnlyList<Fact>)records
                .Select(record =>
                {
                    var node = record["f"].As<INode>();
                    return MapToFact(node, ReadEmbedding(node));
                })
                .ToList();
        }, cancellationToken).ConfigureAwait(false);
    }
}
