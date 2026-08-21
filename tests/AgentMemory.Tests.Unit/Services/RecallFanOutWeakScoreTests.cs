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
/// Signal W — fan out when the blended query scored badly (30.10, design step 9).
/// </summary>
/// <remarks>
/// <para>
/// W is evaluated <b>after</b> the monolithic sections resolve, because unlike the other four rules
/// it is a statement about what they came back with rather than about the query's shape.
/// </para>
/// <para>
/// The important case here is the third one. A provider that publishes no scores at all looks exactly
/// like one whose every score sits below the threshold — so W must be able to tell them apart, and
/// must produce neither a fake fire nor a fake all-clear when it cannot form an opinion.
/// </para>
/// </remarks>
public sealed class RecallFanOutWeakScoreTests
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

    public RecallFanOutWeakScoreTests()
    {
        _clock.UtcNow.Returns(new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));
        _shortTerm.GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Message>());
        _embeddings.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f, 0.2f, 0.3f, 0.4f });
        _deriver.DeriverId.Returns("det-v1");
        _deriver.DeriveAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<RecallSubQuery>
            {
                new() { Affinity = MemoryTypeAffinity.Semantic, QueryText = "leg" },
            });
    }

    private MemoryContextAssembler CreateSut(double? threshold)
    {
        var options = new MemoryOptions();
        options.FanOut.Enabled = true;
        options.FanOut.WeakTopScoreThreshold = threshold;

        return new MemoryContextAssembler(
            _shortTerm, _longTerm, _reasoning, null, _embeddings, _clock,
            Options.Create(options), NullLogger<MemoryContextAssembler>.Instance,
            SingleTenantPolicy, null, null, null, null, null, _deriver);
    }

    /// <summary>A simple query — no pre-retrieval rule fires, so only W can.</summary>
    private static RecallRequest Simple() => new()
    {
        SessionId = "s",
        Query = "coffee",
        Options = RecallOptions.Default,
    };

    [Fact]
    public async Task WIsInertWhenNoThresholdIsSet()
    {
        // Null is the shipped default. 0.7 is a measured dead zone on ONE corpus, so a default value
        // would bake that corpus's calibration into every host.
        var context = await CreateSut(threshold: null)
            .AssembleContextAsync(Simple(), CancellationToken.None);

        context.FanOutReport!.GateFired.Should().BeFalse();
        context.FanOutReport.FiredRules.Should().NotContain("W");
    }

    [Fact]
    public async Task AnUnscoredProviderMarksItselfRatherThanDecidingEitherWay()
    {
        // The substitute publishes no scored contract, so nothing observed a score. W must record that
        // rather than read it as "everything scored badly" (a fake fire) or as a confident decline
        // (a fake all-clear).
        var context = await CreateSut(threshold: 0.7)
            .AssembleContextAsync(Simple(), CancellationToken.None);

        context.FanOutReport!.GateFired.Should().BeFalse();
        context.FanOutReport.FiredRules.Should().Contain("W-unscored");
    }

    [Fact]
    public async Task WNeverFiresOnAQueryAPreRetrievalRuleAlreadyClaimed()
    {
        // A query already known to be compound does not need a second reason, and double-counting
        // would make the fired-rule set unreadable as evidence of WHY it fired.
        var compound = new RecallRequest
        {
            SessionId = "s",
            Query = "What did I see at the MoMA and what did I order at the Met",
            Options = RecallOptions.Default,
        };

        var context = await CreateSut(threshold: 0.7)
            .AssembleContextAsync(compound, CancellationToken.None);

        context.FanOutReport!.GateFired.Should().BeTrue();
        context.FanOutReport.FiredRules.Should().NotContain("W");
        context.FanOutReport.FiredRules.Should().NotContain("W-unscored");
    }

    [Fact]
    public async Task TheReportStillDistinguishesRanFromNeverRan()
    {
        // Whatever W decides, the planner ran -- so the report must exist. A null here would be
        // indistinguishable from the feature being switched off entirely.
        var context = await CreateSut(threshold: 0.7)
            .AssembleContextAsync(Simple(), CancellationToken.None);

        context.FanOutReport.Should().NotBeNull();
    }
}
