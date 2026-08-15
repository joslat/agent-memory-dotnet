using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using AgentMemory.AgentFramework.Mapping;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// 30.8. Forgetting becomes sayable — on a thin recall, once, and never by showing what was forgotten.
/// </summary>
/// <remarks>
/// <para>
/// Forgetting already worked and was <b>invisible</b>: decay pruned, recall returned less, and the
/// agent answered as though it had never known — indistinguishable, to the person asking, from never
/// having been told. A memory system whose gaps all look like the same gap cannot be corrected by its
/// user, because they do not know there is anything to re-supply.
/// </para>
/// <para>
/// The line this feature must not cross is rendering the forgotten <i>content</i>. That would undo the
/// forgetting outright: the decayed values would be back in the prompt, occupying budget, being
/// answered from. What surfaces is the shape of the absence — a topic, a count, a date.
/// </para>
/// </remarks>
public sealed class LegibleForgettingTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);

    private readonly IShortTermMemoryService _shortTerm = Substitute.For<IShortTermMemoryService>();
    private readonly ILongTermMemoryService _longTerm = Substitute.For<ILongTermMemoryService>();
    private readonly IReasoningMemoryService _reasoning = Substitute.For<IReasoningMemoryService>();
    private readonly IEmbeddingOrchestrator _embeddings = Substitute.For<IEmbeddingOrchestrator>();
    private readonly IClock _clock = Substitute.For<IClock>();

    private static readonly IMemoryIsolationPolicy Policy =
        new DefaultMemoryIsolationPolicy(
            Options.Create(new MemoryIsolationOptions()),
            NullLogger<DefaultMemoryIsolationPolicy>.Instance);

    private static Fact Decayed(string subject, string id, int daysAgo = 90) => new()
    {
        FactId = id, Subject = subject, Predicate = "prefers", Object = "the aisle seat",
        Confidence = 0.9,
        CreatedAtUtc = Stamp.AddDays(-daysAgo),
        InvalidatedAtUtc = Stamp.AddDays(-1),
        InvalidatedReason = "decay",
    };

    public LegibleForgettingTests()
    {
        _clock.UtcNow.Returns(Stamp);
        _embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new float[8]));
        _embeddings.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new float[8]));
        _shortTerm.GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>([]));
        _shortTerm.SearchMessagesAsync(
                Arg.Any<string?>(), Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>([]));
        _longTerm.SearchEntitiesAsync(
                Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>([]));
        _longTerm.SearchPreferencesAsync(
                Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Preference>>([]));
        _longTerm.SearchFactsAsync(
                Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([]));
        _reasoning.SearchSimilarTracesAsync(
                Arg.Any<float[]>(), Arg.Any<bool?>(), Arg.Any<int>(), Arg.Any<double>(),
                Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReasoningTrace>>([]));
        _longTerm.SearchDecayedFactsAsync(
                Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>(
                [Decayed("flights", "a", 120), Decayed("flights", "b", 90), Decayed("hotels", "c")]));
    }

    private MemoryContextAssembler Sut() =>
        new(_shortTerm, _longTerm, _reasoning, null, _embeddings, _clock,
            Options.Create(new MemoryOptions()),
            NullLogger<MemoryContextAssembler>.Instance, Policy,
            rankingContext: null, truncationStrategies: null, rerankers: null,
            projectionFeatures: null);

    private Task<MemoryContext> AssembleAsync(RecallOptions options) =>
        Sut().AssembleContextAsync(
            new RecallRequest
            {
                SessionId = "s1",
                Query = "what do you know about my travel preferences?",
                QueryEmbedding = new float[8],
                Options = options,
            },
            CancellationToken.None);

    // ── off ───────────────────────────────────────────────────────────

    [Fact]
    public async Task WithTheFlagOffTheProbeIsNeverIssued()
    {
        var context = await AssembleAsync(RecallOptions.Default);

        await _longTerm.DidNotReceive().SearchDecayedFactsAsync(
            Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(),
            Arg.Any<CancellationToken>());
        context.ForgottenTopics.Should().BeEmpty();
    }

    [Fact]
    public void TheDefaultsAreOffAndTen()
    {
        var options = new RecallOptions();

        options.LegibleForgetting.Should().BeFalse();
        options.TombstoneProbeTopK.Should().Be(10);
    }

    // ── the thinness trigger ──────────────────────────────────────────

    [Fact]
    public async Task AWellAnsweredRecallPaysNothing()
    {
        // The probe runs only when the fact section came back empty. A recall that answered the
        // question has nothing to apologise for, and charging every turn a query for that would be a
        // meta-memory surface funded by the recall path it comments on.
        _longTerm.SearchFactsAsync(
                Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>(
            [
                new Fact
                {
                    FactId = "live", Subject = "flights", Predicate = "prefers", Object = "window",
                    Confidence = 0.9, CreatedAtUtc = Stamp,
                },
            ]));

        var context = await AssembleAsync(RecallOptions.Default with { LegibleForgetting = true });

        await _longTerm.DidNotReceive().SearchDecayedFactsAsync(
            Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(),
            Arg.Any<CancellationToken>());
        context.ForgottenTopics.Should().BeEmpty();
    }

    [Fact]
    public async Task ARecallThatNeverSearchedFactsDoesNotClaimAnAbsence()
    {
        // MaxFacts = 0 means the section was never asked, and never asking is not the same as an
        // absence. Reporting a tombstone here would invent a gap the recall never looked for.
        var context = await AssembleAsync(RecallOptions.Default with
        {
            LegibleForgetting = true,
            MaxFacts = 0,
        });

        await _longTerm.DidNotReceive().SearchDecayedFactsAsync(
            Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(),
            Arg.Any<CancellationToken>());
        context.ForgottenTopics.Should().BeEmpty();
    }

    [Fact]
    public async Task AThinRecallProbesAndReportsTheDominantTopic()
    {
        var context = await AssembleAsync(RecallOptions.Default with { LegibleForgetting = true });

        await _longTerm.Received(1).SearchDecayedFactsAsync(
            Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(),
            Arg.Any<CancellationToken>());
        var summary = context.ForgottenTopics.Should().ContainSingle().Subject;
        summary.Topic.Should().Be("flights", "two of the three decayed facts share that subject");
        summary.Count.Should().Be(2);
    }

    [Fact]
    public async Task TheSummaryCarriesTheDatesThatMakeItMeaningful()
    {
        var context = await AssembleAsync(RecallOptions.Default with { LegibleForgetting = true });

        var summary = context.ForgottenTopics.Single();
        summary.OldestUtc.Should().Be(Stamp.AddDays(-120));
        summary.AgedOutUtc.Should().Be(Stamp.AddDays(-1));
    }

    [Fact]
    public async Task ThePassedProbeBudgetIsTheConfiguredOne()
    {
        await AssembleAsync(RecallOptions.Default with
        {
            LegibleForgetting = true,
            TombstoneProbeTopK = 3,
        });

        await _longTerm.Received(1).SearchDecayedFactsAsync(
            Arg.Any<float[]>(), 3, Arg.Any<double>(), Arg.Any<MemoryScope?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NothingForgottenYieldsNoTombstone()
    {
        _longTerm.SearchDecayedFactsAsync(
                Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([]));

        var context = await AssembleAsync(RecallOptions.Default with { LegibleForgetting = true });

        context.ForgottenTopics.Should().BeEmpty();
    }

    [Fact]
    public async Task AFailingProbeLeavesTheRecallExactlyAsItWas()
    {
        // The correct failure direction for a meta-memory surface: silence, not an error, and
        // certainly not a fabricated absence.
        _longTerm.SearchDecayedFactsAsync(
                Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<Fact>>>(_ => throw new InvalidOperationException("index down"));

        var context = await AssembleAsync(RecallOptions.Default with { LegibleForgetting = true });

        context.ForgottenTopics.Should().BeEmpty();
    }

    // ── rendering ─────────────────────────────────────────────────────

    private static RecallResult Result(IReadOnlyList<ForgottenTopicSummary>? forgotten = null)
    {
        var context = new MemoryContext
        {
            SessionId = "s1",
            AssembledAtUtc = Stamp,
            ForgottenTopics = forgotten ?? [],
        };
        return new RecallResult { Context = context, TotalItemsRetrieved = context.ForgottenTopics.Count };
    }

    private static ForgottenTopicSummary Summary() => new()
    {
        Topic = "flights",
        Count = 2,
        OldestUtc = Stamp.AddDays(-120),
        AgedOutUtc = Stamp.AddDays(-1),
    };

    [Fact]
    public void WithNothingForgottenTheFormatterIsByteIdenticalToBefore()
    {
        MemoryContextFormatter.FormatRecallResult(Result()).Should().BeEmpty();
    }

    [Fact]
    public void WithNothingForgottenTheAgentFrameworkMapperIsByteIdenticalToBefore()
    {
        var options = new ContextFormatOptions();
        var withProperty = MafTypeMapper.ToContextMessages(Result().Context, options);
        var control = MafTypeMapper.ToContextMessages(
            new MemoryContext { SessionId = "s1", AssembledAtUtc = Stamp }, options);

        withProperty.Select(m => $"{m.Role}|{m.Text}")
            .Should().Equal(control.Select(m => $"{m.Role}|{m.Text}"));
    }

    [Fact]
    public void ATombstoneStatesTheAbsenceWithoutShowingWhatWasForgotten()
    {
        // The line this feature must not cross. Rendering the forgotten content would undo the
        // forgetting: the decayed values would be back in the prompt, occupying budget, answered from.
        var rendered = MemoryContextFormatter.FormatRecallResult(Result([Summary()]));

        rendered.Should().Contain("I used to know 2 thing(s) about flights");
        rendered.Should().Contain("no longer available");
        rendered.Should().NotContain("aisle seat", "the forgotten CONTENT must never come back");
    }

    [Fact]
    public void ATombstoneIsDelimitedAndAdmittedLikeEveryOtherRecalledCategory()
    {
        // The topic string is an extracted fact's subject -- user text. Being a statement ABOUT memory
        // does not make it trusted content.
        var rendered = MemoryContextFormatter.FormatRecallResult(Result([Summary()]));

        rendered.Should().Contain("<recalled_memory category=\"forgotten\">");
    }

    [Fact]
    public void TheAgentFrameworkMapperRendersTheSameAbsence()
    {
        var messages = MafTypeMapper.ToContextMessages(
            Result([Summary()]).Context, new ContextFormatOptions());

        string.Join("\n", messages.Select(m => m.Text))
            .Should().Contain("2 thing(s) about flights");
    }

    [Fact]
    public void ARecallWhoseOnlyContentIsATombstoneStillRenders()
    {
        // Otherwise the formatter's zero-items early return would swallow exactly the recall where
        // saying "I no longer know" matters most.
        MemoryContextFormatter.FormatRecallResult(Result([Summary()]))
            .Should().Contain("No Longer Known");
    }
}
