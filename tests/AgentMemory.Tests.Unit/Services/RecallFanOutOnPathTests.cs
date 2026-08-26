using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Memory;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// Fan-out with the retrieval path actually LIVE (30.10, audit finding R7).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why every previous fan-out test was blind.</b> They mocked <c>ILongTermMemoryService</c> alone.
/// The assembler reaches retrieval through an internal <c>IScoredLongTermSearch</c> cast, which is
/// null for such a mock — so the enabled merge path never executed with a non-zero yield, in any test,
/// ever. Three separate defects (R1 cross-leg accumulation, R2 expansion deletion, R4 the witness
/// counting one quantity twice) passed the whole suite because of it.
/// </para>
/// <para>
/// The fix is one word — <c>Substitute.For&lt;ILongTermMemoryService, IScoredLongTermSearch&gt;</c> —
/// and it is the sibling pattern <c>MemoryContextRankedSectionsTests</c> has used all along.
/// </para>
/// </remarks>
public sealed class RecallFanOutOnPathTests
{
    private readonly IShortTermMemoryService _shortTerm = Substitute.For<IShortTermMemoryService>();
    private readonly ILongTermMemoryService _longTerm =
        Substitute.For<ILongTermMemoryService, IScoredLongTermSearch>();
    private readonly IReasoningMemoryService _reasoning = Substitute.For<IReasoningMemoryService>();
    private readonly IEmbeddingOrchestrator _embeddings = Substitute.For<IEmbeddingOrchestrator>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly ISubQueryDeriver _deriver = Substitute.For<ISubQueryDeriver>();

    private IScoredLongTermSearch Scored => (IScoredLongTermSearch)_longTerm;

    private static readonly IMemoryIsolationPolicy SingleTenantPolicy =
        new DefaultMemoryIsolationPolicy(
            Options.Create(new MemoryIsolationOptions()),
            NullLogger<DefaultMemoryIsolationPolicy>.Instance);

    private static Fact F(string id, string obj) => new()
    {
        FactId = id, Subject = "alice", Predicate = "ordered", Object = obj,
        Confidence = 1.0, CreatedAtUtc = DateTimeOffset.UnixEpoch,
    };

    public RecallFanOutOnPathTests()
    {
        _clock.UtcNow.Returns(new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero));
        _shortTerm.GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Message>());
        _embeddings.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f, 0.2f, 0.3f, 0.4f });
        _deriver.DeriverId.Returns("det-v1");

        // Empty defaults so a section only returns what a test explicitly arranges.
        Scored.SearchEntitiesWithScoresAsync(
                Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(),
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<(Entity, double)>());
        Scored.SearchPreferencesWithScoresAsync(
                Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(),
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<(Preference, double)>());
    }

    private MemoryContextAssembler CreateSut(ContextBudget? budget = null)
    {
        var options = new MemoryOptions();
        options.FanOut.Enabled = true;
        if (budget is not null) options = options with { ContextBudget = budget };

        return new MemoryContextAssembler(
            _shortTerm, _longTerm, _reasoning, null, _embeddings, _clock,
            Options.Create(options), NullLogger<MemoryContextAssembler>.Instance,
            SingleTenantPolicy, null, null, null, null, null, _deriver);
    }

    private static RecallRequest Compound() => new()
    {
        SessionId = "s",
        Query = "What did I order at the MoMA and what did I order at the Met",
        Options = RecallOptions.Default,
    };

    /// <summary>Arranges the monolithic fact result and one distinct result per leg embedding.</summary>
    private void ArrangeFacts(
        IReadOnlyList<(Fact, double)> monolithic,
        IReadOnlyList<(Fact, double)> legOne,
        IReadOnlyList<(Fact, double)> legTwo)
    {
        float[] one = [1f, 0f, 0f, 0f];
        float[] two = [0f, 1f, 0f, 0f];

        _embeddings.EmbedQueryAsync("leg one", Arg.Any<CancellationToken>()).Returns(one);
        _embeddings.EmbedQueryAsync("leg two", Arg.Any<CancellationToken>()).Returns(two);

        _deriver.DeriveAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<RecallSubQuery>
            {
                new() { Affinity = MemoryTypeAffinity.Semantic, QueryText = "leg one" },
                new() { Affinity = MemoryTypeAffinity.Semantic, QueryText = "leg two" },
            });

        Scored.SearchFactsWithScoresAsync(
                Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(),
                Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var embedding = call.Arg<float[]>();
                var rows =
                    embedding.SequenceEqual(one) ? legOne :
                    embedding.SequenceEqual(two) ? legTwo :
                    monolithic;
                return new ScoredFactSearchResult(rows.Select(r => r.Item1).ToArray(), rows);
            });
    }

    // ── R1: contributions must accumulate across legs ───────────────────

    [Fact]
    public async Task EveryLegsContributionSurvives_NotJustTheLast()
    {
        // R1. Each leg's merge was fed the ORIGINAL monolithic scored list, so all but the final
        // leg's contributions were silently discarded while the accumulator variables were dutifully
        // written and never effectively read.
        ArrangeFacts(
            monolithic: [(F("m1", "monolithic"), 0.90)],
            legOne: [(F("L1", "from leg one"), 0.80)],
            legTwo: [(F("L2", "from leg two"), 0.70)]);

        var context = await CreateSut().AssembleContextAsync(Compound(), CancellationToken.None);

        var ids = context.RelevantFacts.Items.Select(f => f.FactId).ToArray();
        ids.Should().Contain("m1");
        ids.Should().Contain("L1", "leg one's contribution must not be discarded by leg two's merge");
        ids.Should().Contain("L2");
    }

    // ── R4: the witness reports two DIFFERENT quantities ────────────────

    [Fact]
    public async Task SurvivedBudgetIsMeasuredAfterTheBudget_NotPredictedBeforeIt()
    {
        // R4. UniqueContributions and SurvivedBudget were assigned the same variable, both computed
        // before ApplyBudget ran -- one pre-budget prediction reported as two measurements. The
        // ship/no-ship metric reads these, so as built it would over-count successes.
        ArrangeFacts(
            monolithic: [(F("m1", "monolithic"), 0.99)],
            legOne: [(F("L1", "leg one"), 0.80)],
            legTwo: []);

        var context = await CreateSut().AssembleContextAsync(Compound(), CancellationToken.None);
        var report = context.FanOutReport;

        report.Should().NotBeNull();
        var yields = report!.SubQueries;
        yields.Should().NotBeEmpty();

        // Every surviving id must actually be present in the section the caller reads. This is the
        // claim the two counts exist to support, and it is checkable without knowing the budget.
        var present = context.RelevantFacts.Items.Select(f => f.FactId).ToHashSet(StringComparer.Ordinal);
        foreach (var y in yields)
        {
            y.SurvivedBudget.Should().BeLessThanOrEqualTo(y.UniqueContributions,
                "survivors are a subset of contributions, never more");
        }

        yields.Sum(y => y.SurvivedBudget).Should().BeLessThanOrEqualTo(present.Count,
            "a survivor that is not in the section did not survive");
    }

    // ── R2: predicate expansion must not be deleted ─────────────────────

    [Fact]
    public async Task AZeroYieldLegDoesNotShrinkTheSection()
    {
        // R2. Facts carry an unscored predicate-expansion tail by design (Items superset of Scored).
        // The merge's monolithic arm was the SCORE list, so firing fan-out deleted every expanded
        // fact -- a leg that found nothing made the context smaller.
        float[] legEmbedding = [1f, 0f, 0f, 0f];
        _embeddings.EmbedQueryAsync("leg one", Arg.Any<CancellationToken>()).Returns(legEmbedding);
        _deriver.DeriveAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<RecallSubQuery>
            {
                new() { Affinity = MemoryTypeAffinity.Semantic, QueryText = "leg one" },
            });

        // Monolithic: two scored facts plus one UNSCORED expansion row.
        var scored = new List<(Fact, double)> { (F("s1", "scored one"), 0.9), (F("s2", "scored two"), 0.8) };
        var items = new List<Fact> { F("s1", "scored one"), F("s2", "scored two"), F("x1", "expanded") };

        Scored.SearchFactsWithScoresAsync(
                Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(),
                Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<float[]>().SequenceEqual(legEmbedding)
                ? new ScoredFactSearchResult(Array.Empty<Fact>(), Array.Empty<(Fact, double)>())
                : new ScoredFactSearchResult(items, scored));

        var context = await CreateSut().AssembleContextAsync(Compound(), CancellationToken.None);

        context.RelevantFacts.Items.Select(f => f.FactId)
            .Should().Contain("x1",
                "the unscored expansion tail must survive a fan-out that contributed nothing");
    }

    [Fact]
    public async Task WhenTheBudgetTruncates_SurvivedBudgetIsSmallerThanUniqueContributions()
    {
        // R4, verified empirically rather than by construction. The previous code assigned both counts
        // the same PRE-budget variable, so no budget however small could ever separate them -- which is
        // exactly why the defect was invisible: the two numbers agreed by definition.
        //
        // Here a leg contributes real rows and a deliberately tiny character budget then cuts most of
        // them. If the fix is real the two counts must diverge.
        ArrangeFacts(
            monolithic: [(F("m1", "monolithic row with a reasonably long body of text"), 0.99)],
            legOne:
            [
                (F("L1", "leg row one with a reasonably long body of text"), 0.98),
                (F("L2", "leg row two with a reasonably long body of text"), 0.97),
                (F("L3", "leg row three with a reasonably long body of text"), 0.96),
            ],
            legTwo: []);

        var tight = new ContextBudget { MaxCharacters = 120 };
        var context = await CreateSut(tight).AssembleContextAsync(Compound(), CancellationToken.None);

        var report = context.FanOutReport;
        report.Should().NotBeNull();

        var contributed = report!.SubQueries.Sum(y => y.UniqueContributions);
        var survived = report.SubQueries.Sum(y => y.SurvivedBudget);

        contributed.Should().BeGreaterThan(0, "the leg genuinely found rows the monolithic query missed");
        survived.Should().BeLessThan(contributed,
            "a budget this tight must cut some of them, and SurvivedBudget is what remains AFTER it");

        // And the survivors must actually be present, not merely counted.
        var present = context.RelevantFacts.Items.Select(f => f.FactId).ToHashSet(StringComparer.Ordinal);
        survived.Should().BeLessThanOrEqualTo(present.Count);
    }
}
