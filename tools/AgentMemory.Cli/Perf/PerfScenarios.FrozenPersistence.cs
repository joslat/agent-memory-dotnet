using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace AgentMemory.Cli.Perf;

public static partial class PerfScenarios
{
    private const int FrozenEntityCount = 2;
    private const int FrozenFactCount = 2;
    private const int FrozenPreferenceCount = 1;
    private const int FrozenRelationshipCount = 1;
    private const int FrozenLearnedEmbeddingCount =
        FrozenEntityCount + FrozenFactCount + FrozenPreferenceCount;
    private const int FrozenEmbeddingRequestCount = FrozenResolutionEmbeddingCount + 1;
    private const int FrozenResolutionEmbeddingCount = FrozenEntityCount;
    private const int FrozenEmbeddingCount = FrozenLearnedEmbeddingCount + FrozenResolutionEmbeddingCount;

    private static async Task PrepareFrozenPersistenceAsync(ScenarioSetupContext ctx)
    {
        var message = FrozenPersistenceMessage(ctx.Phase, ctx.Iteration);
        const string cypher = """
            MERGE (m:Message {id: $id})
            SET m.session_id = $sessionId,
                m.conversation_id = $conversationId,
                m.role = $role,
                m.content = $content,
                m.timestamp = datetime($timestamp),
                m.tool_call_ids = [],
                m.metadata = '{}'
            RETURN count(m) AS seeded
            """;

        await using var session = ctx.Profile.Driver.AsyncSession();
        var cursor = await session.RunAsync(
            cypher,
            new
            {
                id = message.MessageId,
                sessionId = message.SessionId,
                conversationId = message.ConversationId,
                role = message.Role,
                content = message.Content,
                timestamp = message.TimestampUtc.ToString("O"),
            }).ConfigureAwait(false);
        var seeded = (await cursor.SingleAsync().ConfigureAwait(false))["seeded"].As<long>();
        if (seeded != 1)
            throw new InvalidOperationException($"PERF-W-08 setup seeded {seeded} messages, expected 1.");
    }

    private static async Task PersistFrozenExtractionAsync(ScenarioContext ctx)
    {
        var sessionId = FrozenPersistenceSessionId(ctx.Phase, ctx.Iteration);
        var ownerId = FrozenPersistenceOwnerId(ctx.Phase, ctx.Iteration);
        var message = FrozenPersistenceMessage(ctx.Phase, ctx.Iteration);
        var memory = ctx.Profile.Services.GetRequiredService<IMemoryService>();

        var result = await memory.ExtractAndPersistAsync(
            new ExtractionRequest
            {
                Messages = [message],
                SessionId = sessionId,
                UserId = ownerId,
                TypesToExtract = ExtractionTypes.All,
            },
            ctx.CancellationToken).ConfigureAwait(false);

        var resultExact =
            result.Status == IngestionStatus.Succeeded &&
            result.Entities.Count == FrozenEntityCount &&
            result.Facts.Count == FrozenFactCount &&
            result.Preferences.Count == FrozenPreferenceCount &&
            result.Relationships.Count == FrozenRelationshipCount &&
            result.SourceMessageIds.SequenceEqual([message.MessageId], StringComparer.Ordinal);
        var persistedExact =
            ctx.Turn.Counter("persist.entities") == FrozenEntityCount &&
            ctx.Turn.Counter("persist.facts") == FrozenFactCount &&
            ctx.Turn.Counter("persist.preferences") == FrozenPreferenceCount &&
            ctx.Turn.Counter("persist.relationships") == FrozenRelationshipCount;
        var spansPresent =
            ctx.Turn.SpanCounts.GetValueOrDefault("memory.extract.resolution") == 1 &&
            ctx.Turn.SpanCounts.GetValueOrDefault("memory.persist.total") == 1 &&
            ctx.Turn.SpanCounts.GetValueOrDefault("provider.embedding") == FrozenEmbeddingRequestCount;
        var excludedWork =
            ctx.Turn.Counter("llm.calls") +
            ctx.Turn.Counter("store.messages") +
            ctx.Turn.Counter("items.retrieved");

        if (!resultExact ||
            !persistedExact ||
            ctx.Turn.Counter("embed.requests") != FrozenEmbeddingRequestCount ||
            ctx.Turn.Counter("embed.items") != FrozenEmbeddingCount ||
            !spansPresent ||
            excludedWork != 0)
        {
            throw new InvalidOperationException(
                $"PERF-W-08 frozen persistence contract failed (result_exact={resultExact}, " +
                $"persisted_exact={persistedExact}, embed.requests/items=" +
                $"{ctx.Turn.Counter("embed.requests")}/{ctx.Turn.Counter("embed.items")}, expected " +
                $"{FrozenEmbeddingRequestCount}/{FrozenEmbeddingCount}; resolution/persistence/provider spans=" +
                $"{ctx.Turn.SpanCounts.GetValueOrDefault("memory.extract.resolution")}/" +
                $"{ctx.Turn.SpanCounts.GetValueOrDefault("memory.persist.total")}/" +
                $"{ctx.Turn.SpanCounts.GetValueOrDefault("provider.embedding")}, expected " +
                $"1/1/{FrozenEmbeddingRequestCount}; excluded_work={excludedWork}/0).");
        }
    }

    private static async Task VerifyFrozenPersistenceAsync(ScenarioVerificationContext ctx)
    {
        var sessionId = FrozenPersistenceSessionId(ctx.Phase, ctx.Iteration);
        var ownerId = FrozenPersistenceOwnerId(ctx.Phase, ctx.Iteration);
        var messageId = FrozenPersistenceMessage(ctx.Phase, ctx.Iteration).MessageId;
        const string cypher = """
            CALL { MATCH (m:Message {session_id: $sessionId}) RETURN count(m) AS messages }
            CALL { MATCH (e:Entity {owner_id: $ownerId}) RETURN count(e) AS entities }
            CALL { MATCH (f:Fact {owner_id: $ownerId}) RETURN count(f) AS facts }
            CALL { MATCH (p:Preference {owner_id: $ownerId}) RETURN count(p) AS preferences }
            CALL {
                MATCH (:Entity {owner_id: $ownerId})-[r:RELATED_TO]->(:Entity {owner_id: $ownerId})
                WHERE r.owner_id = $ownerId AND r.relation_type = 'LAB_P0_WORKS_AT'
                RETURN count(r) AS relationships,
                       count(CASE WHEN $messageId IN r.source_message_ids THEN 1 END)
                           AS relationshipSources
            }
            CALL {
                MATCH (memory)-[:EXTRACTED_FROM]->(:Message {id: $messageId})
                WHERE memory.owner_id = $ownerId
                RETURN count(*) AS provenance
            }
            CALL {
                MATCH (source:Entity)-[r:RELATED_TO]->(target:Entity)
                WHERE r.owner_id = $ownerId
                  AND (source.owner_id <> $ownerId OR target.owner_id <> $ownerId)
                RETURN count(r) AS crossOwnerEdges
            }
            RETURN messages, entities, facts, preferences, relationships,
                   relationshipSources, provenance, crossOwnerEdges
            """;

        await using var session = ctx.Profile.Driver.AsyncSession();
        var cursor = await session.RunAsync(
            cypher,
            new { sessionId, ownerId, messageId }).ConfigureAwait(false);
        var record = await cursor.SingleAsync().ConfigureAwait(false);
        var messages = record["messages"].As<long>();
        var entities = record["entities"].As<long>();
        var facts = record["facts"].As<long>();
        var preferences = record["preferences"].As<long>();
        var relationships = record["relationships"].As<long>();
        var relationshipSources = record["relationshipSources"].As<long>();
        var provenance = record["provenance"].As<long>();
        var crossOwnerEdges = record["crossOwnerEdges"].As<long>();

        if (messages != 1 ||
            entities != FrozenEntityCount ||
            facts != FrozenFactCount ||
            preferences != FrozenPreferenceCount ||
            relationships != FrozenRelationshipCount ||
            relationshipSources != FrozenRelationshipCount ||
            provenance != FrozenLearnedEmbeddingCount ||
            crossOwnerEdges != 0)
        {
            throw new InvalidOperationException(
                $"PERF-W-08 graph read-back failed (messages={messages}/1, entities/facts/preferences/" +
                $"relationships={entities}/{facts}/{preferences}/{relationships}, expected " +
                $"{FrozenEntityCount}/{FrozenFactCount}/{FrozenPreferenceCount}/" +
                $"{FrozenRelationshipCount}; provenance={provenance}/{FrozenLearnedEmbeddingCount}, " +
                $"relationship_sources={relationshipSources}/{FrozenRelationshipCount}, " +
                $"cross_owner_edges={crossOwnerEdges}/0).");
        }
    }

    private static Message FrozenPersistenceMessage(string phase, int iteration)
    {
        var sessionId = FrozenPersistenceSessionId(phase, iteration);
        return new Message
        {
            MessageId = $"{sessionId}-msg-00",
            ConversationId = $"{sessionId}-conversation",
            SessionId = sessionId,
            Role = "user",
            Content = FrozenExtractionOverrides.SourceMarker,
            TimestampUtc = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
        };
    }

    private static string FrozenPersistenceSessionId(string phase, int iteration) =>
        $"perf-w08-{phase}-{iteration}";

    private static string FrozenPersistenceOwnerId(string phase, int iteration) =>
        $"{FrozenPersistenceSessionId(phase, iteration)}-owner";
}
