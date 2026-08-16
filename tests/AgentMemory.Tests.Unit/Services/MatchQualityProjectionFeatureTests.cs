using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Services.Projection;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// 30.2 step 5. Match quality: what scored well, what merely came close, and when to say neither.
/// </summary>
public sealed class MatchQualityProjectionFeatureTests
{
    private static readonly DateTimeOffset Stamp = DateTimeOffset.UnixEpoch;

    private static Fact MakeFact(string id) => new()
    {
        FactId = id, Subject = "Bob", Predicate = "works_at", Object = "Acme",
        Confidence = 0.9, CreatedAtUtc = Stamp,
    };

    private static ReasoningTrace MakeTrace(string id) => new()
    {
        TraceId = id, SessionId = "s1", Task = "do a thing", StartedAtUtc = Stamp,
    };

    private static ProjectionState State(
        MemoryProjectionOptions options,
        IReadOnlyList<Fact>? facts = null,
        IReadOnlyList<(Fact, double)>? factScores = null,
        IReadOnlyList<ReasoningTrace>? traces = null,
        IReadOnlyList<(ReasoningTrace, double)>? traceScores = null) => new()
    {
        Options = options,
        Scope = null,
        Entities = [],
        Facts = facts ?? [],
        Preferences = [],
        Traces = traces ?? [],
        RecentMessages = [],
        RelevantMessages = [],
        EntityScores = [],
        FactScores = factScores ?? [],
        PreferenceScores = [],
        TraceScores = traceScores ?? [],
    };

    private static async Task<ProjectedContext?> RunAsync(ProjectionState state)
    {
        var feature = new MatchQualityProjectionFeature();
        if (!feature.IsEnabled(state.Options)) return null;
        await feature.ApplyAsync(state, CancellationToken.None);
        return state.IsEmpty ? null : state.Build();
    }

    private static MemoryProjectionOptions On => MemoryProjectionOptions.Default with { AnnotateMatchQuality = true };

    [Fact]
    public void TheFeatureIsOffUnlessItsOwnFlagIsSet()
    {
        var feature = new MatchQualityProjectionFeature();

        feature.IsEnabled(MemoryProjectionOptions.Default).Should().BeFalse();
        feature.IsEnabled(On).Should().BeTrue();
        // Another feature's flag must not switch this one on.
        feature.IsEnabled(MemoryProjectionOptions.Default with { AttachSourceQuotes = true }).Should().BeFalse();
    }

    [Fact]
    public async Task AScoreBelowTheThresholdIsMarkedANearMiss()
    {
        var fact = MakeFact("f1");
        var projected = await RunAsync(State(On, [fact], [(fact, 0.72)]));

        projected!.Annotations["f1"].Score.Should().Be(0.72);
        projected.Annotations["f1"].IsNearMiss.Should().BeTrue();
    }

    [Fact]
    public async Task AScoreAtOrAboveTheThresholdIsNotANearMiss()
    {
        var fact = MakeFact("f1");
        var projected = await RunAsync(State(On, [fact], [(fact, 0.85)]));

        projected!.Annotations["f1"].IsNearMiss.Should().BeFalse(
            "the threshold is inclusive at the top -- 0.85 clears a 0.85 bar");
    }

    [Fact]
    public async Task AWeakTopScoreEmitsExactlyOneNoDirectMatchBlockForTheSection()
    {
        // Per section, not per item: three weak facts are one weak section, and three identical lines
        // would spend tokens to say one thing.
        var facts = new[] { MakeFact("f1"), MakeFact("f2"), MakeFact("f3") };
        var projected = await RunAsync(State(On, facts, [(facts[0], 0.70), (facts[1], 0.65), (facts[2], 0.60)]));

        projected!.Blocks.Should().ContainSingle()
            .Which.Should().Match<ProjectedBlock>(b =>
                b.Kind == ProjectedBlockKind.NoDirectMatch && b.SectionKey == ProjectionSectionKeys.Facts);
        projected.Blocks[0].Text.Should().Contain("0.70");
    }

    [Fact]
    public async Task AStrongTopScoreEmitsNoBlockEvenWhenOtherItemsAreWeak()
    {
        // The section HAS a direct answer. Saying otherwise would teach the model to hedge on evidence
        // it should trust -- the failure mode that makes this feature dangerous if it over-fires.
        var facts = new[] { MakeFact("f1"), MakeFact("f2") };
        var projected = await RunAsync(State(On, facts, [(facts[0], 0.97), (facts[1], 0.40)]));

        projected!.Blocks.Should().BeEmpty();
        projected.Annotations["f1"].IsNearMiss.Should().BeFalse();
        projected.Annotations["f2"].IsNearMiss.Should().BeTrue("it is still individually weak");
    }

    [Fact]
    public async Task AnUnscoreableSectionProducesNoScoresNoMarksAndNoBlock()
    {
        // THE void witness. A provider without the scored contract yields an empty score list against a
        // populated section; emitting abstention cues from that would report on wiring, not retrieval.
        var projected = await RunAsync(State(On, [MakeFact("f1"), MakeFact("f2")], factScores: []));

        projected.Should().BeNull("nothing was contributed at all");
    }

    [Fact]
    public async Task TracesUseTheMeasuredKneeAndNotTheSharedPrior()
    {
        // 0.88 clears the fact-side 0.85 prior but is BELOW the measured 0.92 trace knee. If traces
        // shared the fact threshold this would report a confident match, which is exactly the dead-zone
        // behaviour that made procedure retrieval never abstain.
        var trace = MakeTrace("t1");
        var projected = await RunAsync(State(On, traces: [trace], traceScores: [(trace, 0.88)]));

        projected!.Annotations["t1"].IsNearMiss.Should().BeTrue();
        projected.Blocks.Should().ContainSingle()
            .Which.SectionKey.Should().Be(ProjectionSectionKeys.Traces);
    }

    [Fact]
    public async Task ATraceAboveTheKneeIsADirectMatch()
    {
        var trace = MakeTrace("t1");
        var projected = await RunAsync(State(On, traces: [trace], traceScores: [(trace, 0.93)]));

        projected!.Annotations["t1"].IsNearMiss.Should().BeFalse();
        projected.Blocks.Should().BeEmpty();
    }

    [Fact]
    public async Task EachSectionIsJudgedIndependently()
    {
        // A weak fact section must not suppress or trigger a trace-section verdict.
        var fact = MakeFact("f1");
        var trace = MakeTrace("t1");
        var projected = await RunAsync(State(On, [fact], [(fact, 0.30)], [trace], [(trace, 0.99)]));

        projected!.Blocks.Should().ContainSingle()
            .Which.SectionKey.Should().Be(ProjectionSectionKeys.Facts);
    }

    [Fact]
    public async Task AThresholdRaisedByConfigurationIsHonoured()
    {
        // The default is a prior, not a measurement, so it must be movable per request.
        var fact = MakeFact("f1");
        var strict = On with { NearMissThreshold = 0.99 };

        var projected = await RunAsync(State(strict, [fact], [(fact, 0.95)]));

        projected!.Annotations["f1"].IsNearMiss.Should().BeTrue();
    }

    [Fact]
    public async Task AnEmptySectionContributesNothing()
    {
        (await RunAsync(State(On))).Should().BeNull();
    }
}
