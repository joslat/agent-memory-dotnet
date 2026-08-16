using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using AgentMemory.Core.Services.Projection;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// 30.2 step 4. Projection reaches the assembled context — through both recall paths — and costs
/// nothing when it is off.
/// </summary>
/// <remarks>
/// The two assembly paths have already diverged once on a single option (<c>SuccessfulTracesOnly</c>
/// is passed on the live path and hardcoded null on the as-of path), so "the as-of path mirrors the
/// live one" is asserted here rather than assumed. A projection wired into only one of them would
/// silently produce unprojected contexts for every point-in-time recall.
/// </remarks>
public sealed class MemoryContextAssemblerProjectionTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly IShortTermMemoryService _shortTerm = Substitute.For<IShortTermMemoryService>();
    private readonly ILongTermMemoryService _longTerm = Substitute.For<ILongTermMemoryService>();
    private readonly IReasoningMemoryService _reasoning = Substitute.For<IReasoningMemoryService>();
    private readonly IEmbeddingOrchestrator _embeddings = Substitute.For<IEmbeddingOrchestrator>();
    private readonly IClock _clock = Substitute.For<IClock>();

    private static readonly IMemoryIsolationPolicy Policy =
        new DefaultMemoryIsolationPolicy(
            Options.Create(new MemoryIsolationOptions()),
            NullLogger<DefaultMemoryIsolationPolicy>.Instance);

    private static Fact MakeFact(string id) => new()
    {
        FactId = id, Subject = "Bob", Predicate = "works_at", Object = "Acme",
        Confidence = 0.9, CreatedAtUtc = Stamp,
    };

    public MemoryContextAssemblerProjectionTests()
    {
        _clock.UtcNow.Returns(Stamp);
        _embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new float[8]));
        _embeddings.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new float[8]));

        _shortTerm.GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>([]));
        _shortTerm.SearchMessagesAsync(Arg.Any<string?>(), Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>([]));
        _longTerm.SearchEntitiesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>([]));
        _longTerm.SearchPreferencesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Preference>>([]));
        _longTerm.SearchFactsAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([MakeFact("f1")]));
        _reasoning.SearchSimilarTracesAsync(Arg.Any<float[]>(), Arg.Any<bool?>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReasoningTrace>>([]));
    }

    private MemoryContextAssembler Sut(
        MemoryOptions? options = null, params IProjectionFeature[] features) =>
        new(_shortTerm, _longTerm, _reasoning, null, _embeddings, _clock,
            Options.Create(options ?? new MemoryOptions()),
            NullLogger<MemoryContextAssembler>.Instance, Policy,
            rankingContext: null, truncationStrategies: null, rerankers: null,
            projectionFeatures: features);

    private static RecallRequest Request(RecallOptions? options = null) => new()
    {
        SessionId = "s1",
        Query = "who does Bob work for?",
        QueryEmbedding = new float[8],
        Options = options ?? RecallOptions.Default,
    };

    [Fact]
    public async Task WithNoFeaturesRegisteredTheContextCarriesNoProjection()
    {
        // The shipped default for any host that has not registered projection at all.
        var context = await Sut().AssembleContextAsync(Request(), CancellationToken.None);

        context.Projection.Should().BeNull();
    }

    [Fact]
    public async Task WithFeaturesRegisteredButAllFlagsOffTheContextStillCarriesNoProjection()
    {
        // THE off-state at the assembler seam: registration is unconditional, activation is not.
        var context = await Sut(null, new MatchQualityProjectionFeature())
            .AssembleContextAsync(Request(), CancellationToken.None);

        context.Projection.Should().BeNull();
    }

    [Fact]
    public async Task EnablingMatchQualityProducesAProjection()
    {
        var options = new RecallOptions
        {
            Projection = MemoryProjectionOptions.Default with { AnnotateMatchQuality = true },
        };

        var context = await Sut(null, new MatchQualityProjectionFeature())
            .AssembleContextAsync(Request(options), CancellationToken.None);

        // The stub long-term service does not implement IScoredLongTermSearch, so scores are
        // unavailable and the feature correctly contributes nothing -- projection stays null. That is
        // the void witness reaching all the way up through the assembler.
        context.Projection.Should().BeNull(
            "an unscoreable provider must not produce fabricated annotations");
    }

    [Fact]
    public async Task AProjectionFeatureThatContributesReachesTheAssembledContext()
    {
        var options = new RecallOptions
        {
            Projection = MemoryProjectionOptions.Default with { AnnotateMatchQuality = true },
        };

        var context = await Sut(null, new StubFeature())
            .AssembleContextAsync(Request(options), CancellationToken.None);

        context.Projection.Should().NotBeNull();
        context.Projection!.Annotations.Should().ContainKey("stub");
    }

    [Fact]
    public async Task TheApplicationLevelProjectionIsInheritedByARequestThatDidNotAskForOne()
    {
        // The 25.2 inheritance pattern one level down: a host that configured projection through
        // MemoryOptions must see it on a plain RecallAsync, or the option binds and does nothing.
        var appOptions = new MemoryOptions
        {
            Projection = MemoryProjectionOptions.Default with { AnnotateMatchQuality = true },
        };

        var context = await Sut(appOptions, new StubFeature())
            .AssembleContextAsync(Request(), CancellationToken.None);

        context.Projection.Should().NotBeNull();
    }

    [Fact]
    public async Task AnExplicitRequestProjectionWinsOverTheApplicationDefault()
    {
        var appOptions = new MemoryOptions
        {
            Projection = MemoryProjectionOptions.Default with { AnnotateMatchQuality = true },
        };
        // Explicitly asking for the all-off shape must NOT inherit the application's on-shape.
        var requestOptions = new RecallOptions
        {
            Projection = new MemoryProjectionOptions(),
        };

        var context = await Sut(appOptions, new StubFeature())
            .AssembleContextAsync(Request(requestOptions), CancellationToken.None);

        context.Projection.Should().BeNull();
    }

    [Fact]
    public async Task TheAsOfPathProjectsToo()
    {
        // The two paths have diverged once already on a single option, so this is asserted rather than
        // assumed. An unprojected as-of context would be a silent hole.
        _longTerm.SearchFactsAsOfAsync(
                Arg.Any<float[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<double>(),
                Arg.Any<MemoryScope?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([MakeFact("f1")]));
        _shortTerm.GetRecentMessagesAsOfAsync(
                Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>([]));
        _longTerm.SearchEntitiesAsOfAsync(
                Arg.Any<float[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<double>(),
                Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>([]));
        _longTerm.SearchPreferencesAsOfAsync(
                Arg.Any<float[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<double>(),
                Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Preference>>([]));
        _reasoning.SearchSimilarTracesAsOfAsync(
                Arg.Any<float[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<bool?>(), Arg.Any<int>(),
                Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReasoningTrace>>([]));

        var options = new RecallOptions
        {
            Projection = MemoryProjectionOptions.Default with { AnnotateMatchQuality = true },
        };

        var context = await Sut(null, new StubFeature())
            .AssembleContextAsOfAsync(Request(options), Stamp, systemAsOf: null, CancellationToken.None);

        context.Projection.Should().NotBeNull("the as-of path must project exactly like the live path");
    }

    [Fact]
    public async Task TheTraceFloorReachesTheReasoningServiceOnTheLivePath()
    {
        // 30.3. THE reachability question for this option. A floor that is parsed, threaded and then
        // dropped before the query is exactly how IncludeQuestionTypes and AbstentionPolicy came to be
        // dead -- discovered only when a measurement failed to move.
        var options = new RecallOptions { MinSimilarityScore = 0.7, MinTraceSimilarityScore = 0.92 };

        await Sut().AssembleContextAsync(Request(options), CancellationToken.None);

        await _reasoning.Received(1).SearchSimilarTracesAsync(
            Arg.Any<float[]>(), Arg.Any<bool?>(), Arg.Any<int>(), 0.92,
            Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheOtherSectionsKeepTheSharedFloorWhenTracesAreRaised()
    {
        // Per-CATEGORY: raising the procedure floor must not quietly raise the fact floor with it.
        var options = new RecallOptions { MinSimilarityScore = 0.7, MinTraceSimilarityScore = 0.92 };

        await Sut().AssembleContextAsync(Request(options), CancellationToken.None);

        await _longTerm.Received(1).SearchFactsAsync(
            Arg.Any<float[]>(), Arg.Any<int>(), 0.7, Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithNoTraceFloorSetTheSharedScoreIsUsedForTracesToo()
    {
        // The byte-identical default, asserted at the query itself rather than at the option.
        var options = new RecallOptions { MinSimilarityScore = 0.65 };

        await Sut().AssembleContextAsync(Request(options), CancellationToken.None);

        await _reasoning.Received(1).SearchSimilarTracesAsync(
            Arg.Any<float[]>(), Arg.Any<bool?>(), Arg.Any<int>(), 0.65,
            Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>A feature that always contributes, so the wiring itself is what is under test.</summary>
    private sealed class StubFeature : IProjectionFeature
    {
        public bool IsEnabled(MemoryProjectionOptions options) => options.AnnotateMatchQuality;

        public Task ApplyAsync(ProjectionState state, CancellationToken cancellationToken)
        {
            state.Annotate("stub", a => a with { Score = 1.0 });
            return Task.CompletedTask;
        }
    }
}
