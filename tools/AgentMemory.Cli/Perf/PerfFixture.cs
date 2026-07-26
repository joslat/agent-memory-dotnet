using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;

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
