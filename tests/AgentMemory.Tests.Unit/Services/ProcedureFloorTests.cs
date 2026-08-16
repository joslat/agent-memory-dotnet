using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Services.Projection;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// 30.3. The procedure similarity floor, and saying how long a promoted procedure is.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a safety feature wearing a tuning knob's clothes.</b> At the shared 0.7 default,
/// procedure retrieval never abstains — a sweep found every threshold from 0.00 to 0.86 behaves
/// identically, and the measured knee is 0.92. An agent handed a confident wrong procedure
/// <i>executes</i> it; an agent handed nothing investigates. Recalling no procedure is a strictly
/// better failure than recalling the wrong one, and at the shared default only the worse outcome was
/// reachable.
/// </para>
/// </remarks>
public sealed class ProcedureFloorTests
{
    private static readonly DateTimeOffset Stamp = DateTimeOffset.UnixEpoch;

    private static ReasoningTrace Procedure(string id, string? outcome, TraceKind kind = TraceKind.Procedure) => new()
    {
        TraceId = id, SessionId = "s1", Task = "book a flight",
        StartedAtUtc = Stamp, Outcome = outcome, Kind = kind,
    };

    private static ProjectionState State(params ReasoningTrace[] traces) => new()
    {
        Options = MemoryProjectionOptions.Default with { AnnotateMatchQuality = true },
        Scope = null,
        Entities = [],
        Facts = [],
        Preferences = [],
        Traces = traces,
        RecentMessages = [],
        RelevantMessages = [],
        EntityScores = [],
        FactScores = [],
        PreferenceScores = [],
        TraceScores = [],
    };

    // ── the floor ─────────────────────────────────────────────────────

    [Fact]
    public void TheDefaultIsNullSoNothingChangesForAnExistingCaller()
    {
        // Every sealed measurement was taken at the shared floor; a non-null default would silently
        // change what recall returns for everyone.
        RecallOptions.Default.MinTraceSimilarityScore.Should().BeNull();
        RecallOptions.Default.EffectiveTraceMinScore.Should().Be(RecallOptions.Default.MinSimilarityScore);
    }

    [Fact]
    public void AnUnsetTraceFloorResolvesToTheSharedScore()
    {
        var options = new RecallOptions { MinSimilarityScore = 0.55 };

        options.EffectiveTraceMinScore.Should().Be(0.55);
    }

    [Fact]
    public void ASetTraceFloorWinsOverTheSharedScore()
    {
        var options = new RecallOptions { MinSimilarityScore = 0.7, MinTraceSimilarityScore = 0.92 };

        options.EffectiveTraceMinScore.Should().Be(0.92);
        options.MinSimilarityScore.Should().Be(0.7, "the other categories keep the shared floor");
    }

    [Fact]
    public void TheMeasuredKneeIsSettableAndDistinctFromTheDeadZone()
    {
        // 0.86 is the top of the measured dead zone; 0.92 is the knee. Both must be expressible, and
        // they must not collapse to the same effective value.
        new RecallOptions { MinTraceSimilarityScore = 0.86 }.EffectiveTraceMinScore.Should().Be(0.86);
        new RecallOptions { MinTraceSimilarityScore = 0.92 }.EffectiveTraceMinScore.Should().Be(0.92);
    }

    // ── procedure shape ───────────────────────────────────────────────

    [Fact]
    public async Task APromotedProcedureRendersItsLength()
    {
        // The measured failure: replaying the archive task promoted a 16-call exploration, and rendered
        // as a bare outcome it is indistinguishable from a tight five-step recipe.
        var outcome = string.Join("\n", Enumerable.Range(1, 16).Select(i => $"call tool_{i}"));
        var state = State(Procedure("t1", outcome));

        await new ProcedureShapeProjectionFeature().ApplyAsync(state, CancellationToken.None);

        state.Build().Annotations["t1"].ProcedureShape.Should().Be("(16 steps)");
    }

    [Fact]
    public async Task AnEpisodeIsNotAnnotated()
    {
        // An episode's length is not a claim about reusability; annotating it would spend tokens saying
        // something true and useless.
        var state = State(Procedure("t1", "a\nb\nc", TraceKind.Episode));

        await new ProcedureShapeProjectionFeature().ApplyAsync(state, CancellationToken.None);

        state.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task ASingleStepProcedureIsNotAnnotated()
    {
        // "(1 steps)" is noise, and a one-line outcome is as likely a summary as a step list.
        var state = State(Procedure("t1", "did the thing"));

        await new ProcedureShapeProjectionFeature().ApplyAsync(state, CancellationToken.None);

        state.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task AnEmptyOutcomeIsNotAnnotated()
    {
        var state = State(Procedure("t1", null));

        await new ProcedureShapeProjectionFeature().ApplyAsync(state, CancellationToken.None);

        state.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void BlankLinesDoNotInflateTheCount()
    {
        // An inflated step count on a procedure is exactly the over-trust this feature exists to
        // prevent, so the counter is deliberately conservative.
        ProcedureShapeProjectionFeature.CountSteps("a\n\n\nb\n   \nc").Should().Be(3);
        ProcedureShapeProjectionFeature.CountSteps("   ").Should().Be(0);
        ProcedureShapeProjectionFeature.CountSteps(null).Should().Be(0);
    }

    [Fact]
    public async Task TheLengthLivesInItsOwnFieldAndLeavesOtherNotesAlone()
    {
        // Found in review: a first pass wrote the step count into SupersessionNote. The two say
        // unrelated things, and a later reader debugging supersession would have found a step count in
        // a property whose documentation promises a supersession chain.
        var state = State(Procedure("t1", "a\nb"));
        state.Annotate("t1", a => a with { SupersessionNote = "(since 2024-01-01; previously X)" });

        await new ProcedureShapeProjectionFeature().ApplyAsync(state, CancellationToken.None);

        var annotation = state.Build().Annotations["t1"];
        annotation.ProcedureShape.Should().Be("(2 steps)");
        annotation.SupersessionNote.Should().Be("(since 2024-01-01; previously X)",
            "another feature's note must survive untouched");
    }
}
