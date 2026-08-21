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
/// The three-state witness (30.10, design step 8). <b>This test IS the design's contract.</b>
/// </summary>
/// <remarks>
/// <para>
/// Never-ran, ran-and-declined, and fired-but-contributed-nothing must stay distinguishable from the
/// outside. Collapsing them is exactly how query formulation was retired at a reported 0.0000 with
/// nobody able to say whether the mechanism had ever executed — a null that reads as a measurement.
/// </para>
/// <para>
/// Each state is asserted through <see cref="MemoryContextAssembler.AssembleContextAsync"/> rather
/// than by constructing a report directly, because the defect being guarded against is a *wiring*
/// one: a report that is correct in isolation and never reaches the context proves nothing.
/// </para>
/// </remarks>
public sealed class RecallFanOutWitnessTests
{
    private readonly IShortTermMemoryService _shortTerm = Substitute.For<IShortTermMemoryService>();
    private readonly ILongTermMemoryService _longTerm = Substitute.For<ILongTermMemoryService>();
    private readonly IReasoningMemoryService _reasoning = Substitute.For<IReasoningMemoryService>();
    private readonly IEmbeddingOrchestrator _embeddings = Substitute.For<IEmbeddingOrchestrator>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly ISubQueryDeriver _deriver = Substitute.For<ISubQueryDeriver>();

    private static readonly IMemoryIsolationPolicy SingleTenantPolicy =
        new DefaultMemoryIsolationPolicy(
            Options.Create(new MemoryIsolationOptions()),
            NullLogger<DefaultMemoryIsolationPolicy>.Instance);

    public RecallFanOutWitnessTests()
    {
        _clock.UtcNow.Returns(new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));
        _shortTerm.GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Message>());
        _embeddings.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f, 0.2f, 0.3f, 0.4f });
        _deriver.DeriverId.Returns("det-v1");
    }

    private MemoryContextAssembler CreateSut(bool enabled, bool withDeriver = true)
    {
        var options = new MemoryOptions();
        options.FanOut.Enabled = enabled;

        return new MemoryContextAssembler(
            _shortTerm, _longTerm, _reasoning, null, _embeddings, _clock,
            Options.Create(options), NullLogger<MemoryContextAssembler>.Instance,
            SingleTenantPolicy, rankingContext: null, truncationStrategies: null,
            rerankers: null, projectionFeatures: null, workingMemory: null,
            subQueryDeriver: withDeriver ? _deriver : null);
    }

    private static RecallRequest Compound() => new()
    {
        SessionId = "s",
        Query = "What did I see at the MoMA and what did I order at the Met",
        Options = RecallOptions.Default,
    };

    private static RecallRequest Simple() => new()
    {
        SessionId = "s",
        Query = "hi",
        Options = RecallOptions.Default,
    };

    // ── state 1: never ran ──────────────────────────────────────────────

    [Fact]
    public async Task StateOne_FeatureOff_TheReportIsNull()
    {
        var context = await CreateSut(enabled: false)
            .AssembleContextAsync(Compound(), CancellationToken.None);

        context.FanOutReport.Should().BeNull("the planner never ran");
        await _deriver.DidNotReceive().DeriveAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StateOne_NoDeriverRegistered_TheReportIsNull()
    {
        // Enabled but with nothing able to derive: still never-ran, not declined.
        var context = await CreateSut(enabled: true, withDeriver: false)
            .AssembleContextAsync(Compound(), CancellationToken.None);

        context.FanOutReport.Should().BeNull();
    }

    // ── state 2: ran and declined ───────────────────────────────────────

    [Fact]
    public async Task StateTwo_TheGateDeclines_ReportExistsAndSaysSo()
    {
        var context = await CreateSut(enabled: true)
            .AssembleContextAsync(Simple(), CancellationToken.None);

        context.FanOutReport.Should().NotBeNull(
            "the planner RAN; a null here would be indistinguishable from the feature being off");
        context.FanOutReport!.GateFired.Should().BeFalse();
        context.FanOutReport.SubQueries.Should().BeEmpty();

        await _deriver.DidNotReceive().DeriveAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── state 3: fired, contributed nothing ─────────────────────────────

    [Fact]
    public async Task StateThree_FiredAndYieldedNothing_IsDistinctFromBothOthers()
    {
        // The state the query-formulation lesson demands be visible: the mechanism RAN, legs were
        // derived and retrieved, and none of it changed what reached the prompt.
        _deriver.DeriveAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<RecallSubQuery>
            {
                new() { Affinity = MemoryTypeAffinity.Semantic, QueryText = "the MoMA visit" },
                new() { Affinity = MemoryTypeAffinity.Semantic, QueryText = "the Met dinner" },
            });

        var context = await CreateSut(enabled: true)
            .AssembleContextAsync(Compound(), CancellationToken.None);

        var report = context.FanOutReport;
        report.Should().NotBeNull();
        report!.GateFired.Should().BeTrue();
        report.DeriverId.Should().Be("det-v1");
        report.SubQueries.Should().HaveCount(2, "both legs ran and are individually accounted for");
        report.SubQueries.Should().OnlyContain(y => y.UniqueContributions == 0,
            "an unscored substitute returns nothing, so nothing was contributed");
    }

    [Fact]
    public async Task ADerivationThatYieldsNoLegsIsVoided_NotReportedAsZeroYield()
    {
        // "Fired but derived nothing" is not the same claim as "fired and found nothing": no leg ever
        // ran, so a zero yield here would not be a measurement of anything.
        _deriver.DeriveAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RecallSubQuery>());

        var context = await CreateSut(enabled: true)
            .AssembleContextAsync(Compound(), CancellationToken.None);

        context.FanOutReport!.GateFired.Should().BeTrue();
        context.FanOutReport.VoidReason.Should().Be("derivation-failed");
        context.FanOutReport.SubQueries.Should().BeEmpty();
    }

    // ── caller-supplied legs ────────────────────────────────────────────

    [Fact]
    public async Task CallerSuppliedSubQueriesWinEvenWithTheFeatureOff()
    {
        // The TemporalReferenceTime philosophy: an explicit request is not something a global flag
        // gets to veto. This is also what makes the mechanism reachable from the eval harness.
        var request = Compound() with
        {
            SubQueries = new List<RecallSubQuery>
            {
                new() { Affinity = MemoryTypeAffinity.Temporal, QueryText = "when was the MoMA visit" },
            },
        };

        var context = await CreateSut(enabled: false)
            .AssembleContextAsync(request, CancellationToken.None);

        context.FanOutReport.Should().NotBeNull();
        context.FanOutReport!.DeriverId.Should().Be("caller");
        context.FanOutReport.GateFired.Should().BeTrue();
    }

    [Fact]
    public async Task CallerSuppliedLegsAreCappedAtMaxSubQueries()
    {
        // The caller does not get to decide how many round trips a recall costs either.
        var options = new MemoryOptions();
        options.FanOut.MaxSubQueries = 2;

        var sut = new MemoryContextAssembler(
            _shortTerm, _longTerm, _reasoning, null, _embeddings, _clock,
            Options.Create(options), NullLogger<MemoryContextAssembler>.Instance,
            SingleTenantPolicy, null, null, null, null, null, _deriver);

        var request = Compound() with
        {
            SubQueries = Enumerable.Range(0, 5)
                .Select(i => new RecallSubQuery
                {
                    Affinity = MemoryTypeAffinity.Semantic,
                    QueryText = $"leg {i}",
                })
                .ToList(),
        };

        var context = await sut.AssembleContextAsync(request, CancellationToken.None);

        context.FanOutReport!.SubQueries.Should().HaveCount(2);
    }

    [Fact]
    public async Task AnEmbeddingFailureIsCountedIntoTheVoidReason_NotThrown()
    {
        // The recall already succeeded by this point. A failed enhancement must not take it down --
        // and must not vanish either.
        _embeddings.EmbedQueryAsync("leg one", Arg.Any<CancellationToken>())
            .Returns<float[]>(_ => throw new InvalidOperationException("embedding provider is down"));

        var request = Compound() with
        {
            SubQueries = new List<RecallSubQuery>
            {
                new() { Affinity = MemoryTypeAffinity.Semantic, QueryText = "leg one" },
            },
        };

        var assemble = async () => await CreateSut(enabled: false)
            .AssembleContextAsync(request, CancellationToken.None);

        var context = (await assemble.Should().NotThrowAsync()).Which;
        context.FanOutReport!.VoidReason.Should().Be("embedding-failed:1/1");
    }
}
