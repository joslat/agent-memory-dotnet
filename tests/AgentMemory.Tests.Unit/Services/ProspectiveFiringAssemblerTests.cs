using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// 30.7 step 6. The option reaches the query — and the two gates it has to pass first.
/// </summary>
/// <remarks>
/// <para>
/// This is the sixteenth shipped-but-unreachable instance not happening. Firing is built out of a DIM
/// whose default returns empty, so a flag that never reached the assembler would produce exactly the
/// same observable result as "nothing was due" — silently, forever, on every host.
/// </para>
/// <para>
/// The second gate is the interesting one. Firing reads a fact's valid-time window, and a recall
/// running with <see cref="ValidTimeMode.Ignore"/> has no window to read: turning firing on without the
/// valid-time gate would surface facts by a clock the rest of that recall is deliberately ignoring, and
/// the reader would have no way to know the two halves disagreed.
/// </para>
/// </remarks>
public sealed class ProspectiveFiringAssemblerTests
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

    private static Fact Make(string id) => new()
    {
        FactId = id, Subject = "subscription", Predicate = "renews", Object = "today",
        Confidence = 0.9, CreatedAtUtc = Stamp.AddDays(-30),
        ValidFrom = Stamp.AddDays(-1),
    };

    public ProspectiveFiringAssemblerTests()
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
        _longTerm.SearchFactsAsync(
                Arg.Any<float[]>(), Arg.Any<ValidTimeMode>(), Arg.Any<int>(), Arg.Any<double>(),
                Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([]));
        _reasoning.SearchSimilarTracesAsync(
                Arg.Any<float[]>(), Arg.Any<bool?>(), Arg.Any<int>(), Arg.Any<double>(),
                Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReasoningTrace>>([]));
        _longTerm.GetDueFactsAsync(
                Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<TimeSpan>(),
                Arg.Any<int>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProspectiveDueResult { Due = [Make("d1")] }));
    }

    private MemoryContextAssembler Sut() =>
        new(_shortTerm, _longTerm, _reasoning, null, _embeddings, _clock,
            Options.Create(new MemoryOptions()),
            NullLogger<MemoryContextAssembler>.Instance, Policy,
            rankingContext: null, truncationStrategies: null, rerankers: null,
            projectionFeatures: null);

    private static RecallRequest Request(RecallOptions options) => new()
    {
        SessionId = "s1",
        Query = "anything?",
        QueryEmbedding = new float[8],
        Options = options,
    };

    private Task<MemoryContext> AssembleAsync(RecallOptions options) =>
        Sut().AssembleContextAsync(Request(options), CancellationToken.None);

    // ── the two gates ─────────────────────────────────────────────────

    [Fact]
    public async Task WithFiringOffTheQueryIsNeverIssued()
    {
        var context = await AssembleAsync(RecallOptions.Default);

        await _longTerm.DidNotReceive().GetDueFactsAsync(
            Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<TimeSpan>(),
            Arg.Any<int>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        context.DueFacts.Items.Should().BeEmpty();
        context.ExpiringFacts.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task FiringOnWithoutTheValidTimeGateStillIssuesNothing()
    {
        // Firing reads a fact's valid-time window. A recall ignoring valid time has no window to read,
        // so surfacing facts by that clock would make the two halves of one recall disagree.
        var context = await AssembleAsync(RecallOptions.Default with { ProspectiveFiring = true });

        await _longTerm.DidNotReceive().GetDueFactsAsync(
            Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<TimeSpan>(),
            Arg.Any<int>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        context.DueFacts.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task BothGatesOnReachesTheQueryAndPopulatesTheSection()
    {
        var context = await AssembleAsync(RecallOptions.Default with
        {
            ProspectiveFiring = true,
            ValidTime = ValidTimeMode.Current,
        });

        await _longTerm.Received(1).GetDueFactsAsync(
            Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<TimeSpan>(),
            Arg.Any<int>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        context.DueFacts.Items.Should().ContainSingle().Which.FactId.Should().Be("d1");
    }

    // ── the window it asks for ────────────────────────────────────────

    [Fact]
    public async Task TheLookbackWindowIsTheConfiguredOneAndEndsAtNow()
    {
        await AssembleAsync(RecallOptions.Default with
        {
            ProspectiveFiring = true,
            ValidTime = ValidTimeMode.Current,
            DueLookback = TimeSpan.FromDays(3),
        });

        await _longTerm.Received(1).GetDueFactsAsync(
            Stamp.AddDays(-3), Stamp, Arg.Any<TimeSpan>(),
            Arg.Any<int>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheBudgetAndExpiringWindowAreTheConfiguredOnes()
    {
        await AssembleAsync(RecallOptions.Default with
        {
            ProspectiveFiring = true,
            ValidTime = ValidTimeMode.Current,
            MaxDueItems = 2,
            ExpiringWindow = TimeSpan.FromDays(30),
        });

        await _longTerm.Received(1).GetDueFactsAsync(
            Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), TimeSpan.FromDays(30),
            2, Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    // ── de-dup ────────────────────────────────────────────────────────

    [Fact]
    public async Task AFactThatIsBothRelevantAndDueRendersOnlyAsDue()
    {
        // Rendering it twice spends the budget twice on one fact and makes the reminder look like a
        // coincidence of the query rather than something volunteered.
        _longTerm.SearchFactsAsync(
                Arg.Any<float[]>(), Arg.Any<ValidTimeMode>(), Arg.Any<int>(), Arg.Any<double>(),
                Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([Make("d1"), Make("other")]));

        var context = await AssembleAsync(RecallOptions.Default with
        {
            ProspectiveFiring = true,
            ValidTime = ValidTimeMode.Current,
        });

        context.DueFacts.Items.Select(f => f.FactId).Should().Equal("d1");
        context.RelevantFacts.Items.Select(f => f.FactId).Should().Equal("other");
    }

    [Fact]
    public async Task AnUnrelatedRelevantFactIsUntouched()
    {
        _longTerm.SearchFactsAsync(
                Arg.Any<float[]>(), Arg.Any<ValidTimeMode>(), Arg.Any<int>(), Arg.Any<double>(),
                Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([Make("other")]));

        var context = await AssembleAsync(RecallOptions.Default with
        {
            ProspectiveFiring = true,
            ValidTime = ValidTimeMode.Current,
        });

        context.RelevantFacts.Items.Select(f => f.FactId).Should().Equal("other");
    }

    // ── diagnostics tell silence apart from absence ───────────────────

    [Fact]
    public async Task WithFiringOffTheSectionIsMarkedNeverSearched()
    {
        // The shipped-but-unreachable trap in its exact shape: a host whose custom
        // ILongTermMemoryService silently hits the DIM's empty default must be able to tell that apart
        // from "nothing was due". They produce an identical section otherwise.
        var context = await AssembleAsync(RecallOptions.Default with { IncludeDiagnostics = true });

        context.DueFacts.Diagnostics!.Searched.Should().BeFalse();
    }

    [Fact]
    public async Task WithFiringOnTheSectionIsMarkedSearched()
    {
        var context = await AssembleAsync(RecallOptions.Default with
        {
            ProspectiveFiring = true,
            ValidTime = ValidTimeMode.Current,
            IncludeDiagnostics = true,
        });

        context.DueFacts.Diagnostics!.Searched.Should().BeTrue();
    }

    // ── the DIM, as a SemVer property ─────────────────────────────────

    [Theory]
    [InlineData(typeof(ILongTermMemoryService))]
    [InlineData(typeof(IFactRepository))]
    public void TheNewMemberShipsAsADefaultInterfaceMethodSoNoImplementorBreaks(Type contract)
    {
        // Both interfaces are locked under SemVer, and both are large enough that a hand-written
        // "implements only the old surface" stub would be forty throwing members obscuring the one line
        // under test -- so the SemVer property is asserted directly: the member is NOT abstract, which
        // is precisely what lets an existing implementor keep compiling untouched.
        var method = contract.GetMethod("GetDueFactsAsync");

        method.Should().NotBeNull();
        method!.IsAbstract.Should().BeFalse(
            "{0}.GetDueFactsAsync must carry a default body, or adding it breaks every implementor",
            contract.Name);
    }

    [Fact]
    public void TheDefaultIsAnEmptyResultRatherThanAThrow()
    {
        // Firing differs from delta recall here, deliberately. An empty delta is a positive claim
        // ("nothing changed") that a store unable to compute one must not fabricate. An empty firing
        // result is not a claim at all -- a store with no valid-time query simply does not fire -- and
        // the section diagnostics carry the distinction instead.
        ProspectiveDueResult.Empty.IsEmpty.Should().BeTrue();
        ProspectiveDueResult.Empty.Due.Should().BeEmpty();
        ProspectiveDueResult.Empty.Expiring.Should().BeEmpty();
    }
}
