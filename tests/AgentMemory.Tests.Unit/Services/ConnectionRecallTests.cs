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
/// SPEECH-RELATION-RECALL (<see cref="RecallOptions.MaxConnectionFacts"/>): "Where does my sister live?" recalls
/// "Anna | sister of | user" by similarity; the hop adds Anna's own live facts, which share no word with the question.
/// Found by the speech gate as B26. Off by default, and then nothing is read.
/// </summary>
public sealed class ConnectionRecallTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    private readonly IShortTermMemoryService _shortTerm = Substitute.For<IShortTermMemoryService>();
    private readonly ILongTermMemoryService _longTerm = Substitute.For<ILongTermMemoryService>();
    private readonly IReasoningMemoryService _reasoning = Substitute.For<IReasoningMemoryService>();
    private readonly IEmbeddingOrchestrator _embeddings = Substitute.For<IEmbeddingOrchestrator>();
    private readonly TimeProvider _clock = Substitute.For<TimeProvider>();
    private IReadOnlyList<Fact> _relevant = [];

    private static readonly IMemoryIsolationPolicy Policy =
        new DefaultMemoryIsolationPolicy(Options.Create(new MemoryIsolationOptions()), NullLogger<DefaultMemoryIsolationPolicy>.Instance);

    private static Fact F(string id, string subject, string predicate, string obj, int day) => new()
    {
        FactId = id, Subject = subject, Predicate = predicate, Object = obj, Confidence = 0.9,
        CreatedAtUtc = new DateTimeOffset(2026, 11, day, 10, 0, 0, TimeSpan.Zero),
    };

    private static readonly Fact Sister = F("sister", "Anna", "sister of", "user", 1);
    private static readonly Fact Madrid = F("madrid", "Anna", "lives in", "Madrid", 20);
    private static readonly Fact Lisbon = F("lisbon", "Anna", "lives in", "Lisbon", 2) with { InvalidatedAtUtc = new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero) };
    private static readonly Fact Course = F("course", "Anna", "takes", "a pottery course", 5) with { ValidUntil = new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero) };
    private static readonly Fact Nurse = F("nurse", "Anna", "works as", "a nurse", 10);

    public ConnectionRecallTests()
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
        _longTerm.GetFactsBySubjectAsync("Anna", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([Sister, Lisbon, Course, Nurse, Madrid]));
    }

    private Task<MemoryContext> AskAsync(string question, RecallOptions options) =>
        new MemoryContextAssembler(_shortTerm, _longTerm, _reasoning, null, _embeddings, _clock,
                Options.Create(new MemoryOptions()), NullLogger<MemoryContextAssembler>.Instance, Policy,
                rankingContext: null, truncationStrategies: null, rerankers: null, projectionFeatures: null)
            .AssembleContextAsync(new RecallRequest
            {
                SessionId = "s1", Query = question, Question = question, QueryEmbedding = new float[8], Options = options,
            }, CancellationToken.None);

    private static IEnumerable<string> Ids(MemoryContext context) => context.RelevantFacts.Items.Select(f => f.FactId);

    [Fact]
    public async Task Off_by_default_nothing_is_read_and_recall_is_unchanged()
    {
        _relevant = [Sister];

        var context = await AskAsync("Where does my sister live?", RecallOptions.Default);

        Ids(context).Should().Equal("sister");
        await _longTerm.DidNotReceiveWithAnyArgs().GetFactsBySubjectAsync(default!, default, default);
    }

    [Fact]
    public async Task On_the_question_reaches_the_sisters_live_facts_newest_first_and_not_the_closed_or_ended_ones()
    {
        _relevant = [Sister];

        var context = await AskAsync("Where does my sister live?", RecallOptions.Default with { MaxConnectionFacts = 8 });

        Ids(context).Should().BeEquivalentTo(["sister", "madrid", "nurse"]);
        await _longTerm.Received(1).GetFactsBySubjectAsync("Anna", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_connection_whose_relation_the_question_does_not_name_is_not_followed()
    {
        _relevant = [Sister];

        var context = await AskAsync("What is the weather like in Madrid?", RecallOptions.Default with { MaxConnectionFacts = 8 });

        Ids(context).Should().Equal("sister");
        await _longTerm.DidNotReceiveWithAnyArgs().GetFactsBySubjectAsync(default!, default, default);
    }

    [Fact]
    public async Task The_cap_bounds_what_is_added()
    {
        _relevant = [Sister];

        var context = await AskAsync("How is my sister?", RecallOptions.Default with { MaxConnectionFacts = 1 });

        Ids(context).Should().Equal("sister", "madrid");
    }

    [Fact]
    public async Task The_owner_as_subject_connects_to_the_object_and_a_word_form_still_matches()
    {
        var acme = F("acme", "Acme", "has offices in", "Rosario and Madrid", 3);
        _relevant = [F("works", "user", "works at", "Acme", 1)];
        _longTerm.GetFactsBySubjectAsync("Acme", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([acme]));

        var context = await AskAsync("Where does the place I work have offices?", RecallOptions.Default with { MaxConnectionFacts = 4 });

        Ids(context).Should().Contain("acme");
    }

    [Fact]
    public void A_fact_without_the_owner_or_with_a_long_other_side_is_no_connection()
    {
        var asked = new HashSet<string>(["sister", "live"]);

        MemoryContextAssembler.Connections([F("a", "Anna", "sister of", "Clara", 1)], asked).Should().BeEmpty();
        MemoryContextAssembler.Connections([F("b", "user", "sister of", "the woman I met at the conference last year", 1)], asked).Should().BeEmpty();
        MemoryContextAssembler.Connections([F("c", "user", "sister of", "user", 1)], asked).Should().BeEmpty();
        MemoryContextAssembler.Connections([Sister, Sister with { FactId = "again" }], asked).Should().Equal("Anna");
    }

    [Fact]
    public async Task A_cap_outside_its_range_is_refused()
    {
        _relevant = [Sister];

        var asking = () => AskAsync("Where does my sister live?", RecallOptions.Default with { MaxConnectionFacts = 33 });

        await asking.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
