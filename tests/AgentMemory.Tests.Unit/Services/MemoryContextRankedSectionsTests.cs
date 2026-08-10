using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// RankedItems coverage for the four sections that used to drop their scores (entities, preferences,
/// facts, traces) alongside the messages section that already had them. Without a per-section score
/// nothing downstream can tell a weak retrieval from a strong one, so these tests pin both halves of the
/// contract: populated when <see cref="RecallOptions.IncludeDiagnostics"/> is on, and the scored contract
/// not even consulted when it is off.
/// </summary>
public sealed class MemoryContextRankedSectionsTests
{
    private readonly IShortTermMemoryService _shortTerm = Substitute.For<IShortTermMemoryService, IScoredMessageSearch>();
    private readonly ILongTermMemoryService _longTerm = Substitute.For<ILongTermMemoryService, IScoredLongTermSearch>();
    private readonly IReasoningMemoryService _reasoning = Substitute.For<IReasoningMemoryService, IScoredTraceSearch>();
    private readonly IEmbeddingOrchestrator _embeddings = Substitute.For<IEmbeddingOrchestrator>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly DateTimeOffset _fixedTime = new(2025, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private IScoredMessageSearch ScoredMessages => (IScoredMessageSearch)_shortTerm;
    private IScoredLongTermSearch ScoredLongTerm => (IScoredLongTermSearch)_longTerm;
    private IScoredTraceSearch ScoredTraces => (IScoredTraceSearch)_reasoning;

    public MemoryContextRankedSectionsTests()
    {
        _clock.UtcNow.Returns(_fixedTime);
        SetupUnscoredReturns();
        SetupScoredReturns();
    }

    [Fact]
    public async Task IncludeDiagnostics_PopulatesRankedItemsForEverySection()
    {
        var sut = CreateSut();

        var context = await sut.AssembleContextAsync(Request(includeDiagnostics: true));

        context.RelevantMessages.RankedItems.Should().Equal(
            new MemoryContextRankedItem("message-1", 0.91, RetrievalRank: 1, ContextRank: 1),
            new MemoryContextRankedItem("message-2", 0.81, RetrievalRank: 2, ContextRank: 2));
        context.RelevantEntities.RankedItems.Should().Equal(
            new MemoryContextRankedItem("entity-1", 0.92, RetrievalRank: 1, ContextRank: 1),
            new MemoryContextRankedItem("entity-2", 0.82, RetrievalRank: 2, ContextRank: 2));
        context.RelevantPreferences.RankedItems.Should().Equal(
            new MemoryContextRankedItem("pref-1", 0.93, RetrievalRank: 1, ContextRank: 1),
            new MemoryContextRankedItem("pref-2", 0.83, RetrievalRank: 2, ContextRank: 2));
        context.RelevantFacts.RankedItems.Should().Equal(
            new MemoryContextRankedItem("fact-1", 0.94, RetrievalRank: 1, ContextRank: 1),
            new MemoryContextRankedItem("fact-2", 0.84, RetrievalRank: 2, ContextRank: 2));
        context.SimilarTraces.RankedItems.Should().Equal(
            new MemoryContextRankedItem("trace-1", 0.95, RetrievalRank: 1, ContextRank: 1),
            new MemoryContextRankedItem("trace-2", 0.85, RetrievalRank: 2, ContextRank: 2));
    }

    [Fact]
    public async Task IncludeDiagnostics_LeavesSectionItemsIdenticalToTheUndiagnosedRecall()
    {
        var sut = CreateSut();

        var plain = await sut.AssembleContextAsync(Request(includeDiagnostics: false));
        var diagnosed = await sut.AssembleContextAsync(Request(includeDiagnostics: true));

        // Compared by identity rather than by record equality: the domain records carry collection
        // members, which records compare by reference, so two equal-valued fixtures never compare equal.
        diagnosed.RelevantEntities.Items.Select(e => e.EntityId)
            .Should().Equal(plain.RelevantEntities.Items.Select(e => e.EntityId));
        diagnosed.RelevantPreferences.Items.Select(p => p.PreferenceId)
            .Should().Equal(plain.RelevantPreferences.Items.Select(p => p.PreferenceId));
        diagnosed.RelevantFacts.Items.Select(f => f.FactId)
            .Should().Equal(plain.RelevantFacts.Items.Select(f => f.FactId));
        diagnosed.SimilarTraces.Items.Select(t => t.TraceId)
            .Should().Equal(plain.SimilarTraces.Items.Select(t => t.TraceId));
        plain.RelevantEntities.Items.Should().NotBeEmpty("the comparison would otherwise be vacuous");
    }

    [Fact]
    public async Task DiagnosticsOff_LeavesEveryRankedItemsEmptyAndNeverCallsTheScoredContract()
    {
        var sut = CreateSut();

        var context = await sut.AssembleContextAsync(Request(includeDiagnostics: false));

        context.RelevantMessages.RankedItems.Should().BeEmpty();
        context.RelevantEntities.RankedItems.Should().BeEmpty();
        context.RelevantPreferences.RankedItems.Should().BeEmpty();
        context.RelevantFacts.RankedItems.Should().BeEmpty();
        context.SimilarTraces.RankedItems.Should().BeEmpty();

        await ScoredLongTerm.DidNotReceive().SearchEntitiesWithScoresAsync(
            Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        await ScoredLongTerm.DidNotReceive().SearchPreferencesWithScoresAsync(
            Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        await ScoredLongTerm.DidNotReceive().SearchFactsWithScoresAsync(
            Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<bool>(),
            Arg.Any<int>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        await ScoredTraces.DidNotReceive().SearchSimilarTracesWithScoresAsync(
            Arg.Any<float[]>(), Arg.Any<bool?>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());

        // The unscored searches are still the ones that ran — the default path is untouched.
        await _longTerm.Received(1).SearchEntitiesAsync(
            Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        await _reasoning.Received(1).SearchSimilarTracesAsync(
            Arg.Any<float[]>(), Arg.Any<bool?>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IncludeDiagnostics_IssuesOneRetrievalPerSectionNotTwo()
    {
        var sut = CreateSut();

        await sut.AssembleContextAsync(Request(includeDiagnostics: true));

        await _longTerm.DidNotReceive().SearchEntitiesAsync(
            Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        await _longTerm.DidNotReceive().SearchPreferencesAsync(
            Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        await _reasoning.DidNotReceive().SearchSimilarTracesAsync(
            Arg.Any<float[]>(), Arg.Any<bool?>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        await ScoredLongTerm.Received(1).SearchEntitiesWithScoresAsync(
            Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IncludeDiagnostics_ExpandedFactWithoutAScoreGetsNoRankedEntry()
    {
        // Predicate expansion appends facts fetched by predicate rather than by similarity: they belong in
        // Items but have no comparable score, so they must be ABSENT from RankedItems rather than carry a
        // fabricated one.
        ScoredLongTerm
            .SearchFactsWithScoresAsync(
                Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<bool>(),
                Arg.Any<int>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ScoredFactSearchResult(
                [NewFact("fact-1"), NewFact("fact-expanded")],
                [(NewFact("fact-1"), 0.94)])));
        var sut = CreateSut();

        var context = await sut.AssembleContextAsync(Request(includeDiagnostics: true));

        context.RelevantFacts.Items.Select(f => f.FactId).Should().Equal("fact-1", "fact-expanded");
        context.RelevantFacts.RankedItems.Should().Equal(
            new MemoryContextRankedItem("fact-1", 0.94, RetrievalRank: 1, ContextRank: 1));
    }

    [Fact]
    public async Task IncludeDiagnostics_ServiceWithoutTheScoredContract_LeavesRankedItemsEmpty()
    {
        // A custom ILongTermMemoryService cannot be asked for scores, and an unscoreable section must
        // report an EMPTY RankedItems rather than a placeholder score. Its Items must still arrive.
        var plainLongTerm = Substitute.For<ILongTermMemoryService>();
        plainLongTerm
            .SearchEntitiesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>([NewEntity("entity-1")]));
        plainLongTerm
            .SearchPreferencesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Preference>>([]));
        plainLongTerm
            .SearchFactsAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([]));
        var sut = CreateSut(plainLongTerm);

        var context = await sut.AssembleContextAsync(Request(includeDiagnostics: true));

        context.RelevantEntities.Items.Select(e => e.EntityId).Should().Equal("entity-1");
        context.RelevantEntities.RankedItems.Should().BeEmpty();
        context.RelevantFacts.RankedItems.Should().BeEmpty();
        context.RelevantPreferences.RankedItems.Should().BeEmpty();
        // The trace service still exposes the contract, so its section is unaffected by the long-term gap.
        context.SimilarTraces.RankedItems.Should().HaveCount(2);
    }

    [Fact]
    public async Task IncludeDiagnostics_ContextRankIsPostBudgetWhileRetrievalRankStaysProviderOrder()
    {
        // A middle item evicted by the budget must widen the RetrievalRank gap (1,3) while ContextRank
        // renumbers over the survivors (1,2) — the two ranks are what separate "the provider never
        // returned it" from "we returned it and then dropped it".
        ScoredLongTerm
            .SearchEntitiesWithScoresAsync(
                Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(Entity Entity, double Score)>>(
            [
                (NewEntity("entity-1"), 0.92),
                (NewEntity("entity-dropped", _fixedTime.AddMinutes(-5)), 0.87),
                (NewEntity("entity-3"), 0.82)
            ]));
        // Entities only, so the global victim search can pick nothing else: 8+10 + 14+10 + 8+10 = 60 chars,
        // and evicting the single oldest (24) lands on 36.
        var options = Options.Create(new MemoryOptions { ContextBudget = new ContextBudget { MaxCharacters = 40 } });
        var request = Request(includeDiagnostics: true, RecallOptions.Default with
        {
            IncludeDiagnostics = true,
            MaxRecentMessages = 0,
            MaxRelevantMessages = 0,
            MaxPreferences = 0,
            MaxFacts = 0,
            MaxTraces = 0
        });
        var sut = CreateSut(options: options);

        var context = await sut.AssembleContextAsync(request);

        context.Truncated.Should().BeTrue();
        context.RelevantEntities.Items.Select(e => e.EntityId).Should().Equal("entity-1", "entity-3");
        context.RelevantEntities.RankedItems.Should().Equal(
            new MemoryContextRankedItem("entity-1", 0.92, RetrievalRank: 1, ContextRank: 1),
            new MemoryContextRankedItem("entity-3", 0.82, RetrievalRank: 3, ContextRank: 2));
    }

    [Fact]
    public async Task AsOf_IncludeDiagnostics_PopulatesRankedItemsForEveryScoredSection()
    {
        // Bitemporal recall assembles the same sections from a second code path; the instrument has to
        // reach both. (RelevantMessages is structurally empty here — the temporal snapshot has no
        // semantic message search — so it stays empty, not unscored.)
        var sut = CreateSut();

        var context = await sut.AssembleContextAsOfAsync(Request(includeDiagnostics: true), _fixedTime);

        context.RelevantEntities.RankedItems.Should().Equal(
            new MemoryContextRankedItem("entity-1", 0.62, RetrievalRank: 1, ContextRank: 1),
            new MemoryContextRankedItem("entity-2", 0.52, RetrievalRank: 2, ContextRank: 2));
        context.RelevantPreferences.RankedItems.Should().Equal(
            new MemoryContextRankedItem("pref-1", 0.63, RetrievalRank: 1, ContextRank: 1));
        context.RelevantFacts.RankedItems.Should().Equal(
            new MemoryContextRankedItem("fact-1", 0.64, RetrievalRank: 1, ContextRank: 1));
        context.SimilarTraces.RankedItems.Should().Equal(
            new MemoryContextRankedItem("trace-1", 0.65, RetrievalRank: 1, ContextRank: 1));
        context.RelevantMessages.RankedItems.Should().BeEmpty();
    }

    [Fact]
    public async Task AsOf_DiagnosticsOff_LeavesEveryRankedItemsEmptyAndNeverCallsTheScoredContract()
    {
        var sut = CreateSut();

        var context = await sut.AssembleContextAsOfAsync(Request(includeDiagnostics: false), _fixedTime);

        context.RelevantEntities.RankedItems.Should().BeEmpty();
        context.RelevantPreferences.RankedItems.Should().BeEmpty();
        context.RelevantFacts.RankedItems.Should().BeEmpty();
        context.SimilarTraces.RankedItems.Should().BeEmpty();
        context.RelevantEntities.Items.Should().NotBeEmpty("the default path must still return its items");

        await ScoredLongTerm.DidNotReceive().SearchEntitiesAsOfWithScoresAsync(
            Arg.Any<float[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        await ScoredLongTerm.DidNotReceive().SearchPreferencesAsOfWithScoresAsync(
            Arg.Any<float[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        await ScoredLongTerm.DidNotReceive().SearchFactsAsOfWithScoresAsync(
            Arg.Any<float[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
        await ScoredTraces.DidNotReceive().SearchSimilarTracesAsOfWithScoresAsync(
            Arg.Any<float[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<bool?>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    // ---- Setup ----

    private MemoryContextAssembler CreateSut(
        ILongTermMemoryService? longTerm = null,
        IOptions<MemoryOptions>? options = null) =>
        new(_shortTerm, longTerm ?? _longTerm, _reasoning, graphRag: null, _embeddings, _clock,
            options ?? Options.Create(new MemoryOptions()),
            NullLogger<MemoryContextAssembler>.Instance,
            new DefaultMemoryIsolationPolicy(
                Options.Create(new MemoryIsolationOptions()),
                NullLogger<DefaultMemoryIsolationPolicy>.Instance));

    private static RecallRequest Request(bool includeDiagnostics, RecallOptions? options = null) => new()
    {
        SessionId = "session-1",
        Query = "what do I know?",
        QueryEmbedding = new float[1536],
        Options = options ?? RecallOptions.Default with { IncludeDiagnostics = includeDiagnostics }
    };

    private void SetupUnscoredReturns()
    {
        _shortTerm
            .GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>([]));
        _shortTerm
            .SearchMessagesAsync(Arg.Any<string?>(), Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>([NewMessage("message-1"), NewMessage("message-2")]));
        _longTerm
            .SearchEntitiesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>([NewEntity("entity-1"), NewEntity("entity-2")]));
        _longTerm
            .SearchPreferencesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Preference>>([NewPreference("pref-1"), NewPreference("pref-2")]));
        _longTerm
            .SearchFactsAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([NewFact("fact-1"), NewFact("fact-2")]));
        _reasoning
            .SearchSimilarTracesAsync(Arg.Any<float[]>(), Arg.Any<bool?>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReasoningTrace>>([NewTrace("trace-1"), NewTrace("trace-2")]));

        _shortTerm
            .GetRecentMessagesAsOfAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>([]));
        _longTerm
            .SearchEntitiesAsOfAsync(Arg.Any<float[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>([NewEntity("entity-1"), NewEntity("entity-2")]));
        _longTerm
            .SearchPreferencesAsOfAsync(Arg.Any<float[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Preference>>([NewPreference("pref-1")]));
        _longTerm
            .SearchFactsAsOfAsync(Arg.Any<float[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([NewFact("fact-1")]));
        _reasoning
            .SearchSimilarTracesAsOfAsync(Arg.Any<float[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<bool?>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReasoningTrace>>([NewTrace("trace-1")]));
    }

    private void SetupScoredReturns()
    {
        ScoredMessages
            .SearchMessagesWithScoresAsync(Arg.Any<string?>(), Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(Message Message, double Score)>>(
                [(NewMessage("message-1"), 0.91), (NewMessage("message-2"), 0.81)]));
        ScoredLongTerm
            .SearchEntitiesWithScoresAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(Entity Entity, double Score)>>(
                [(NewEntity("entity-1"), 0.92), (NewEntity("entity-2"), 0.82)]));
        ScoredLongTerm
            .SearchPreferencesWithScoresAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(Preference Preference, double Score)>>(
                [(NewPreference("pref-1"), 0.93), (NewPreference("pref-2"), 0.83)]));
        ScoredLongTerm
            .SearchFactsWithScoresAsync(
                Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<bool>(),
                Arg.Any<int>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ScoredFactSearchResult(
                [NewFact("fact-1"), NewFact("fact-2")],
                [(NewFact("fact-1"), 0.94), (NewFact("fact-2"), 0.84)])));
        ScoredTraces
            .SearchSimilarTracesWithScoresAsync(
                Arg.Any<float[]>(), Arg.Any<bool?>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(ReasoningTrace Trace, double Score)>>(
                [(NewTrace("trace-1"), 0.95), (NewTrace("trace-2"), 0.85)]));

        // Distinct scores from the live path so a test cannot pass by reading the wrong recall path.
        ScoredLongTerm
            .SearchEntitiesAsOfWithScoresAsync(
                Arg.Any<float[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(Entity Entity, double Score)>>(
                [(NewEntity("entity-1"), 0.62), (NewEntity("entity-2"), 0.52)]));
        ScoredLongTerm
            .SearchPreferencesAsOfWithScoresAsync(
                Arg.Any<float[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(Preference Preference, double Score)>>(
                [(NewPreference("pref-1"), 0.63)]));
        ScoredLongTerm
            .SearchFactsAsOfWithScoresAsync(
                Arg.Any<float[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(Fact Fact, double Score)>>(
                [(NewFact("fact-1"), 0.64)]));
        ScoredTraces
            .SearchSimilarTracesAsOfWithScoresAsync(
                Arg.Any<float[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<bool?>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(ReasoningTrace Trace, double Score)>>(
                [(NewTrace("trace-1"), 0.65)]));
    }

    // ---- Fixtures ----

    private Message NewMessage(string id) => new()
    {
        MessageId = id,
        ConversationId = "conv-1",
        SessionId = "session-1",
        Role = "user",
        Content = id,
        TimestampUtc = _fixedTime
    };

    private Entity NewEntity(string id, DateTimeOffset? createdAt = null) => new()
    {
        EntityId = id,
        Name = id,
        Type = "Person",
        Confidence = 1.0,
        CreatedAtUtc = createdAt ?? _fixedTime
    };

    private Preference NewPreference(string id) => new()
    {
        PreferenceId = id,
        Category = "general",
        PreferenceText = id,
        Confidence = 1.0,
        CreatedAtUtc = _fixedTime
    };

    private Fact NewFact(string id) => new()
    {
        FactId = id,
        Subject = id,
        Predicate = "is",
        Object = id,
        Confidence = 1.0,
        CreatedAtUtc = _fixedTime
    };

    private ReasoningTrace NewTrace(string id) => new()
    {
        TraceId = id,
        SessionId = "session-1",
        Task = id,
        StartedAtUtc = _fixedTime
    };
}
