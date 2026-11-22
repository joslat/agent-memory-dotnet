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
/// DRT-7 (C-03): a fan-out's searches go out together, at most eight at once, and are merged in today's order. The
/// merge's own behaviour is pinned by RecallFanOutOnPathTests and RecallFanOutMergeTests, which pass unchanged.
/// </summary>
public sealed class FanOutSearchesTogetherTests
{
    private readonly IShortTermMemoryService _shortTerm = Substitute.For<IShortTermMemoryService>();
    private readonly ILongTermMemoryService _longTerm = Substitute.For<ILongTermMemoryService, IScoredLongTermSearch>();
    private readonly IReasoningMemoryService _reasoning = Substitute.For<IReasoningMemoryService>();
    private readonly IEmbeddingOrchestrator _embeddings = Substitute.For<IEmbeddingOrchestrator>();
    private readonly TimeProvider _clock = Substitute.For<TimeProvider>();
    private readonly ISubQueryDeriver _deriver = Substitute.For<ISubQueryDeriver>();
    private IScoredLongTermSearch Scored => (IScoredLongTermSearch)_longTerm;

    private static readonly float[] One = [1f, 0f, 0f, 0f];
    private static readonly float[] Two = [0f, 1f, 0f, 0f];
    private static readonly float[] Three = [0f, 0f, 1f, 0f];

    private static Fact F(string id) => new()
    {
        FactId = id, Subject = "alice", Predicate = "ordered", Object = id, Confidence = 1.0, CreatedAtUtc = DateTimeOffset.UnixEpoch,
    };

    public FanOutSearchesTogetherTests()
    {
        _clock.GetUtcNow().Returns(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));
        _shortTerm.GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<Message>());
        _embeddings.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 0.1f, 0.2f, 0.3f, 0.4f });
        _embeddings.EmbedQueryAsync("leg one", Arg.Any<CancellationToken>()).Returns(One);
        _embeddings.EmbedQueryAsync("leg two", Arg.Any<CancellationToken>()).Returns(Two);
        _embeddings.EmbedQueryAsync("leg three", Arg.Any<CancellationToken>()).Returns(Three);
        _deriver.DeriverId.Returns("det-v1");
        _deriver.DeriveAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new List<RecallSubQuery>
        {
            new() { Affinity = MemoryTypeAffinity.Semantic, QueryText = "leg one" },
            new() { Affinity = MemoryTypeAffinity.Semantic, QueryText = "leg two" },
            new() { Affinity = MemoryTypeAffinity.Semantic, QueryText = "leg three" },
        });
        Scored.SearchEntitiesWithScoresAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<(Entity, double)>());
        Scored.SearchPreferencesWithScoresAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<(Preference, double)>());
    }

    private MemoryContextAssembler CreateSut()
    {
        var options = new MemoryOptions();
        options.FanOut.Enabled = true;
        var policy = new DefaultMemoryIsolationPolicy(Options.Create(new MemoryIsolationOptions()), NullLogger<DefaultMemoryIsolationPolicy>.Instance);
        return new MemoryContextAssembler(_shortTerm, _longTerm, _reasoning, null, _embeddings, _clock,
            Options.Create(options), NullLogger<MemoryContextAssembler>.Instance, policy, null, null, null, null, null, _deriver);
    }

    private static RecallRequest Compound() => new()
    {
        SessionId = "s", Query = "What did I order at the MoMA and what did I order at the Met", Options = RecallOptions.Default,
    };

    private static string Leg(float[] embedding) =>
        embedding.SequenceEqual(One) ? "one" : embedding.SequenceEqual(Two) ? "two" : embedding.SequenceEqual(Three) ? "three" : "monolithic";

    [Fact]
    public async Task The_legs_searches_run_at_once_and_merge_in_the_legs_order()
    {
        var inFlight = 0;
        var mostAtOnce = 0;
        var gate = new object();
        Scored.SearchFactsWithScoresAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(),
                Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var leg = Leg(call.Arg<float[]>());
                lock (gate) mostAtOnce = Math.Max(mostAtOnce, ++inFlight);
                // The first leg answers last: the merge must still put its rows first.
                await Task.Delay(leg switch { "one" => 120, "two" => 60, "three" => 30, _ => 0 });
                lock (gate) inFlight--;
                (Fact, double)[] rows = leg == "monolithic" ? [] : [(F($"from-{leg}"), 0.8)];
                return new ScoredFactSearchResult(rows.Select(r => r.Item1).ToArray(), rows);
            });

        var context = await CreateSut().AssembleContextAsync(Compound(), CancellationToken.None);

        mostAtOnce.Should().BeGreaterThan(1, "the legs' fact searches overlap");
        context.RelevantFacts.Items.Select(f => f.FactId).Should().Equal("from-one", "from-two", "from-three");
    }

    [Fact]
    public async Task Every_search_is_sent_and_the_first_failure_in_the_legs_order_is_thrown()
    {
        var sent = new List<string>();
        Scored.SearchFactsWithScoresAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(),
                Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var leg = Leg(call.Arg<float[]>());
                lock (sent) sent.Add(leg);
                await Task.Delay(leg == "two" ? 80 : 10);
                if (leg is "two" or "three") throw new InvalidOperationException($"leg {leg} failed");
                return new ScoredFactSearchResult(Array.Empty<Fact>(), Array.Empty<(Fact, double)>());
            });

        var act = () => CreateSut().AssembleContextAsync(Compound(), CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("leg two failed",
            "leg two comes before leg three, though leg three failed first");
        sent.Should().Contain(["one", "two", "three"], "every search is sent, where the first failure used to stop the loop");
    }
}
