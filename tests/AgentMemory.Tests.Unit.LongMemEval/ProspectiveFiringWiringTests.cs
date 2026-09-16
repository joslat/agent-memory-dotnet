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
    /// A firing arm on a TIMESTAMPED vertical is now ACCEPTED, because firing reaches that path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This test previously asserted a REFUSAL, and the inversion is the point.</b> The refusal
    /// existed because <c>AssembleContextAsOfCoreAsync</c> had no firing block at all, so an arm named
    /// <c>vtcurrent-firing</c> would have run the ordinary as-of path and reported an off-state under
    /// an on-state's name — reaching the ablation's "kills the value claim" verdict by accident.
    /// </para>
    /// <para>
    /// D2 built the capability rather than relaxing the check: <c>GetDueFactsAsOfAsync</c>, bounded by
    /// BOTH clocks with the same two transaction predicates as <c>SearchFactsAsOf</c>, verified
    /// against live Neo4j by four integration tests — including a control proving the exclusions are
    /// the clocks doing work and not a query matching nothing. <b>The guard narrowed by truth, never
    /// by relaxation</b>, which is the rule W1c set in this same method.
    /// </para>
    /// </remarks>
    [Fact]
    public void AFiringArmOnATimestampedVerticalIsNowAcceptedBecauseFiringReachesThatPath()
    {
        var adapter = Adapter(new LongMemEvalAdapterOptions { ProspectiveFiring = true });

        var inject = () => adapter.InjectTimestampedConversationHistory(History());

        inject.Should().NotThrow();
    }

    /// <summary>
    /// GraphRAG on the as-of path is STILL refused — only the claim that became false was narrowed.
    /// </summary>
    /// <remarks>
    /// Lifting one refusal must not loosen the other. GraphRAG-as-of remains unimplemented, so its
    /// claim is still true and its guard still binds.
    /// </remarks>
    [Fact]
    public void GraphRagOnTheAsOfPathIsStillRefused()
    {
        var adapter = Adapter(new LongMemEvalAdapterOptions { GraphRagItems = 5 });

        var inject = () => adapter.InjectTimestampedConversationHistory(History());

        inject.Should().Throw<InvalidOperationException>().WithMessage("*GraphRAG*");
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

/// <summary>
/// The entity-linking lever — seventh reachable-but-never-fed instance, found by checking the NEXT
/// queued run's wiring rather than the current one's.
/// </summary>
/// <remarks>
/// <para>
/// <c>ExtractionOptions.LinkFactsToEntities</c> is public and unit-tested;
/// <c>LongMemEvalMemoryProfile</c> configured <c>Extraction { DerivedMemory, SupersedeReplacedFacts }</c>
/// and not this, so no run could set it. Every store probe this project has taken reports
/// <c>0 entity(ies)</c> on every line, across every vertical.
/// </para>
/// <para>
/// It gates the D1 recommendation — entity linking before the conjunction router — whose evidence is
/// that <c>alias-then-count</c> produced <b>14 undercounts and zero overcounts</b> across all 15
/// questions: one-directional loss, the signature of a missing join rather than a bad ranking.
/// </para>
/// <para>
/// <b>Unlike every other arm lever, this one changes INGESTION.</b> Two arms differing by it are not
/// two readings of one store — they are two stores. That is why it must be nameable on disk.
/// </para>
/// </remarks>
public sealed class EntityLinkingWiringTests
{
    private static TypedMemEvalProgram.TypedMemEvalRunOptions Parse(params string[] extra) =>
        TypedMemEvalProgram.Parse(["--typedmemeval", "conjunction", .. extra]);

    [Fact]
    public void TheFlagIsAKnownOption() =>
        TypedMemEvalProgram.KnownOptions.Should().Contain("--link-fact-entities");

    [Fact]
    public void TheFlagReachesTheOptionsRecord() =>
        Parse("--link-fact-entities").LinkFactsToEntities.Should().BeTrue();

    [Fact]
    public void TheDefaultArmLeavesItOff()
    {
        var off = Parse();

        off.LinkFactsToEntities.Should().BeFalse();
        off.Arm.IsDefault.Should().BeTrue(
            "every C-D row was ingested on this path and must stay reproducible from it");
    }

    /// <summary>An entity-linked run is a DIFFERENT STORE and its filename must say so.</summary>
    [Fact]
    public void AnEntityLinkedArmIsItsOwnTokenBecauseItIsItsOwnCorpus()
    {
        Parse("--link-fact-entities").Arm.FileToken().Should().Be("entlink");
        Parse().Arm.FileToken().Should().Be("default");
    }

    [Fact]
    public void TheDescriptionCarriesTheLever() =>
        Parse("--link-fact-entities").Arm.Describe()
            .Should().Contain("link-facts-to-entities=True");
}

/// <summary>
/// Provenance must name the commit the RUN started on, not the one HEAD reached by the time it
/// finished.
/// </summary>
/// <remarks>
/// Caught on a live run rather than in review: the temporal band launched at <c>f520c7c</c> and two
/// commits landed while it was still ingesting. Because the sha was read when each artifact was
/// WRITTEN — four to six hours later, three times over for a banded <c>--runs 3</c> — the artifacts
/// would have claimed a commit whose binary never executed. The project's own note on this field says
/// the commit "is what makes a six-month-old number re-derivable at all", which is exactly the
/// property a write-time read destroys.
/// </remarks>
public sealed class ProvenanceCommitCaptureTests
{
    [Fact]
    public void TheShaIsCapturedOnceAtStartupRatherThanPerArtifact()
    {
        var field = typeof(TypedMemEvalProgram).GetField(
            "StartupGitSha",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        field.Should().NotBeNull(
            "the sha must live in a static initialiser that runs before the first run starts");
        field!.IsInitOnly.Should().BeTrue(
            "a mutable field could be reassigned mid-band and reintroduce the drift this closes");
    }

    /// <summary>Nothing reads the sha at write time any more.</summary>
    /// <remarks>
    /// The defect was a single call site, so a single call site is what must not come back. Asserted
    /// against the source rather than behaviour because the failure is invisible at runtime — a wrong
    /// commit is still a well-formed string.
    /// </remarks>
    [Fact]
    public void NoProvenanceWriterCallsReadGitShaDirectly()
    {
        var source = FindSource("TypedMemEvalProgram.cs");

        source.Should().NotContain("commit = ReadGitSha()",
            "provenance must use the startup capture; reading HEAD when the artifact is written is "
            + "the drift this closes");
    }

    private static string FindSource(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "tools")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the repository root must be locatable from the test output");
        var path = Path.Combine(
            directory!.FullName, "tools", "AgentMemory.LongMemEval", fileName);

        File.Exists(path).Should().BeTrue($"{fileName} must be where this test expects it");
        return File.ReadAllText(path);
    }
}

/// <summary>
/// The harness binary must carry the commit it was BUILT from, not the one checked out when it ran.
/// </summary>
/// <remarks>
/// <para>
/// A banded run on 2026-09-15 made the difference visible: three members, <b>one</b> Release binary,
/// and two different recorded commits — because branches moved between members while provenance was
/// reading <c>.git/HEAD</c>. The first fix moved that read from artifact-write time to process start,
/// which was strictly better and still answered the wrong question.
/// </para>
/// <para>
/// The stamp is what answers "what produced these numbers". This test exists so it cannot quietly
/// stop being applied: a build that loses the <c>SourceRevisionId</c> target would otherwise go on
/// writing provenance with a null commit and nothing would say so.
/// </para>
/// </remarks>
public sealed class BuildCommitStampTests
{
    [Fact]
    public void TheHarnessAssemblyCarriesA40CharacterCommitSha()
    {
        var informational = typeof(TypedMemEvalProgram).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>()
            .SingleOrDefault()?.InformationalVersion;

        informational.Should().NotBeNull();

        var plus = informational!.IndexOf('+', StringComparison.Ordinal);
        plus.Should().BeGreaterThan(0,
            "the build must append +<SourceRevisionId>; without it provenance cannot name the binary "
            + "that produced a number, and a long run's checkout will have moved on");

        var sha = informational[(plus + 1)..];
        sha.Should().HaveLength(40).And.Subject.Should().MatchRegex("^[0-9a-f]{40}$");
    }

    /// <summary>The provenance field reads that stamp rather than the working tree.</summary>
    /// <remarks>
    /// Asserted against the source because the failure is invisible in the output: a working-tree sha
    /// is still a well-formed sha, and it is wrong in exactly the cases that matter — long runs, and
    /// runs whose branch moved underneath them.
    /// </remarks>
    [Fact]
    public void ProvenanceNamesTheBinaryNotTheCheckout()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "tools")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull();
        var source = File.ReadAllText(Path.Combine(
            directory!.FullName, "tools", "AgentMemory.LongMemEval", "TypedMemEvalProgram.cs"));

        source.Should().Contain("commit = BuildCommitSha",
            "the authoritative field must be the binary's stamp");
        source.Should().Contain("workingTreeHeadAtStart = StartupGitSha",
            "the checkout is kept as a SEPARATE diagnostic so a reader can see when the two differ — "
            + "which means the run used a binary that does not match the tree");
    }
}

/// <summary>
/// The READ side of the identity edge — the eighth lever no harness could set.
/// </summary>
/// <remarks>
/// <para>
/// <c>NodeDistanceReranker</c> walks <c>[:RELATED_TO|ABOUT*..4]</c>, gated on
/// <c>MemoryOptions.NodeDistanceReranking</c>. That gate was set by the MCP host's environment
/// variable and by nothing under the TypedMemEval verb, so <b>no benchmark run in this project's
/// history has had structural re-ranking on</b> — and until Wave E-1 wrote 739 <c>ABOUT</c> edges,
/// no store this library built contained one for it to follow.
/// </para>
/// <para>
/// <b>The edge and its reader have never met.</b> E-1 measured the write side working (79% of facts
/// linked) and the census flat — undercounts 14→14 — which says the join exists and recall ignores
/// it. This is the lever that would stop it being ignored, and it is why the two options are only
/// informative together: the edge without this gate has no reader, and this gate without the edge
/// has nothing to follow.
/// </para>
/// </remarks>
public sealed class NodeDistanceRerankingWiringTests
{
    private static TypedMemEvalProgram.TypedMemEvalRunOptions Parse(params string[] extra) =>
        TypedMemEvalProgram.Parse(["--typedmemeval", "conjunction", .. extra]);

    [Fact]
    public void TheFlagIsAKnownOption() =>
        TypedMemEvalProgram.KnownOptions.Should().Contain("--node-distance-rerank");

    [Fact]
    public void TheFlagReachesTheOptionsRecord() =>
        Parse("--node-distance-rerank").NodeDistanceReranking.Should().BeTrue();

    [Fact]
    public void TheDefaultArmLeavesItOff()
    {
        var off = Parse();

        off.NodeDistanceReranking.Should().BeFalse();
        off.Arm.IsDefault.Should().BeTrue("every recorded measurement was taken without it");
    }

    /// <summary>
    /// The pair gets a compound token, and each half is distinguishable on disk.
    /// </summary>
    /// <remarks>
    /// Three artifacts must never be confusable: the edge with no reader (`entlink`), the reader with
    /// no edge (`noderank`), and both (`entlink-noderank`). The middle one is the trap — it names a
    /// feature while traversing a store that contains nothing for it to traverse.
    /// </remarks>
    [Fact]
    public void EachHalfAndThePairAreDistinctTokens()
    {
        Parse("--link-fact-entities").Arm.FileToken().Should().Be("entlink");
        Parse("--node-distance-rerank").Arm.FileToken().Should().Be("noderank");
        Parse("--link-fact-entities", "--node-distance-rerank").Arm.FileToken()
            .Should().Be("entlink-noderank");
    }

    [Fact]
    public void TheDescriptionCarriesTheLever() =>
        Parse("--node-distance-rerank").Arm.Describe()
            .Should().Contain("node-distance-reranking=True");
}

/// <summary>
/// <c>--runs</c> above 1 is refused: band members would share one store and degrade each other.
/// </summary>
/// <remarks>
/// <para>
/// Measured on a temporal ×3 attempt stopped at run 3. All members ingest into the SAME container,
/// so the store grows monotonically — run 1 searched 5,129 facts, run 2 searched 10,324. Owner
/// scoping keeps results CORRECT (no foreign fact is ever returned) but the indexed path takes a
/// <b>global</b> top-K and filters to the owner afterwards, so foreign rows consume the budget
/// first. Mean returned fell 4.72 → 1.61, starved searches rose 5 → 29, and the scores fell with
/// them: 54.0% then 36.0%, against 62.0% for the identical question set on an uncontaminated store.
/// </para>
/// <para>
/// A band whose members degrade monotonically measures store growth — the one thing banding exists
/// to rule out. The flag is refused rather than left to produce a plausible-looking band.
/// </para>
/// </remarks>
public sealed class BandingStoreIsolationTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void RunsAboveOneIsRefusedBecauseMembersWouldShareAStore(int runs)
    {
        var parse = () => TypedMemEvalProgram.Parse(
            ["--typedmemeval", "temporal", "--random-seed", "20260821",
             "--runs", runs.ToString(System.Globalization.CultureInfo.InvariantCulture)]);

        parse.Should().Throw<ArgumentException>()
            .WithMessage("*REFUSED*shared store*");
    }

    /// <summary>A single run is untouched — every sealed measurement was taken this way.</summary>
    [Fact]
    public void ASingleRunStillParses()
    {
        var parse = () => TypedMemEvalProgram.Parse(
            ["--typedmemeval", "temporal", "--random-seed", "20260821", "--runs", "1"]);

        parse.Should().NotThrow();
    }

    /// <summary>The seed check still fires first, so its message is not lost behind the new one.</summary>
    [Fact]
    public void TheUnseededBandStillFailsOnTheSeedFirst()
    {
        var parse = () => TypedMemEvalProgram.Parse(["--typedmemeval", "temporal", "--runs", "3"]);

        parse.Should().Throw<ArgumentException>().WithMessage("*requires --random-seed*");
    }
}
