using AgentEval.Core;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Microsoft.Extensions.AI;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// The firing lever, which no harness has ever set — found while verifying the wiring for
/// firing-ablation v2 rather than after paying for it.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this closes.</b> <c>RecallOptions.ProspectiveFiring</c> is public, consumed by
/// <c>MemoryContextAssembler</c>, and covered by three dedicated unit-test classes. The benchmark
/// harness's single <c>RecallOptions</c> construction never assigned it — and never assigned
/// <c>ValidTime</c> either, which defaults to <see cref="ValidTimeMode.Ignore"/>. The assembler gates
/// firing on <b>both</b>, so every prospective number this project has produced — <c>due-window</c>
/// 1/18 included — was measured with the feature dark twice over. Sixth instance of
/// reachable-but-not-fed.
/// </para>
/// <para>
/// <b>Why this had to exist before the ablation, not after.</b> Firing-ablation v2 is specified as a
/// feature-dark arm against a firing-enabled arm, with the reading fixed both ways: an arm that is
/// still indistinguishable "would kill the firing feature's value claim". Run today, BOTH arms would
/// have been feature-dark, the result would have read indistinguishable, and a working feature would
/// have been retired on evidence that never tested it.
/// </para>
/// <para>
/// <b>Two flags, not one.</b> Enabling firing alone changes nothing; enabling both changes two things
/// at once. Only the third arm — valid-time on, firing off — separates them, and it is expressible
/// only because the levers stayed separate.
/// </para>
/// </remarks>
public sealed class ProspectiveFiringWiringTests
{
    private static TypedMemEvalProgram.TypedMemEvalRunOptions Parse(params string[] extra) =>
        TypedMemEvalProgram.Parse(["--typedmemeval", "prospective", .. extra]);

    private static AgentMemoryLongMemEvalAdapter Adapter(LongMemEvalAdapterOptions options) =>
        new(Substitute.For<IMemoryService>(), Substitute.For<IChatClient>(), "run-1", options);

    /// <summary>A minimal timestamped history — the shape every prospective question arrives as.</summary>
    private static TimestampedConversationHistory History() => new()
    {
        Turns =
        [
            new TimestampedConversationTurn(
                "I joined Riverside Fitness.", "Noted.",
                new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero), 0),
        ],
        QueryTime = new DateTimeOffset(2026, 5, 4, 9, 0, 0, TimeSpan.Zero),
    };

    [Theory]
    [InlineData("--current-valid-time")]
    [InlineData("--prospective-firing")]
    public void EachFlagIsAKnownOption(string flag) =>
        TypedMemEvalProgram.KnownOptions.Should().Contain(flag,
            "an unlisted option is rejected by the validator, so a mistyped run would spend money "
            + "on the wrong arm");

    [Fact]
    public void TheFlagsReachTheOptionsRecord()
    {
        var on = Parse("--current-valid-time", "--prospective-firing");

        on.CurrentValidTimeOnly.Should().BeTrue();
        on.ProspectiveFiring.Should().BeTrue();
    }

    /// <summary>The default arm leaves both off, so every sealed measurement stays reproducible.</summary>
    [Fact]
    public void TheDefaultArmLeavesBothOff()
    {
        var off = Parse();

        off.CurrentValidTimeOnly.Should().BeFalse();
        off.ProspectiveFiring.Should().BeFalse();
        off.Arm.IsDefault.Should().BeTrue(
            "every C-D row was taken on this path and must stay byte-identical to it");
    }

    /// <summary>
    /// The three arms the ablation needs are three DIFFERENT tokens on disk.
    /// </summary>
    /// <remarks>
    /// The arm is stamped into the filename. If valid-time-only and valid-time-plus-firing produced
    /// the same token, the two artifacts that separate the confound would be indistinguishable once
    /// the shell log is gone — which is the entire defect <see cref="TypedMemEvalArm"/> exists to
    /// close.
    /// </remarks>
    [Fact]
    public void TheThreeArmsAreThreeDistinctTokens()
    {
        var dark = Parse().Arm.FileToken();
        var validTimeOnly = Parse("--current-valid-time").Arm.FileToken();
        var firing = Parse("--current-valid-time", "--prospective-firing").Arm.FileToken();

        new[] { dark, validTimeOnly, firing }.Should().OnlyHaveUniqueItems();
        dark.Should().Be("default");
        validTimeOnly.Should().Be("vtcurrent");
        firing.Should().Be("vtcurrent-firing");
    }

    /// <summary>
    /// Firing WITHOUT current-valid-time is inert in the engine, and the token still says so.
    /// </summary>
    /// <remarks>
    /// This arm is a trap and the test exists to keep it visible: the assembler's gate is
    /// <c>ProspectiveFiring &amp;&amp; ValidTime == Current</c>, so this configuration runs the
    /// default retrieval path while its name claims a feature. The token naming it distinctly is what
    /// lets a reader see that an artifact came from here rather than from a real firing arm.
    /// </remarks>
    [Fact]
    public void FiringWithoutValidTimeIsItsOwnTokenBecauseItIsItsOwnMistake()
    {
        var inert = Parse("--prospective-firing").Arm;

        inert.FileToken().Should().Be("firing");
        inert.IsDefault.Should().BeFalse(
            "it is not the default arm, even though the engine will behave as though it were");
    }

    /// <summary>The arm description carries both, so the sidecar records what actually ran.</summary>
    [Fact]
    public void TheDescriptionCarriesBothLevers()
    {
        var describe = Parse("--current-valid-time", "--prospective-firing").Arm.Describe();

        describe.Should().Contain("current-valid-time-only=True");
        describe.Should().Contain("prospective-firing=True");
    }

    /// <summary>
    /// With both flags off the adapter is byte-identical to every run taken before them.
    /// </summary>
    /// <remarks>
    /// This is load-bearing, not hygiene. The firing ablation pairs its new arms against the
    /// <c>default</c> prospective row already on the board — an artifact produced BEFORE these flags
    /// existed. That pairing is only legitimate if an unflagged run still takes exactly the old path:
    /// the adapter previously left <c>ValidTime</c> unassigned (so <c>Ignore</c>) and
    /// <c>ProspectiveFiring</c> unassigned (so false), and the new code must land on the same two
    /// values rather than merely on plausible ones.
    /// </remarks>
    [Fact]
    public void BothLeversDefaultOffOnTheAdapterSoTheSealedDefaultArmStillPairs()
    {
        var adapter = new LongMemEvalAdapterOptions();

        adapter.CurrentValidTimeOnly.Should().BeFalse();
        adapter.ProspectiveFiring.Should().BeFalse();

        // The values those defaults produce are the ones the old code produced by omission.
        var recall = new RecallOptions
        {
            ValidTime = adapter.CurrentValidTimeOnly ? ValidTimeMode.Current : ValidTimeMode.Ignore,
            ProspectiveFiring = adapter.ProspectiveFiring,
        };

        recall.ValidTime.Should().Be(new RecallOptions().ValidTime);
        recall.ProspectiveFiring.Should().Be(new RecallOptions().ProspectiveFiring);
    }

    /// <summary>
    /// A firing arm on a TIMESTAMPED vertical is REFUSED, because firing cannot reach that path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gate <c>ProspectiveFiring &amp;&amp; ValidTime == Current</c> lives in
    /// <c>AssembleContextAsync</c> alone. <c>AssembleContextAsOfCoreAsync</c> contains no firing
    /// block — zero references to <c>ProspectiveFiring</c>, <c>GetDueFactsAsync</c> or a due section.
    /// Every prospective question carries a <c>QuestionDate</c>, so every one of them routes through
    /// <c>RecallAsOfAsync</c> and CANNOT fire however the arm is configured.
    /// </para>
    /// <para>
    /// So wiring the flag made firing <b>requestable</b> without making it <b>reachable</b>. An arm
    /// named <c>vtcurrent-firing</c> that quietly ran the ordinary as-of path would report an
    /// off-state under an on-state's name — and firing-ablation v2's pre-registered reading for
    /// "indistinguishable" is that it KILLS the feature's value claim. That verdict must not be
    /// reachable by accident, so the adapter refuses instead, alongside the GraphRAG refusal that
    /// already existed for the same reason.
    /// </para>
    /// </remarks>
    [Fact]
    public void AFiringArmOnATimestampedVerticalIsRefusedRatherThanSilentlyDark()
    {
        var adapter = Adapter(new LongMemEvalAdapterOptions { ProspectiveFiring = true });

        var inject = () => adapter.InjectTimestampedConversationHistory(History());

        inject.Should().Throw<InvalidOperationException>()
            .WithMessage("*does not implement prospective firing*");
    }

    /// <summary>The same history is accepted when firing is off, so nothing else regressed.</summary>
    [Fact]
    public void TheSameTimestampedHistoryIsAcceptedWithFiringOff()
    {
        var adapter = Adapter(new LongMemEvalAdapterOptions());

        var inject = () => adapter.InjectTimestampedConversationHistory(History());

        inject.Should().NotThrow();
    }

    /// <summary>
    /// The engine's gate is what these flags exist to satisfy — asserted here, not assumed.
    /// </summary>
    /// <remarks>
    /// A wiring test that stopped at the options record would prove the flag parses, not that the
    /// feature can fire. <see cref="RecallOptions.ProspectiveFiring"/> defaulting false and
    /// <see cref="RecallOptions.ValidTime"/> defaulting to Ignore are the two facts that made every
    /// prior prospective measurement an off-state, so both are pinned.
    /// </remarks>
    [Fact]
    public void TheEngineDefaultsAreBothOffWhichIsWhyThisWasNeverMeasured()
    {
        var defaults = new RecallOptions();

        defaults.ProspectiveFiring.Should().BeFalse();
        defaults.ValidTime.Should().Be(ValidTimeMode.Ignore,
            "firing reads a fact's valid-time window, and a recall ignoring valid time has none to "
            + "read — so the feature was dark on BOTH conditions, not one");
    }
}
