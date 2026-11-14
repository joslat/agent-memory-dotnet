using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using AgentMemory.Core.Services.Budgeting;
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
    private readonly TimeProvider _clock = Substitute.For<TimeProvider>();
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
        _clock.GetUtcNow().Returns(Now);
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

    private MemoryContextAssembler Sut(MemoryOptions? options = null) =>
        new(_shortTerm, _longTerm, _reasoning, null, _embeddings, _clock,
            Options.Create(options ?? new MemoryOptions()),
            NullLogger<MemoryContextAssembler>.Instance, Policy,
            rankingContext: null, truncationStrategies: null, rerankers: null,
            projectionFeatures: null);

    private Task<MemoryContext> AskAsync(string question, RecallOptions options) =>
        Sut().AssembleContextAsync(new RecallRequest
        {
            SessionId = "s1", Query = question, Question = question, QueryEmbedding = new float[8], Options = options,
        }, CancellationToken.None);

    [Fact]
    public async Task Event_details_are_not_called_while_off_even_with_dated_facts()
    {
        _relevant = [GoLive];
        await AskAsync("Plan my prep for go-live week.", AllOn);
        await _longTerm.DidNotReceiveWithAnyArgs().GetEventDetailsAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task Dated_window_reaches_undated_details_once_without_duplicate_dates_or_fake_scores()
    {
        var location = Mate with { FactId = "location", Subject = "Acme go-live", Predicate = "happens at", Object = "Córdoba" };
        var presence = Mate with { FactId = "presence", Subject = "Acme go-live", Predicate = "requires", Object = "in person" };
        _longTerm.GetEventDetailsAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyCollection<string>>(),
            Arg.Any<int>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Fact>>([location, presence, GoLive]));
        var context = await AskAsync("Plan my prep for go-live week.", AllOn with { EventDetails = true, MaxFacts = 8, MaxEventDetailFacts = 8 });
        context.RelevantFacts.Items.Select(f => f.FactId).Should().Equal("location", "presence");
        context.WindowFacts.Items.Should().Contain(f => f.FactId == GoLive.FactId);
        await _longTerm.Received(1).GetEventDetailsAsync(
            Arg.Is<IReadOnlyList<string>>(ids => ids.Contains("go-live")),
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.Contains("go-live") && ids.Contains("bariloche")),
            8, Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Event_details_need_a_dated_seed_and_respect_zero_fact_budget()
    {
        _relevant = [Mate];
        await AskAsync("What do I drink?", RecallOptions.Default with { EventDetails = true });
        await AskAsync("Plan my prep for go-live week.", AllOn with { EventDetails = true, MaxFacts = 0 });
        await _longTerm.DidNotReceiveWithAnyArgs().GetEventDetailsAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task Event_details_share_the_existing_context_budget()
    {
        var detail = Mate with { FactId = "long-detail", Subject = "Acme go-live", Object = new string('x', 4000) };
        _longTerm.GetEventDetailsAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyCollection<string>>(),
            Arg.Any<int>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Fact>>([detail]));
        var room = ContextBudgetEstimator.EstimateChars(new[] { Bariloche, GoLive });
        var context = await Sut(new MemoryOptions { ContextBudget = new ContextBudget { MaxCharacters = room } })
            .AssembleContextAsync(new() { SessionId = "s1", Query = "Plan my prep for go-live week.",
                QueryEmbedding = new float[8], Options = AllOn with { EventDetails = true } });
        context.RelevantFacts.Items.Should().NotContain(f => f.FactId == detail.FactId);
        context.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task Point_in_time_recall_refuses_live_identity_details_before_any_service_read()
    {
        Func<Task> action = async () => { await Sut().AssembleContextAsOfAsync(new() { SessionId = "s1", Query = "go-live",
            QueryEmbedding = new float[8], Options = RecallOptions.Default with { EventDetails = true } }, Now); };
        await action.Should().ThrowAsync<NotSupportedException>().WithMessage("*live recall only*");
        _longTerm.ReceivedCalls().Should().BeEmpty(); _shortTerm.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task The_asked_event_anchor_survives_a_full_dated_seed_budget()
    {
        _relevant = Enumerable.Range(0, 12).Select(i => GoLive with { FactId = "unrelated-dated-" + i }).ToArray();
        _longTerm.GetEventDetailsAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyCollection<string>>(),
            Arg.Any<int>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Fact>>([]));
        await AskAsync("Plan my prep for go-live week.", AllOn with { EventDetails = true, MaxFacts = 20 });
        await _longTerm.Received(1).GetEventDetailsAsync(Arg.Is<IReadOnlyList<string>>(ids =>
            ids.Count == 10 && ids[0] == "go-live"), Arg.Any<IReadOnlyCollection<string>>(), 8,
            Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    private static RecallOptions AllOn => RecallOptions.Default with { Upcoming = true, TimeWindows = true, OverlapExpansion = true };

    // ── ILC-33: person details ────────────────────────────────────────

    private static readonly Fact Monitoring = Mate with { FactId = "monitoring", Subject = "Valentina", Predicate = "will monitor", Object = "the invoice queue remotely" };
    private static readonly Fact Tutorial = Mate with { FactId = "tutorial", Subject = "Valentina", Predicate = "will prepare", Object = "a video tutorial for the clerks" };

    private void People(params PersonIdentity[] people) =>
        _longTerm.GetPersonIdentitiesAsync(Arg.Any<int>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PersonIdentity>>(people));

    private static PersonIdentity Person(string id, params string[] names) => new() { EntityId = id, Surfaces = names };

    [Fact]
    public async Task A_question_naming_a_confirmed_person_brings_their_facts_once_without_fake_scores()
    {
        _relevant = [Mate, Tutorial];
        People(Person("valentina", "Valentina"), Person("tomas", "Tomás"));
        _longTerm.GetPersonDetailsAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<int>(),
            Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Fact>>([Monitoring, Tutorial]));
        var context = await AskAsync("I have my mentoring session with Valentina tomorrow.", RecallOptions.Default with { PersonDetails = true });
        context.RelevantFacts.Items.Select(f => f.FactId).Should().OnlyHaveUniqueItems().And.Contain(["mate", "tutorial", "monitoring"]);
        await _longTerm.Received(1).GetPersonDetailsAsync(Arg.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[] { "valentina" })),
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.Contains("mate") && ids.Contains("tutorial")), 8,
            Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_name_two_confirmed_people_share_reads_no_facts()
    {
        People(Person("cousin", "Alex Kim"), Person("colleague", "Alex Kim"));
        await AskAsync("What do I know about Alex Kim?", RecallOptions.Default with { PersonDetails = true });
        await _longTerm.Received(1).GetPersonIdentitiesAsync(1000, Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        await _longTerm.DidNotReceiveWithAnyArgs().GetPersonDetailsAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task A_full_page_of_people_may_hide_a_namesake_so_no_facts_are_read()
    {
        // The read is bounded to 1,000 people ordered by id: when it comes back full, a second Valentina may be past the
        // bound, so "Valentina" is not known to be unique and nobody is expanded.
        People([Person("valentina", "Valentina"), .. Enumerable.Range(1, 999).Select(i => Person($"person-{i:D4}", $"Person {i}"))]);
        await AskAsync("I have my mentoring session with Valentina tomorrow.", RecallOptions.Default with { PersonDetails = true });
        await _longTerm.DidNotReceiveWithAnyArgs().GetPersonDetailsAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task Person_details_are_not_read_while_off_or_with_no_fact_budget()
    {
        People(Person("valentina", "Valentina"));
        await AskAsync("What is Valentina working on?", RecallOptions.Default);
        await AskAsync("What is Valentina working on?", RecallOptions.Default with { PersonDetails = true, MaxFacts = 0 });
        await AskAsync("What is Valentina working on?", RecallOptions.Default with { PersonDetails = true, MaxPersonDetailFacts = 0 });
        await _longTerm.DidNotReceiveWithAnyArgs().GetPersonIdentitiesAsync(default, default, default);
        await _longTerm.DidNotReceiveWithAnyArgs().GetPersonDetailsAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task An_out_of_range_person_cap_is_refused()
    {
        var action = () => AskAsync("What is Valentina working on?", RecallOptions.Default with { PersonDetails = true, MaxPersonDetailFacts = 65 });
        await action.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Point_in_time_recall_refuses_live_person_details_before_any_service_read()
    {
        Func<Task> action = async () => { await Sut().AssembleContextAsOfAsync(new() { SessionId = "s1", Query = "Valentina",
            QueryEmbedding = new float[8], Options = RecallOptions.Default with { PersonDetails = true } }, Now); };
        await action.Should().ThrowAsync<NotSupportedException>().WithMessage("*live recall only*");
        _longTerm.ReceivedCalls().Should().BeEmpty();
    }

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

    // ── review round 1 ───────────────────────────────────────────────

    [Fact]
    public async Task AReplayedQuestionReadsItsWindowAgainstWhenItWasAsked()
    {
        // Asked on Friday 6 November, replayed on 4 December: "next week" is 9 to 15 November.
        var askedOn = new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);
        await Sut().AssembleContextAsync(new RecallRequest
        {
            SessionId = "s1", Query = "what is planned next week?", QueryEmbedding = new float[8], TemporalReferenceTime = askedOn,
            Options = RecallOptions.Default with { TimeWindows = true },
        }, CancellationToken.None);

        await _longTerm.Received(1).GetFactsInWindowAsync(
            new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero).AddTicks(-1),
            10, Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheLookAheadSectionsSpendTheContextBudgetFirst()
    {
        // Room for the window's two facts and nothing more: the relevant fact is cut, and the recall says so.
        _relevant = [Mate];
        var room = ContextBudgetEstimator.EstimateChars(new[] { Bariloche, GoLive });
        var options = new MemoryOptions { ContextBudget = new ContextBudget { MaxCharacters = room } };

        var context = await Sut(options).AssembleContextAsync(new RecallRequest
        {
            SessionId = "s1", Query = "What do I have on my calendar between December 7 and December 13?", QueryEmbedding = new float[8],
            Options = RecallOptions.Default with { TimeWindows = true },
        }, CancellationToken.None);

        context.WindowFacts.Items.Should().HaveCount(2);
        context.RelevantFacts.Items.Should().BeEmpty();
        context.Truncated.Should().BeTrue();
    }

    [Fact]
    public void WhenTheLookAheadSectionsAloneExceedTheBudgetOverlapGoesFirst()
    {
        var lookAhead = new MemoryContextAssembler.LookAheadResult(
            Upcoming: [GoLive], Window: [Bariloche], Overlapping: [Mate], ResolvedWindow: null,
            FiredUpcoming: true, FiredWindow: true, FiredOverlap: true, Ids: new HashSet<string>(StringComparer.Ordinal));
        var room = ContextBudgetEstimator.EstimateChars(new[] { GoLive, Bariloche });

        var (fitted, trimmed) = MemoryContextAssembler.FitLookAhead(lookAhead, room);

        trimmed.Should().BeTrue();
        fitted.Overlapping.Should().BeEmpty();
        fitted.Upcoming.Should().ContainSingle();
        fitted.Window.Should().ContainSingle();
    }

    [Fact]
    public void AFactRecalledByDateCountsAsAccessedOnce()
    {
        var context = new MemoryContext
        {
            SessionId = "s1", AssembledAtUtc = Now,
            RelevantFacts = new MemoryContextSection<Fact> { Items = [Mate] },
            WindowFacts = new MemoryContextSection<Fact> { Items = [GoLive, Bariloche] },
            UpcomingFacts = new MemoryContextSection<Fact> { Items = [Bariloche] },
        };

        MemoryService.CollectAccessedNodes(context).Where(n => n.NodeKind == MemoryNodeKind.Fact).Select(n => n.NodeId)
            .Should().BeEquivalentTo(["mate", "go-live", "bariloche"]);
    }

    // ── review round 2 ────────────────────────────────────────────────

    [Fact]
    public async Task UnderTheFailStrategyTheLookAheadSectionsAreNotTrimmedAndTheBudgetFails()
    {
        var room = ContextBudgetEstimator.EstimateChars(new[] { GoLive });
        var options = new MemoryOptions { ContextBudget = new ContextBudget { MaxCharacters = room, TruncationStrategy = TruncationStrategy.Fail } };

        var act = () => Sut(options).AssembleContextAsync(new RecallRequest
        {
            SessionId = "s1", Query = "What do I have on my calendar between December 7 and December 13?", QueryEmbedding = new float[8],
            Options = RecallOptions.Default with { TimeWindows = true },
        }, CancellationToken.None);

        (await act.Should().ThrowAsync<MemoryException>()).Which.Code.Should().Be(MemoryErrorCodes.ContextBudgetExceeded);
    }

    [Fact]
    public async Task AnAnchorDatedOnlyByItsWordsReachesTheWindowSection()
    {
        // The anchor's date came from its words, so the window read (date fields only) cannot return it.
        var spoken = GoLive with { FactId = "spoken-go-live", OccurredOn = null };
        _longTerm.FindAnchorFactAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Fact?>(spoken with { OccurredOn = GoLive.OccurredOn }));
        _longTerm.GetFactsInWindowAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([Bariloche]));

        var context = await AskAsync("Plan my prep for go-live week.", RecallOptions.Default with { TimeWindows = true });

        context.WindowFacts.Items.Select(f => f.FactId).Should().Equal("bariloche", "spoken-go-live");
    }

    [Fact]
    public void AFullWindowMakesRoomForItsAnchor()
    {
        var window = MemoryContextAssembler.WithAnchor([Bariloche, Mate with { OccurredOn = Now }], GoLive, max: 2);

        window.Select(f => f.FactId).Should().Contain("go-live").And.HaveCount(2);
    }

    [Theory]
    [InlineData(DatePrecision.Month, 1, 31)]
    [InlineData(DatePrecision.Year, 1, 365)]
    [InlineData(DatePrecision.Day, 1, 1)]
    public void AnOverlapWindowSpansWhatTheDateStated(DatePrecision precision, int firstDay, int days)
    {
        var fact = Mate with { OccurredOn = new DateTimeOffset(2026, precision == DatePrecision.Year ? 1 : 12, firstDay, 0, 0, 0, TimeSpan.Zero), OccurredOnPrecision = precision };

        var window = MemoryContextAssembler.WindowOf(fact, Now)!;

        (window.EndUtc - window.StartUtc).TotalDays.Should().BeApproximately(days, 0.01);
    }
}
