using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using AgentMemory.Neo4j.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.Driver;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// Mention-frequency reranking (R7), and the loop it must not become.
/// </summary>
/// <remarks>
/// <para>
/// A fact the world asserted five times is usually more central than one mentioned once, and
/// similarity cannot see that: two facts phrased alike score alike however often either was said.
/// </para>
/// <para>
/// <b>The signal's provenance is the task.</b> The counter must record how often <i>the world</i>
/// re-asserted a fact, never how often <i>we</i> surfaced it. Ranking on our own retrievals is a
/// rich-get-richer loop — whatever ranks highly gets retrieved, retrieval raises the count, the count
/// raises the rank — which looks like learning and is self-reinforcement. The tests below pin the
/// source of the number, not only its effect.
/// </para>
/// </remarks>
public sealed class MentionFrequencyRerankerTests
{
    private readonly INeo4jTransactionRunner _tx = Substitute.For<INeo4jTransactionRunner>();

    private MentionFrequencyReranker CreateSut(bool enabled = true) =>
        new(_tx,
            NullLogger<MentionFrequencyReranker>.Instance,
            Options.Create(new MemoryOptions { MentionFrequencyReranking = enabled }));

    private static MemoryRerankContext Context(MemoryItemKind kind = MemoryItemKind.Fact) =>
        new("where does alice live", [0.1f, 0.2f], MemoryScope.For("alice"), kind);

    private void Mentions(params (string Id, int Count)[] counts) =>
        _tx.ReadAsync(
                Arg.Any<Func<IAsyncQueryRunner, Task<IReadOnlyDictionary<string, int>>>>(),
                Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<string, int>)counts.ToDictionary(
                c => c.Id, c => c.Count, StringComparer.Ordinal));

    // ── the signal's provenance ───────────────────────────────────────────

    [Fact]
    public void TheCounterIsIncrementedByIngestionAndNothingElse()
    {
        // THE constraint. mention_count appears in the upsert paths -- where a new ingestion re-states
        // an existing triple -- and is read by exactly one query. If it ever appears in a retrieval
        // path, the loop is closed and the ranking starts reinforcing itself.
        var upsert = FactQueries.Upsert + FactQueries.UpsertBatch;

        upsert.Should().Contain("f.mention_count      = 1", "a first assertion counts once");
        upsert.Should().Contain("coalesce(f.mention_count, 1) + 1", "a re-assertion counts again");
        FactQueries.MentionCounts.Should().Contain("coalesce(f.mention_count, 1)");
    }

    [Fact]
    public void TheReadAuditIsNeverConsulted()
    {
        // The substitution this task exists to refuse. :MemoryReadAudit records our own retrievals;
        // ranking on it would promote whatever already ranks highly, on evidence we generated.
        FactQueries.MentionCounts.Should().NotContain("MemoryReadAudit");
        FactQueries.MentionCounts.Should().NotContain("read_count");
    }

    [Fact]
    public void APreExistingFactCountsAsMentionedOnce()
    {
        // Facts written before the property existed have no counter. One is the honest reading of an
        // absent count -- it was asserted at least once -- and it leaves their ranking unchanged,
        // since log(1) is 0.
        FactQueries.MentionCounts.Should().Contain("coalesce(f.mention_count, 1)");
    }

    // ── the boost ─────────────────────────────────────────────────────────

    [Fact]
    public async Task AWellAttestedFactOvertakesANearTie()
    {
        // Salience breaks a near-tie: 0.80*(1+0.15*ln 20) = 1.16 beats 0.82*(1+0) = 0.82.
        Mentions(("often", 20), ("once", 1));
        var candidates = new[]
        {
            new MemoryContextRankedItem("once", 0.82, 1, 1),
            new MemoryContextRankedItem("often", 0.80, 2, 2),
        };

        var result = await CreateSut().RerankAsync(candidates, Context());

        result.Select(r => r.ItemId).Should().Equal("often", "once");
    }

    [Fact]
    public async Task ChatterDoesNotBuryAPreciseAnswer()
    {
        // The failure a linear boost would cause. A fact mentioned 50 times must not outrank a much
        // better match: 0.30*(1+0.15*ln 50) = 0.48 stays below 0.95*(1+0) = 0.95. Salience is a
        // tiebreaker among plausible answers, not a substitute for answering the question.
        Mentions(("chatter", 50), ("precise", 1));
        var candidates = new[]
        {
            new MemoryContextRankedItem("precise", 0.95, 1, 1),
            new MemoryContextRankedItem("chatter", 0.30, 2, 2),
        };

        var result = await CreateSut().RerankAsync(candidates, Context());

        result.Select(r => r.ItemId).Should().Equal("precise", "chatter");
    }

    [Fact]
    public async Task TheBoostIsLogarithmicNotLinear()
    {
        // The gap between 1 and 3 mentions is real; between 30 and 32 it is noise. Under a linear
        // boost the 32-mention fact would win here; under a log boost the stronger match holds.
        Mentions(("many", 32), ("fewer", 30));
        var candidates = new[]
        {
            new MemoryContextRankedItem("fewer", 0.90, 1, 1),
            new MemoryContextRankedItem("many", 0.89, 2, 2),
        };

        var result = await CreateSut().RerankAsync(candidates, Context());

        result.Select(r => r.ItemId).Should().Equal("fewer", "many");
    }

    [Fact]
    public async Task ScoresAreNeverRewritten()
    {
        // A reranker reorders. Rewriting the provider's score would corrupt the section diagnostics,
        // which report TopScore from these same items.
        Mentions(("a", 10), ("b", 1));
        var candidates = new[]
        {
            new MemoryContextRankedItem("a", 0.70, 1, 1),
            new MemoryContextRankedItem("b", 0.90, 2, 2),
        };

        var result = await CreateSut().RerankAsync(candidates, Context());

        result.Should().OnlyContain(r => r.Score == 0.70 || r.Score == 0.90);
        result.Select(r => r.ItemId).Should().BeEquivalentTo(["a", "b"]);
    }

    // ── the gates ─────────────────────────────────────────────────────────

    [Fact]
    public void ItIsOffByDefault()
    {
        new MemoryOptions().MentionFrequencyReranking.Should().BeFalse();
        CreateSut(enabled: false).IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task ANonFactSectionIsLeftAloneWithoutQuerying()
    {
        // mention_count is maintained on Fact only. Boosting another kind by a counter nothing
        // increments would be a no-op dressed as a feature -- and would still cost a query.
        var candidates = new[]
        {
            new MemoryContextRankedItem("a", 0.9, 1, 1),
            new MemoryContextRankedItem("b", 0.8, 2, 2),
        };

        var result = await CreateSut().RerankAsync(candidates, Context(MemoryItemKind.Entity));

        result.Select(r => r.ItemId).Should().Equal("a", "b");
        await _tx.DidNotReceive().ReadAsync(
            Arg.Any<Func<IAsyncQueryRunner, Task<IReadOnlyDictionary<string, int>>>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EqualSalienceLeavesTheProviderOrderIntact()
    {
        // Ties break on retrieval rank rather than arbitrarily, so enabling the reranker on a corpus
        // where every fact was asserted once changes nothing at all.
        Mentions(("a", 1), ("b", 1));
        var candidates = new[]
        {
            new MemoryContextRankedItem("a", 0.9, 1, 1),
            new MemoryContextRankedItem("b", 0.9, 2, 2),
        };

        var result = await CreateSut().RerankAsync(candidates, Context());

        result.Select(r => r.ItemId).Should().Equal("a", "b");
    }
}
