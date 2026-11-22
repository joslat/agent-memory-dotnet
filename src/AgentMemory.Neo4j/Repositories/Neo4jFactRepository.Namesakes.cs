using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Resolution;
using AgentMemory.Neo4j.Infrastructure;
using Neo4j.Driver;

namespace AgentMemory.Neo4j.Repositories;

internal sealed partial class Neo4jFactRepository
{
    /// <summary>Recalled items and their entities read for one recall, at most.</summary>
    internal const int MaxNamesakeItems = 64, MaxNamesakeEntities = 32;

    /// <summary>
    /// RECALL-NAMESAKE-LEAK: who each recalled fact (bound by identity, or linked by ABOUT) and message (MENTIONS) is about,
    /// live and in scope, with each entity's facts that say what it is and, for the caller's own entities only, up to four
    /// of its source messages. One statement.
    /// </summary>
    internal const string RecalledItemEntitiesQuery = """
        UNWIND $items AS item
        CALL {
          WITH item
          OPTIONAL MATCH (:Fact {id: item.id})-[:HAS_IDENTITY_BINDING]->(:IdentityFactBinding)-[:RESOLVES_TO]->(b:Entity)
          WHERE item.kind = 'fact'
          OPTIONAL MATCH (:Fact {id: item.id})-[:ABOUT]->(a:Entity)
          WHERE item.kind = 'fact'
          OPTIONAL MATCH (:Message {id: item.id})-[:MENTIONS]->(n:Entity)
          WHERE item.kind = 'message'
          WITH collect(DISTINCT b) + collect(DISTINCT a) + collect(DISTINCT n) AS found
          UNWIND found AS e
          RETURN DISTINCT e
        }
        WITH item.id AS itemId, e
        WHERE e.invalidated_at IS NULL AND e.merged_into IS NULL
          AND (NOT $hasOwner OR e.owner_id = $ownerId OR ($includeShared AND e.owner_id IS NULL))
        WITH collect({item: itemId, id: e.id}) AS pairs, collect(DISTINCT e)[..$maxEntities] AS entities
        UNWIND entities AS e
        CALL {
          WITH e
          OPTIONAL MATCH (f:Fact)-[:HAS_IDENTITY_BINDING]->(:IdentityFactBinding)-[:RESOLVES_TO]->(e)
          WHERE f.invalidated_at IS NULL AND (NOT $hasOwner OR f.owner_id = $ownerId OR ($includeShared AND f.owner_id IS NULL))
          RETURN f
          UNION
          WITH e
          OPTIONAL MATCH (f:Fact)-[:ABOUT]->(e)
          WHERE f.invalidated_at IS NULL AND (NOT $hasOwner OR f.owner_id = $ownerId OR ($includeShared AND f.owner_id IS NULL))
          RETURN f
        }
        WITH pairs, e, f ORDER BY f.created_at DESC
        WITH pairs, e, [x IN collect(f) WHERE x IS NOT NULL][..16] AS facts
        CALL {
          WITH e
          OPTIONAL MATCH (m:Message) WHERE $hasOwner AND e.owner_id = $ownerId AND m.id IN coalesce(e.source_message_ids, [])[..4]
          RETURN [t IN collect(left(coalesce(m.content, ''), 1024)) WHERE t <> ''] AS texts
        }
        RETURN pairs, e {.id, .name, .type, .owner_id, .canonical_name, .description, .subtype,
                confidence: coalesce(e.confidence, 1.0), .created_at,
                aliases: coalesce(e.aliases, [])[..32], source_message_ids: coalesce(e.source_message_ids, [])[..32]} AS entity,
               coalesce(e.identity_surfaces, [])[..32] AS kept,
               [x IN facts | {subject: coalesce(x.subject, ''), predicate: coalesce(x.predicate, ''), object: coalesce(x.object, '')}] AS known,
               texts
        """;

    public async Task<IReadOnlyList<RecalledItemEntity>> GetRecalledItemEntitiesAsync(IReadOnlyList<string> factIds,
        IReadOnlyList<string> messageIds, MemoryScope? scope, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(factIds);
        ArgumentNullException.ThrowIfNull(messageIds);
        var items = factIds.Distinct(StringComparer.Ordinal).Select(id => new Dictionary<string, object?> { ["id"] = id, ["kind"] = "fact" })
            .Concat(messageIds.Distinct(StringComparer.Ordinal).Select(id => new Dictionary<string, object?> { ["id"] = id, ["kind"] = "message" }))
            .Take(MaxNamesakeItems).ToList();
        if (items.Count == 0) return Array.Empty<RecalledItemEntity>();
        var hasOwner = scope?.HasOwnerFilter == true;
        var parameters = new Dictionary<string, object?>
        {
            ["items"] = items, ["maxEntities"] = MaxNamesakeEntities, ["hasOwner"] = hasOwner,
            ["ownerId"] = hasOwner ? scope!.OwnerId : null, ["includeShared"] = scope?.IncludeShared ?? true,
        };
        return await _tx.ReadSingleAsync(async runner =>
        {
            var cursor = await runner.RunAsync(RecalledItemEntitiesQuery, parameters).ConfigureAwait(false);
            var rows = await cursor.ToListAsync().ConfigureAwait(false);
            var result = new List<RecalledItemEntity>();
            foreach (var row in rows)
            {
                var entity = Neo4jEntityRepository.MapToEntity(row["entity"].As<IReadOnlyDictionary<string, object>>(), null);
                var known = row["known"].As<List<Dictionary<string, object>>>()
                    .Select(fact => ((string?)fact["subject"].As<string>(), (string?)fact["predicate"].As<string>(), (string?)fact["object"].As<string>()));
                var knownAs = IdentityRelationCue.FromFacts(entity, known);
                var kept = row["kept"].As<List<object>>().Select(value => value?.ToString()).OfType<string>().ToList();
                var texts = row["texts"].As<List<string>>();
                foreach (var pair in row["pairs"].As<List<Dictionary<string, object>>>())
                    if (pair["id"].As<string>() == entity.EntityId)
                        result.Add(new RecalledItemEntity
                        {
                            ItemId = pair["item"].As<string>(), Entity = entity, KeptNames = kept, KnownAs = knownAs, SourceTexts = texts,
                        });
            }
            return (IReadOnlyList<RecalledItemEntity>)result;
        }, cancellationToken).ConfigureAwait(false);
    }
}
