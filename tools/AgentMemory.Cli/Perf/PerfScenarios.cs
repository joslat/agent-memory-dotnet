using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
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
public sealed record PerfScenario(string Id, string Description, Func<ScenarioContext, Task> RunAsync);

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
        new("PERF-R-04", "Full multi-category recall at shipped defaults", RecallAsync),
        new("PERF-W-02", "Single response message, extraction enabled (shipped defaults)", StoreAndExtractAsync),
    ];

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
    /// PERF-R-04 — the reference read turn: everything the MAF provider does before the model is called.
    /// </summary>
    private static async Task RecallAsync(ScenarioContext ctx)
    {
        var messages = new[] { new ChatMessage(ChatRole.User, PerfFixture.ProbeQuery) };

        var context = await ctx.Provider.BuildContextAsync(
            messages,
            PerfFixture.SessionId,
            PerfFixture.ConversationId,
            ctx.CancellationToken,
            PerfFixture.OwnerId).ConfigureAwait(false);

        // Materialized once: AIContext.Messages is an enumerable, so counting and summing it separately
        // would enumerate it twice.
        var contextMessages = context.Messages?.ToList() ?? [];
        ctx.Turn.Add("context.messages", contextMessages.Count);
        ctx.Turn.Add("context.chars", contextMessages.Sum(m => (long)(m.Text?.Length ?? 0)));

        // Self-check, not decoration. A fixture whose vectors drift below MinSimilarityScore produces an
        // empty recall that still "succeeds" — and a baseline recorded from that would understate the
        // real cost by an order of magnitude and be quietly wrong forever after.
        var retrieved = ctx.Turn.Counter("items.retrieved");
        if (retrieved < PerfFixture.ExpectedRecalledItems)
        {
            var breakdown = string.Join(", ", PerfFixture.ExpectedByCategory
                .Select(kv => $"{kv.Key}={ctx.Turn.Counter($"items.{kv.Key}")}/{kv.Value}"));
            throw new InvalidOperationException(
                $"PERF-R-04 recalled {retrieved} items but expected {PerfFixture.ExpectedRecalledItems} " +
                $"({breakdown}). The fixture is not exercising the default recall shape, so this " +
                "measurement would be misleading. Check MinSimilarityScore and the seeded embeddings.");
        }
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
            new ChatMessage(ChatRole.User,
                "Alice Martin just moved to the Acme Corporation platform team and prefers concise updates."),
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
        // extraction yield nothing, persistence write nothing, and this scenario measure a no-op.
        if (ctx.Turn.Counter("llm.calls") == 0)
        {
            throw new InvalidOperationException(
                "PERF-W-02 recorded zero LLM calls, so automatic extraction did not run. Check that LLM " +
                "extraction is opted in (AddNeo4jAgentMemory's configureLlm) and AutoExtractOnPersist is true.");
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
    public static Neo4jMemoryContextProvider CreateProvider(HermeticProfile profile)
    {
        var services = profile.Services;
        return new Neo4jMemoryContextProvider(
            services.GetRequiredService<IMemoryService>(),
            services.GetRequiredService<IEmbeddingOrchestrator>(),
            services.GetRequiredService<IClock>(),
            services.GetRequiredService<IIdGenerator>(),
            services.GetRequiredService<IOptions<MemoryOptions>>(),
            Options.Create(new ContextFormatOptions()),
            Options.Create(new AgentFrameworkOptions()),
            services.GetRequiredService<ILogger<Neo4jMemoryContextProvider>>());
    }
}
