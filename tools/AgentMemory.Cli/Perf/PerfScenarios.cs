using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentMemory.Cli.Perf;

/// <summary>A named, versioned unit of measurement.</summary>
/// <remarks>
/// Ids are stable forever. Adding a scenario is additive; <em>changing</em> one requires a new id, so a
/// number recorded today stays comparable to one recorded a year from now.
/// </remarks>
public sealed record PerfScenario(
    string Id,
    string Description,
    Func<ScenarioContext, Task> RunAsync,
    bool SupportsInterleavedAb = true);

/// <summary>Everything a scenario body needs for one iteration.</summary>
/// <param name="Phase">
/// <c>warmup</c> or <c>measure</c>. Scenarios that create per-iteration state must include this in the
/// key they derive — both phases count iterations from zero, so the index alone is not unique.
/// </param>
public sealed record ScenarioContext(
    HermeticProfile Profile,
    Neo4jMemoryContextProvider Provider,
    TurnRecord Turn,
    int Iteration,
    string Phase,
    string? Variant,
    RecallOptions RecallOptions,
    CancellationToken CancellationToken);

/// <summary>
/// The Step 1 scenario catalog: one read-path scenario and one write-path scenario, both at shipped
/// defaults. Between them they cover the two questions the roadmap's estimates most need replaced with
/// facts — what a recall costs before the model runs, and what a turn costs after it.
/// </summary>
public static class PerfScenarios
{
    public static IReadOnlyList<PerfScenario> All { get; } =
    [
        new(
            "PERF-R-01",
            "Greeting-only turn at the shipped default recall policy",
            GreetingRecallAsync),
        new("PERF-R-04", "Full multi-category recall at shipped defaults", RecallAsync),
        new(
            "PERF-W-02",
            "Single response message, extraction enabled (shipped defaults)",
            StoreAndExtractAsync,
            SupportsInterleavedAb: false),
    ];

    private const string StoreProbeUserMessage =
        "Alice Martin just moved to the Acme Corporation platform team and prefers concise updates.";

    /// <summary>
    /// Input-keyed model responses required by cost scenarios. Kept separate from judged fixture rules:
    /// an unmatched rule deliberately returns an empty extraction, so omitting this entry turns W-02
    /// into a no-op that its self-assertion rejects.
    /// </summary>
    internal static IReadOnlyList<ScriptedChatClient.Rule> ScriptedRules { get; } =
        [new(StoreProbeUserMessage, ScriptedChatClient.ExtractionPayload)];

    public static IReadOnlyList<PerfScenario> Select(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter) || filter.Equals("all", StringComparison.OrdinalIgnoreCase))
            return All;

        var wanted = filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var selected = All.Where(s => wanted.Contains(s.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        if (selected.Count == 0)
            throw new ArgumentException(
                $"no scenario matched '{filter}'. Known: {string.Join(", ", All.Select(s => s.Id))}.");
        return selected;
    }

    /// <summary>
    /// PERF-R-01 — a greeting/acknowledgement turn with no memory-retrieval intent. The shipped default
    /// policy still recalls, so this captures the work that matrix rank 1 must eliminate.
    /// </summary>
    private static async Task GreetingRecallAsync(ScenarioContext ctx)
    {
        var identity = ctx.Variant is null
            ? PerfFixture.DefaultIdentity
            : PerfFixture.ForVariant(ctx.Variant);
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "thanks, that's great"),
        };

        var context = await ctx.Provider.BuildContextAsync(
            messages,
            identity.SessionId,
            identity.ConversationId,
            ctx.CancellationToken,
            identity.OwnerId).ConfigureAwait(false);

        RecordContext(ctx.Turn, context);

        // This scenario measures today's shipped policy, not a fixture-configured skipping policy.
        // Ten recent messages are guaranteed by the scale-S fixture and are independent of semantic
        // similarity, so they provide a robust proof that the recall path really ran.
        var recent = ctx.Turn.Counter("items.recent");
        var retrieved = ctx.Turn.Counter("items.retrieved");
        var reads = ctx.Turn.Counter("neo4j.tx.read");
        var embeddings = ctx.Turn.Counter("embed.requests");
        var preferences = ctx.Turn.Counter("items.preferences");
        var accessTracked = ctx.Turn.Counter("access_tracking.items");
        var unexpectedSemantic =
            ctx.Turn.Counter("items.relevant") +
            ctx.Turn.Counter("items.entities") +
            ctx.Turn.Counter("items.facts") +
            ctx.Turn.Counter("items.traces");
        if (recent != 10 || preferences != 1 || unexpectedSemantic != 0 ||
            retrieved != 11 || accessTracked != 1 || reads == 0 || embeddings == 0)
        {
            throw new InvalidOperationException(
                $"PERF-R-01 did not exercise shipped-default recall (items.recent={recent}/10, " +
                $"items.preferences={preferences}/1, items.other_semantic={unexpectedSemantic}/0, " +
                $"items.retrieved={retrieved}/11, access_tracking.items={accessTracked}/1, " +
                $"neo4j.tx.read={reads}, embed.requests={embeddings}). The deterministic fixture's " +
                "current hash-bucket overlap is part of this locked scenario shape. Do not configure " +
                "a skipping policy inside this fixture: rank 1 must change product behavior and update " +
                "this expectation explicitly.");
        }
    }

    /// <summary>
    /// PERF-R-04 — the reference read turn: everything the MAF provider does before the model is called.
    /// </summary>
    private static async Task RecallAsync(ScenarioContext ctx)
    {
        var identity = ctx.Variant is null
            ? PerfFixture.DefaultIdentity
            : PerfFixture.ForVariant(ctx.Variant);
        var messages = new[] { new ChatMessage(ChatRole.User, PerfFixture.ProbeQueryFor(identity)) };

        var context = await ctx.Provider.BuildContextAsync(
            messages,
            identity.SessionId,
            identity.ConversationId,
            ctx.CancellationToken,
            identity.OwnerId).ConfigureAwait(false);

        RecordContext(ctx.Turn, context);

        // Self-check, not decoration. A fixture whose vectors drift below MinSimilarityScore produces an
        // empty recall that still "succeeds" — and a baseline recorded from that would understate the
        // real cost by an order of magnitude and be quietly wrong forever after.
        var expected = PerfFixture.ExpectedRecall(ctx.RecallOptions);
        var retrieved = ctx.Turn.Counter("items.retrieved");
        if (retrieved != expected.Total)
        {
            var breakdown = string.Join(", ", expected.ByCategory
                .Select(kv => $"{kv.Key}={ctx.Turn.Counter($"items.{kv.Key}")}/{kv.Value}"));
            throw new InvalidOperationException(
                $"PERF-R-04 recalled {retrieved} items but expected {expected.Total} " +
                $"({breakdown}). The fixture is not exercising the configured recall shape, so this " +
                "measurement would be misleading. Check MinSimilarityScore and the seeded embeddings.");
        }
    }

    private static void RecordContext(TurnRecord turn, AIContext context)
    {
        // Materialized once: AIContext.Messages is an enumerable, so counting and summing it separately
        // would enumerate it twice.
        var contextMessages = context.Messages?.ToList() ?? [];
        turn.Add("context.messages", contextMessages.Count);
        turn.Add("context.chars", contextMessages.Sum(m => (long)(m.Text?.Length ?? 0)));
    }

    /// <summary>
    /// PERF-W-02 — the reference write turn: response-message persistence plus automatic extraction,
    /// which is what <c>AutoExtractOnPersist=true</c> makes every turn pay today.
    /// </summary>
    private static async Task StoreAndExtractAsync(ScenarioContext ctx)
    {
        // A distinct session per iteration keeps iterations independent: reusing one would let each turn
        // see the previous turn's messages and entities, so cost would climb with iteration number and
        // the "measurement" would really be measuring accumulation.
        //
        // The PHASE must be part of the key, not just the index. Warm-up and measurement both count from
        // zero, so keying on the index alone made measured iteration 0 run against the session warm-up
        // iteration 0 had already populated — the one measured turn that was not a clean turn.
        var sessionId = $"perf-w02-{ctx.Phase}-{ctx.Iteration}";
        var conversationId = $"{sessionId}-conv";
        var requestMessages = new[]
        {
            new ChatMessage(ChatRole.User, StoreProbeUserMessage),
        };
        var responseMessages = new[]
        {
            new ChatMessage(ChatRole.Assistant,
                "Noted — Alice Martin is on the Acme Corporation platform team and prefers concise written updates."),
        };

        await ctx.Provider.PerformStoreAsync(
            requestMessages,
            responseMessages,
            sessionId,
            conversationId,
            ctx.CancellationToken,
            PerfFixture.OwnerId).ConfigureAwait(false);

        // The mirror of the recall self-check: a scripted model returning unparseable output would make
        // extraction yield nothing, persistence write nothing, and this scenario measure a no-op. Model
        // calls alone are not evidence: an empty-but-valid response still records all four calls.
        var modelCalls = ctx.Turn.Counter("llm.calls");
        var entities = ctx.Turn.Counter("persist.entities");
        var facts = ctx.Turn.Counter("persist.facts");
        var preferences = ctx.Turn.Counter("persist.preferences");
        if (modelCalls == 0 || entities == 0 || facts == 0 || preferences == 0)
        {
            throw new InvalidOperationException(
                $"PERF-W-02 did not persist the scripted extraction (llm.calls={modelCalls}, " +
                $"persist.entities={entities}, persist.facts={facts}, " +
                $"persist.preferences={preferences}). The scenario would measure a no-op. Check that " +
                "LLM extraction is opted in, AutoExtractOnPersist is true, and the scripted client " +
                "returned its cost-scenario payload.");
        }
    }

    /// <summary>
    /// Builds the MAF context provider used by both scenarios, from the same services a host would get.
    /// </summary>
    /// <remarks>
    /// Constructed directly rather than resolved: the AgentFramework options types are not registered by
    /// <c>AddNeo4jAgentMemory</c>, and passing explicit defaults here documents that the scenarios run
    /// against shipped defaults rather than a tuned configuration.
    /// </remarks>
    public static Neo4jMemoryContextProvider CreateProvider(
        HermeticProfile profile,
        RecallOptions? recallOptions = null)
    {
        var services = profile.Services;
        var configuredMemory = services.GetRequiredService<IOptions<MemoryOptions>>().Value;
        var selectedRecall = recallOptions ?? configuredMemory.Recall;
        return new Neo4jMemoryContextProvider(
            services.GetRequiredService<IMemoryService>(),
            services.GetRequiredService<IEmbeddingOrchestrator>(),
            services.GetRequiredService<IClock>(),
            services.GetRequiredService<IIdGenerator>(),
            Options.Create(configuredMemory with { Recall = selectedRecall }),
            Options.Create(new ContextFormatOptions()),
            Options.Create(new AgentFrameworkOptions()),
            services.GetRequiredService<ILogger<Neo4jMemoryContextProvider>>());
    }
}
