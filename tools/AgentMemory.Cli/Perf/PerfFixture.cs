using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace AgentMemory.Cli.Perf;

/// <summary>
/// Seeds the scale-S dataset: enough memory, close enough to the probe query, that a default recall
/// actually returns its configured limits.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the texts overlap so heavily with the probe query.</b> Vector recall filters on
/// <c>RecallOptions.MinSimilarityScore</c> (0.7 by default). If seeded items sit at chance similarity
/// to the query, every semantic search returns empty — and then access tracking never fires, extraction
/// has nothing to resolve against, and the scenario reports a healthy-looking measurement of almost no
/// work. That failure is silent, which is precisely why <see cref="PerfScenarios"/> asserts on the
/// retrieved item count rather than trusting the fixture.
/// </para>
/// <para>
/// Items are therefore drawn from one topic with deliberate token overlap. This is a fixture, not a
/// retrieval-quality benchmark: its job is to put the recall path into its default shape.
/// </para>
/// </remarks>
public static class PerfFixture
{
    /// <summary>The probe query every recall scenario issues.</summary>
    public const string ProbeQuery =
        "What does Alice Martin work on at Acme Corporation, and what are her communication preferences?";

    /// <summary>Owner for every seeded item; recall is owner-scoped, so this must match the probe.</summary>
    public const string OwnerId = "perf-owner";

    /// <summary>Session used by the recall scenarios.</summary>
    public const string SessionId = "perf-session";

    /// <summary>Conversation used by the recall scenarios.</summary>
    public const string ConversationId = "perf-session-conv";

    public sealed record DatasetIdentity(
        string OwnerId,
        string SessionId,
        string ConversationId,
        string IdPrefix,
        string TopicToken);

    public static DatasetIdentity DefaultIdentity { get; } =
        new(OwnerId, SessionId, ConversationId, "perf", string.Empty);

    public static DatasetIdentity ForVariant(string variant) =>
        new(
            $"{OwnerId}-{variant}",
            $"{SessionId}-{variant}",
            $"{ConversationId}-{variant}",
            $"perf-{variant}",
            variant switch
            {
                "control" => "alpha",
                "candidate" => "bravo",
                _ => throw new ArgumentException($"unknown A/B fixture variant '{variant}'.", nameof(variant)),
            });

    public static string ProbeQueryFor(DatasetIdentity identity) =>
        Qualify(ProbeQuery, identity);

    // Sized above the shipped RecallOptions defaults (10 entities / 10 facts / 5 preferences /
    // 10 recent / 5 relevant / 3 traces) so the limits, not the fixture, decide what comes back.
    private const int EntityCount = 20;
    private const int FactCount = 20;
    private const int MessageCount = 30;
    private const int PreferenceCount = 12;
    private const int TraceCount = 8;

    /// <summary>
    /// What a default recall should return per category once seeded — the scenario's self-check.
    /// These are the shipped <see cref="RecallOptions"/> limits, so this doubles as documentation of
    /// the default recall shape.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> ExpectedByCategory =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["recent"] = 10,
            ["relevant"] = 5,
            ["entities"] = 10,
            ["facts"] = 10,
            ["preferences"] = 5,
            ["traces"] = 3,
        };

    /// <summary>Total items a default recall should return once seeded.</summary>
    public static readonly int ExpectedRecalledItems = ExpectedByCategory.Values.Sum();

    public static async Task SeedAsync(
        HermeticProfile profile,
        TextWriter log,
        CancellationToken cancellationToken,
        DatasetIdentity? identity = null)
    {
        identity ??= DefaultIdentity;
        var services = profile.Services;
        var shortTerm = services.GetRequiredService<IShortTermMemoryService>();
        var longTerm = services.GetRequiredService<ILongTermMemoryService>();
        var reasoning = services.GetRequiredService<IReasoningMemoryService>();
        var clock = services.GetRequiredService<IClock>();

        log.WriteLine("perf: seeding scale-S fixture…");

        await shortTerm.AddConversationAsync(
                identity.ConversationId, identity.SessionId, identity.OwnerId, null, cancellationToken)
            .ConfigureAwait(false);

        var now = clock.UtcNow;

        for (var i = 0; i < MessageCount; i++)
        {
            var text = Qualify($"Alice Martin at Acme Corporation discussed platform work item {i} " +
                               "and her preference for concise written communication.", identity);
            await shortTerm.AddMessageAsync(new Message
            {
                MessageId = $"{identity.IdPrefix}-msg-{i}",
                SessionId = identity.SessionId,
                ConversationId = identity.ConversationId,
                Role = i % 2 == 0 ? "user" : "assistant",
                Content = text,
                TimestampUtc = now.AddSeconds(i),
            }, cancellationToken).ConfigureAwait(false);
        }

        for (var i = 0; i < EntityCount; i++)
        {
            var name = $"Acme Corporation platform team {i}";
            await longTerm.AddEntityAsync(new Entity
            {
                EntityId = $"{identity.IdPrefix}-entity-{i}",
                Name = name,
                Type = "ORGANIZATION",
                Description = $"Alice Martin communication work at Acme Corporation, area {i}.",
                Confidence = 0.95,
                OwnerId = identity.OwnerId,
                CreatedAtUtc = now,
                Embedding = Embed(
                    Qualify($"{name} Alice Martin work communication preferences", identity), profile.Dimensions),
            }, cancellationToken).ConfigureAwait(false);
        }

        for (var i = 0; i < FactCount; i++)
        {
            await longTerm.AddFactAsync(new Fact
            {
                FactId = $"{identity.IdPrefix}-fact-{i}",
                Subject = "Alice Martin",
                Predicate = i % 2 == 0 ? "works_at" : "prefers",
                Object = i % 2 == 0 ? $"Acme Corporation platform team {i}" : $"concise communication style {i}",
                Confidence = 0.9,
                OwnerId = identity.OwnerId,
                CreatedAtUtc = now,
                Embedding = Embed(
                    Qualify($"Alice Martin work Acme Corporation communication preferences {i}", identity),
                    profile.Dimensions),
            }, cancellationToken).ConfigureAwait(false);
        }

        // Preference texts must be pulled apart deliberately. AddPreferenceAsync deduplicates on create:
        // a new preference in the same category whose embedding is within DeduplicationSimilarityThreshold
        // (0.95) of an existing one REINFORCES that one instead of creating a node. Twelve paraphrases of
        // "prefers concise communication" therefore collapse to a single node, and recall then returns 1
        // preference where the default limit is 5. Each entry below shares the probe query's anchor terms
        // (Alice Martin / Acme Corporation / preference) so it still clears MinSimilarityScore, but carries
        // enough distinct content to stay under the dedup threshold.
        var preferenceTexts = new[]
        {
            "Alice Martin prefers concise written summaries rather than long documents at Acme Corporation",
            "Alice Martin prefers asynchronous updates over scheduled meetings at Acme Corporation",
            "Alice Martin prefers code examples included in technical explanations at Acme Corporation",
            "Alice Martin prefers being notified about incidents by pager at Acme Corporation",
            "Alice Martin prefers weekly planning agendas circulated in advance at Acme Corporation",
            "Alice Martin prefers dark mode interfaces and keyboard shortcuts at Acme Corporation",
            "Alice Martin prefers metric units and ISO dates in reports at Acme Corporation",
            "Alice Martin prefers direct feedback without hedging language at Acme Corporation",
            "Alice Martin prefers small reviewable pull requests at Acme Corporation",
            "Alice Martin prefers morning deep work blocks kept free at Acme Corporation",
            "Alice Martin prefers diagrams over prose for architecture at Acme Corporation",
            "Alice Martin prefers recorded demos instead of live presentations at Acme Corporation",
        };

        foreach (var (text, i) in preferenceTexts.Select((t, i) => (t, i)))
        {
            await longTerm.AddPreferenceAsync(new Preference
            {
                PreferenceId = $"{identity.IdPrefix}-pref-{i}",
                Category = "communication",
                PreferenceText = text,
                Confidence = 0.9,
                OwnerId = identity.OwnerId,
                CreatedAtUtc = now,
                Embedding = Embed(Qualify(text, identity), profile.Dimensions),
            }, cancellationToken).ConfigureAwait(false);
        }

        for (var i = 0; i < TraceCount; i++)
        {
            var task = Qualify(
                $"Summarize Alice Martin communication preferences for Acme Corporation work {i}", identity);
            var trace = await reasoning.StartTraceAsync(
                identity.SessionId, task, Embed(task, profile.Dimensions), null, identity.OwnerId, cancellationToken)
                .ConfigureAwait(false);
            await reasoning.CompleteTraceAsync(trace.TraceId, "done", true, cancellationToken)
                .ConfigureAwait(false);
        }

        log.WriteLine(
            $"perf: seeded {identity.IdPrefix}: {EntityCount} entities, {FactCount} facts, " +
            $"{preferenceTexts.Length} preferences, {MessageCount} messages, {TraceCount} traces.");
    }

    /// <summary>
    /// Seeds an iteration-isolated whole-session extraction fixture in one raw-driver transaction.
    /// This runs before the measured turn: setup latency and setup database work are fixture cost, not
    /// product extraction cost. The production session-read and extraction paths remain fully measured.
    /// </summary>
    public static async Task SeedSessionExtractionAsync(
        HermeticProfile profile,
        string sessionId,
        string conversationId,
        int messageCount,
        CancellationToken cancellationToken)
    {
        var startedAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var messages = Enumerable.Range(0, messageCount)
            .Select(index => new Dictionary<string, object?>
            {
                ["id"] = $"{sessionId}-msg-{index:D2}",
                ["session_id"] = sessionId,
                ["conversation_id"] = conversationId,
                ["role"] = index % 2 == 0 ? "user" : "assistant",
                ["content"] = index == 0
                    ? PerfScenarios.StoreProbeUserMessage
                    : $"Session extraction fixture message {index:D2}: Alice Martin works on the " +
                      "Acme Corporation platform team and prefers concise written updates.",
                ["timestamp"] = startedAt.AddSeconds(index).ToString("O"),
                ["tool_call_ids"] = Array.Empty<string>(),
                ["metadata"] = "{}",
            })
            .ToList();

        const string cypher = """
            UNWIND $messages AS item
            MERGE (m:Message {id: item.id})
            SET m.session_id = item.session_id,
                m.conversation_id = item.conversation_id,
                m.role = item.role,
                m.content = item.content,
                m.timestamp = item.timestamp,
                m.tool_call_ids = item.tool_call_ids,
                m.metadata = item.metadata
            RETURN count(m) AS seeded
            """;

        await using var session = profile.Driver.AsyncSession();
        var seeded = await session.ExecuteWriteAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(cypher, new { messages }).ConfigureAwait(false);
            var record = await cursor.SingleAsync().ConfigureAwait(false);
            return record["seeded"].As<long>();
        }).ConfigureAwait(false);

        if (seeded != messageCount)
        {
            throw new InvalidOperationException(
                $"Session extraction setup seeded {seeded} messages but expected {messageCount}.");
        }
    }

    public sealed record SessionExtractionShape(
        long Messages,
        long Entities,
        long Facts,
        long Preferences,
        long ProvenanceRelationships);

    public sealed record RawBatchStorageShape(
        long Messages,
        long MessagesWithExpectedEmbedding,
        long DistinctIds,
        IReadOnlyList<string> Ids);

    /// <summary>
    /// Reads raw messages after the measured turn to prove the batch and every expected-size embedding
    /// reached Neo4j. The raw driver keeps verification work out of the measured product counters.
    /// </summary>
    public static async Task<RawBatchStorageShape> InspectRawBatchStorageAsync(
        HermeticProfile profile,
        string sessionId,
        int dimensions)
    {
        const string cypher = """
            MATCH (m:Message {session_id: $sessionId})
            WITH m ORDER BY m.id
            RETURN count(m) AS messages,
                   count(CASE WHEN m.embedding IS NOT NULL
                                   AND size(m.embedding) = $dimensions THEN 1 END)
                       AS messagesWithExpectedEmbedding,
                   count(DISTINCT m.id) AS distinctIds,
                   collect(m.id) AS ids
            """;

        await using var session = profile.Driver.AsyncSession();
        var cursor = await session.RunAsync(
            cypher,
            new { sessionId, dimensions }).ConfigureAwait(false);
        var record = await cursor.SingleAsync().ConfigureAwait(false);
        return new RawBatchStorageShape(
            record["messages"].As<long>(),
            record["messagesWithExpectedEmbedding"].As<long>(),
            record["distinctIds"].As<long>(),
            record["ids"].As<List<object>>().Select(value => value.As<string>()).ToArray());
    }

    /// <summary>
    /// Reads the graph after the measured turn to prove extraction actually learned the expected items.
    /// Raw-driver verification is intentional: it runs outside the turn and must not inflate product cost.
    /// </summary>
    public static async Task<SessionExtractionShape> InspectSessionExtractionAsync(
        HermeticProfile profile,
        string sessionId,
        string ownerId)
    {
        const string cypher = """
            CALL { MATCH (m:Message {session_id: $sessionId}) RETURN count(m) AS messages }
            CALL { MATCH (e:Entity {owner_id: $ownerId}) RETURN count(e) AS entities }
            CALL { MATCH (f:Fact {owner_id: $ownerId}) RETURN count(f) AS facts }
            CALL { MATCH (p:Preference {owner_id: $ownerId}) RETURN count(p) AS preferences }
            CALL {
                MATCH (memory)-[:EXTRACTED_FROM]->(m:Message {session_id: $sessionId})
                WHERE memory.owner_id = $ownerId
                RETURN count(*) AS provenance
            }
            RETURN messages, entities, facts, preferences, provenance
            """;

        await using var session = profile.Driver.AsyncSession();
        var cursor = await session.RunAsync(cypher, new { sessionId, ownerId }).ConfigureAwait(false);
        var record = await cursor.SingleAsync().ConfigureAwait(false);
        return new SessionExtractionShape(
            record["messages"].As<long>(),
            record["entities"].As<long>(),
            record["facts"].As<long>(),
            record["preferences"].As<long>(),
            record["provenance"].As<long>());
    }

    public sealed record ExpectedRecallShape(IReadOnlyDictionary<string, int> ByCategory, int Total);

    /// <summary>Expected shape for a configured recall, capped by what scale S seeds.</summary>
    public static ExpectedRecallShape ExpectedRecall(RecallOptions options)
    {
        var byCategory = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["recent"] = Math.Min(options.MaxRecentMessages, MessageCount),
            ["relevant"] = Math.Min(options.MaxRelevantMessages, MessageCount),
            ["entities"] = Math.Min(options.MaxEntities, EntityCount),
            ["facts"] = Math.Min(options.MaxFacts, FactCount),
            ["preferences"] = Math.Min(options.MaxPreferences, PreferenceCount),
            ["traces"] = Math.Min(options.MaxTraces, TraceCount),
        };

        return new ExpectedRecallShape(byCategory, byCategory.Values.Sum());
    }

    /// <summary>
    /// Computes an embedding with the same function the profile's generator uses, so seeded vectors and
    /// query vectors live in the same space. Seeding through the generator would also work but would
    /// pollute the embedding counters before measurement starts.
    /// </summary>
    private static float[] Embed(string text, int dimensions) =>
        DeterministicEmbeddingGenerator.Vector(text, dimensions);

    private static string Qualify(string text, DatasetIdentity identity) =>
        string.IsNullOrEmpty(identity.TopicToken)
            ? text
            : $"{text} {identity.TopicToken}";

}
