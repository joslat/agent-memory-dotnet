using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// Look-ahead recall (<c>RecallOptions.Upcoming</c>, <c>TimeWindows</c>, <c>OverlapExpansion</c>) reaches its reads,
/// asks them for the right period, and issues nothing at all when off. The questions are Bruna's, asked on Friday
/// 4 December: her go-live is on Friday 11 December, and she will be in Bariloche from 9 to 13 December.
/// </summary>
public sealed class LookAheadAssemblerTests
{
    private static readonly DateTimeOffset Now = new(2026, 12, 4, 9, 0, 0, TimeSpan.Zero);

    private readonly IShortTermMemoryService _shortTerm = Substitute.For<IShortTermMemoryService>();
    private readonly ILongTermMemoryService _longTerm = Substitute.For<ILongTermMemoryService>();
    private readonly IReasoningMemoryService _reasoning = Substitute.For<IReasoningMemoryService>();
    private readonly IEmbeddingOrchestrator _embeddings = Substitute.For<IEmbeddingOrchestrator>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private IReadOnlyList<Fact> _relevant = [];

    private static readonly IMemoryIsolationPolicy Policy =
        new DefaultMemoryIsolationPolicy(
            Options.Create(new MemoryIsolationOptions()),
            NullLogger<DefaultMemoryIsolationPolicy>.Instance);

    private static readonly Fact GoLive = new()
    {
        FactId = "go-live", Subject = "Bruna", Predicate = "is leading", Object = "the go-live",
        Confidence = 0.9, CreatedAtUtc = new(2026, 9, 7, 10, 0, 0, TimeSpan.Zero),
        OccurredOn = new(2026, 12, 11, 0, 0, 0, TimeSpan.Zero),
    };

    private static readonly Fact Bariloche = new()
    {
        FactId = "bariloche", Subject = "Bruna", Predicate = "will be in", Object = "Bariloche",
        Confidence = 0.9, CreatedAtUtc = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero),
        ValidFrom = new(2026, 12, 9, 0, 0, 0, TimeSpan.Zero), ValidUntil = new(2026, 12, 13, 0, 0, 0, TimeSpan.Zero),
    };

    private static readonly Fact Mate = new()
    {
        FactId = "mate", Subject = "Bruna", Predicate = "drinks", Object = "mate",
        Confidence = 0.9, CreatedAtUtc = new(2026, 9, 21, 10, 0, 0, TimeSpan.Zero),
    };

    public LookAheadAssemblerTests()
    {
        _clock.UtcNow.Returns(Now);
        _embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(new float[8]));
        _embeddings.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(new float[8]));
        _shortTerm.GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>([]));
        _shortTerm.SearchMessagesAsync(Arg.Any<string?>(), Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>([]));
        _longTerm.SearchEntitiesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>([]));
        _longTerm.SearchPreferencesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Preference>>([]));
        _longTerm.SearchFactsAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(_relevant));
        _longTerm.SearchFactsAsync(Arg.Any<float[]>(), Arg.Any<ValidTimeMode>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(_relevant));
        _reasoning.SearchSimilarTracesAsync(Arg.Any<float[]>(), Arg.Any<bool?>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReasoningTrace>>([]));
        _longTerm.GetUpcomingFactsAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([Bariloche, GoLive]));
        _longTerm.GetFactsInWindowAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([Bariloche, GoLive]));
        _longTerm.FindAnchorFactAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Fact?>(GoLive));
        _longTerm.GetOverlappingFactsAsync(Arg.Any<IReadOnlyList<TimeWindow>>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<int>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([Bariloche]));
    }

    private MemoryContextAssembler Sut() =>
        new(_shortTerm, _longTerm, _reasoning, null, _embeddings, _clock,
            Options.Create(new MemoryOptions()),
            NullLogger<MemoryContextAssembler>.Instance, Policy,
            rankingContext: null, truncationStrategies: null, rerankers: null,
            projectionFeatures: null);

    private Task<MemoryContext> AskAsync(string question, RecallOptions options) =>
        Sut().AssembleContextAsync(new RecallRequest
        {
            SessionId = "s1", Query = question, Question = question, QueryEmbedding = new float[8], Options = options,
        }, CancellationToken.None);

    private static RecallOptions AllOn => RecallOptions.Default with { Upcoming = true, TimeWindows = true, OverlapExpansion = true };

    // ── off issues nothing ────────────────────────────────────────────

    [Theory]
    [InlineData("Plan my prep for go-live week.")]
    [InlineData("What do I have on my calendar between December 7 and December 13?")]
    [InlineData("When is the Acme go-live, and where does it happen?")]
    public async Task WithEveryFlagOffNoLookAheadReadIsIssued(string question)
    {
        _relevant = [GoLive];
        var context = await AskAsync(question, RecallOptions.Default with { MaxFacts = 10 });

        await _longTerm.DidNotReceiveWithAnyArgs().GetUpcomingFactsAsync(default, default, default, default, default);
        await _longTerm.DidNotReceiveWithAnyArgs().GetFactsInWindowAsync(default, default, default, default, default);
        await _longTerm.DidNotReceiveWithAnyArgs().FindAnchorFactAsync(default!, default, default, default);
        await _longTerm.DidNotReceiveWithAnyArgs().GetOverlappingFactsAsync(default!, default!, default, default, default);
        context.UpcomingFacts.Items.Should().BeEmpty();
        context.WindowFacts.Items.Should().BeEmpty();
        context.OverlappingFacts.Items.Should().BeEmpty();
        context.ResolvedTimeWindow.Should().BeNull();
        context.RelevantFacts.Items.Should().ContainSingle().Which.FactId.Should().Be("go-live");
    }

    // ── upcoming ──────────────────────────────────────────────────────

    [Fact]
    public async Task AQuestionThatLooksAheadGetsWhatStartsInTheNextTwoWeeks()
    {
        var context = await AskAsync("When is the Acme go-live, and where does it happen?", RecallOptions.Default with { Upcoming = true });

        await _longTerm.Received(1).GetUpcomingFactsAsync(Now, Now.AddDays(14), 5, Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        context.UpcomingFacts.Items.Select(f => f.FactId).Should().Equal("bariloche", "go-live");
    }

    [Fact]
    public async Task AQuestionAboutThePastGetsNoCalendar()
    {
        var context = await AskAsync("What did Tomás find in the tax rounding?", RecallOptions.Default with { Upcoming = true });

        await _longTerm.DidNotReceiveWithAnyArgs().GetUpcomingFactsAsync(default, default, default, default, default);
        context.UpcomingFacts.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task OnEveryRecallItFiresWhateverTheQuestion()
    {
        await AskAsync("What did Tomás find in the tax rounding?", RecallOptions.Default with { Upcoming = true, UpcomingOnEveryRecall = true });

        await _longTerm.ReceivedWithAnyArgs(1).GetUpcomingFactsAsync(default, default, default, default, default);
    }

    // ── time windows ──────────────────────────────────────────────────

    [Fact]
    public async Task ANamedRangeIsReadWholeAndReported()
    {
        var context = await AskAsync("What do I have on my calendar between December 7 and December 13?", RecallOptions.Default with { TimeWindows = true });

        var start = new DateTimeOffset(2026, 12, 7, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 12, 14, 0, 0, 0, TimeSpan.Zero).AddTicks(-1);
        await _longTerm.Received(1).GetFactsInWindowAsync(start, end, 10, Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        context.WindowFacts.Items.Select(f => f.FactId).Should().Equal("bariloche", "go-live");
        context.ResolvedTimeWindow.Should().BeEquivalentTo(new TimeWindow { StartUtc = start, EndUtc = end });
    }

    [Fact]
    public async Task AnAnchoredWeekTakesItsDateFromTheEvent()
    {
        var context = await AskAsync("Plan my prep for go-live week.", RecallOptions.Default with { TimeWindows = true });

        await _longTerm.Received(1).FindAnchorFactAsync("go-live", Now, Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        // The go-live is on Friday 11 December: its week is Monday 7 to Sunday 13, and the trip is inside it.
        await _longTerm.Received(1).GetFactsInWindowAsync(
            new DateTimeOffset(2026, 12, 7, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 12, 14, 0, 0, 0, TimeSpan.Zero).AddTicks(-1),
            10, Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        context.WindowFacts.Items.Select(f => f.FactId).Should().Contain("bariloche");
        context.ResolvedTimeWindow!.Anchor.Should().Be("go-live");
        context.ResolvedTimeWindow.AnchorFactId.Should().Be("go-live");
    }

    [Fact]
    public async Task AnAnchorTheStoreCannotDateGivesNoWindow()
    {
        _longTerm.FindAnchorFactAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Fact?>(null));

        var context = await AskAsync("Plan my prep for go-live week.", RecallOptions.Default with { TimeWindows = true });

        await _longTerm.DidNotReceiveWithAnyArgs().GetFactsInWindowAsync(default, default, default, default, default);
        context.ResolvedTimeWindow.Should().BeNull();
    }

    [Fact]
    public async Task AFactInTheWindowRendersThereOnly()
    {
        _relevant = [GoLive, Mate];

        var context = await AskAsync("Plan my prep for go-live week.", AllOn);

        context.WindowFacts.Items.Select(f => f.FactId).Should().Contain("go-live");
        context.RelevantFacts.Items.Select(f => f.FactId).Should().Equal("mate");
        context.UpcomingFacts.Items.Select(f => f.FactId).Should().NotContain(context.WindowFacts.Items.Select(f => f.FactId));
    }

    // ── overlap ───────────────────────────────────────────────────────

    [Fact]
    public async Task ADatedFactRecalledBringsWhatOverlapsIt()
    {
        _relevant = [GoLive, Mate];

        var context = await AskAsync("When is the Acme go-live, and where does it happen?", RecallOptions.Default with { OverlapExpansion = true });

        // One window: the go-live's day. "Mate" has no date, so it occupies none.
        await _longTerm.Received(1).GetOverlappingFactsAsync(
            Arg.Is<IReadOnlyList<TimeWindow>>(w => w.Count == 1
                && w[0].StartUtc == new DateTimeOffset(2026, 12, 11, 0, 0, 0, TimeSpan.Zero)
                && w[0].EndUtc == new DateTimeOffset(2026, 12, 12, 0, 0, 0, TimeSpan.Zero).AddTicks(-1)),
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.Contains("go-live") && ids.Contains("mate")),
            5, Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        context.OverlappingFacts.Items.Should().ContainSingle().Which.FactId.Should().Be("bariloche");
    }

    [Fact]
    public async Task WithNoDatedFactRecalledOverlapIssuesNothing()
    {
        _relevant = [Mate];

        await AskAsync("what do I drink?", RecallOptions.Default with { OverlapExpansion = true });

        await _longTerm.DidNotReceiveWithAnyArgs().GetOverlappingFactsAsync(default!, default!, default, default, default);
    }

    [Fact]
    public void AFactMerelyValidSinceThePastOccupiesNoWindow()
    {
        var since = Mate with { ValidFrom = Now.AddDays(-30) };

        MemoryContextAssembler.WindowOf(since, Now).Should().BeNull();
        MemoryContextAssembler.WindowOf(Bariloche, Now)!.StartUtc.Should().Be(Bariloche.ValidFrom!.Value);
        MemoryContextAssembler.WindowOf(Mate with { ValidFrom = Now.AddDays(3) }, Now).Should().NotBeNull("a start still ahead is a day");
    }
}
