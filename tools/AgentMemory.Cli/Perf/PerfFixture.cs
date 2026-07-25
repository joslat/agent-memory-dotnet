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

    // Sized above the shipped RecallOptions defaults (10 entities / 10 facts / 5 preferences /
    // 10 recent / 5 relevant / 3 traces) so the limits, not the fixture, decide what comes back.
    private const int EntityCount = 20;
    private const int FactCount = 20;
    private const int MessageCount = 30;
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

    public static async Task SeedAsync(HermeticProfile profile, TextWriter log, CancellationToken cancellationToken)
    {
        var services = profile.Services;
        var shortTerm = services.GetRequiredService<IShortTermMemoryService>();
        var longTerm = services.GetRequiredService<ILongTermMemoryService>();
        var reasoning = services.GetRequiredService<IReasoningMemoryService>();
        var clock = services.GetRequiredService<IClock>();

        log.WriteLine("perf: seeding scale-S fixture…");

        await shortTerm.AddConversationAsync(ConversationId, SessionId, OwnerId, null, cancellationToken)
            .ConfigureAwait(false);

        var now = clock.UtcNow;

        for (var i = 0; i < MessageCount; i++)
        {
            var text = $"Alice Martin at Acme Corporation discussed platform work item {i} " +
                       "and her preference for concise written communication.";
            await shortTerm.AddMessageAsync(new Message
            {
                MessageId = $"perf-msg-{i}",
                SessionId = SessionId,
                ConversationId = ConversationId,
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
                EntityId = $"perf-entity-{i}",
                Name = name,
                Type = "ORGANIZATION",
                Description = $"Alice Martin communication work at Acme Corporation, area {i}.",
                Confidence = 0.95,
                OwnerId = OwnerId,
                CreatedAtUtc = now,
                Embedding = Embed($"{name} Alice Martin work communication preferences", profile.Dimensions),
            }, cancellationToken).ConfigureAwait(false);
        }

        for (var i = 0; i < FactCount; i++)
        {
            await longTerm.AddFactAsync(new Fact
            {
                FactId = $"perf-fact-{i}",
                Subject = "Alice Martin",
                Predicate = i % 2 == 0 ? "works_at" : "prefers",
                Object = i % 2 == 0 ? $"Acme Corporation platform team {i}" : $"concise communication style {i}",
                Confidence = 0.9,
                OwnerId = OwnerId,
                CreatedAtUtc = now,
                Embedding = Embed(
                    $"Alice Martin work Acme Corporation communication preferences {i}", profile.Dimensions),
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
                PreferenceId = $"perf-pref-{i}",
                Category = "communication",
                PreferenceText = text,
                Confidence = 0.9,
                OwnerId = OwnerId,
                CreatedAtUtc = now,
                Embedding = Embed(text, profile.Dimensions),
            }, cancellationToken).ConfigureAwait(false);
        }

        for (var i = 0; i < TraceCount; i++)
        {
            var task = $"Summarize Alice Martin communication preferences for Acme Corporation work {i}";
            var trace = await reasoning.StartTraceAsync(
                SessionId, task, Embed(task, profile.Dimensions), null, OwnerId, cancellationToken)
                .ConfigureAwait(false);
            await reasoning.CompleteTraceAsync(trace.TraceId, "done", true, cancellationToken)
                .ConfigureAwait(false);
        }

        log.WriteLine(
            $"perf: seeded {EntityCount} entities, {FactCount} facts, {preferenceTexts.Length} preferences, " +
            $"{MessageCount} messages, {TraceCount} traces.");
    }

    /// <summary>
    /// Computes an embedding with the same function the profile's generator uses, so seeded vectors and
    /// query vectors live in the same space. Seeding through the generator would also work but would
    /// pollute the embedding counters before measurement starts.
    /// </summary>
    private static float[] Embed(string text, int dimensions) =>
        DeterministicEmbeddingGenerator.Vector(text, dimensions);
}
