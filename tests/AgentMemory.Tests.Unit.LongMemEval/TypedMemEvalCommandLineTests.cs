using System.Reflection;
using AgentEval.Memory.External.TypedMemEval;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// 30.9c prereq C: every option the <c>--typedmemeval</c> verb advertises survives its parser and
/// lands on its options record.
/// </summary>
/// <remarks>
/// The discipline is ExtractionSeedCommandLineTests', for the reason that file documents: an
/// option in <c>KnownOptions</c> is a promise that the verb honours it — the argument validator's
/// whole purpose is refusing options that would be ignored, so a name in that list and absent from
/// the options record is the validator lying.
/// </remarks>
public sealed class TypedMemEvalCommandLineTests
{
    private static TypedMemEvalProgram.TypedMemEvalRunOptions Parse(params string[] args)
        => TypedMemEvalProgram.Parse(args);

    [Theory]
    [InlineData("prospective", TypedMemEvalVertical.Prospective)]
    [InlineData("episodic", TypedMemEvalVertical.Episodic)]
    [InlineData("arithmetic", TypedMemEvalVertical.Arithmetic)]
    [InlineData("workingmemory", TypedMemEvalVertical.WorkingMemory)]
    [InlineData("forgetting", TypedMemEvalVertical.Forgetting)]
    public void EachVerticalSlugParsesToItsVertical(string slug, TypedMemEvalVertical expected)
    {
        Parse("--typedmemeval", slug).Verticals.Should().Equal(expected);
    }

    [Fact]
    public void AllRunsEveryVerticalInDeclarationOrder()
    {
        // Read from the descriptor table, never a literal list: when 0.23 (or any later revision)
        // changes the family, this test must keep describing the shipped set.
        Parse("--typedmemeval", "all").Verticals.Should().Equal(
            TypedMemEvalVerticals.All.Select(descriptor => descriptor.Vertical));
    }

    [Fact]
    public void AnUnknownVerticalIsRefusedWithTheKnownSlugs()
    {
        var act = () => Parse("--typedmemeval", "prospektive");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*prospektive*").And.Message.Should().Contain("prospective");
    }

    [Fact]
    public void AMissingVerticalValueIsRefused()
    {
        var act = () => Parse("--typedmemeval");

        act.Should().Throw<ArgumentException>().WithMessage("*--typedmemeval requires a value*");
    }

    [Fact]
    public void DefaultsAreWholeCorpusUnseededSingleRunAgentArm()
    {
        var options = Parse("--typedmemeval", "forgetting");

        options.MaxQuestions.Should().BeNull("null runs the whole corpus, whatever size it ships at");
        options.RandomSeed.Should().BeNull();
        options.AnswerSeed.Should().BeNull();
        options.Runs.Should().Be(1);
        options.Oracle.Should().BeFalse();
        options.Control.Should().BeFalse();
    }

    [Fact]
    public void MaxQuestionsIsCarried()
    {
        Parse("--typedmemeval", "forgetting", "--max-questions", "4")
            .MaxQuestions.Should().Be(4);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("four")]
    public void MaxQuestionsRefusesNonPositiveOrNonNumericValues(string value)
    {
        var act = () => Parse("--typedmemeval", "forgetting", "--max-questions", value);

        act.Should().Throw<ArgumentException>().WithMessage("*--max-questions*positive*");
    }

    [Theory]
    [InlineData("--random-seed")]
    [InlineData("--answer-seed")]
    public void ASeedMayBeZeroOrNegative_AndNonNumericIsRefusedNotDropped(string option)
    {
        // A sampling seed is not a count; and a value the parser cannot read must stop the run
        // rather than fall back to "unseeded" while the operator believes otherwise.
        int? Of(TypedMemEvalProgram.TypedMemEvalRunOptions options) =>
            option == "--random-seed" ? options.RandomSeed : options.AnswerSeed;

        Of(Parse("--typedmemeval", "forgetting", option, "0")).Should().Be(0);
        Of(Parse("--typedmemeval", "forgetting", option, "-7")).Should().Be(-7);
        var act = () => Parse("--typedmemeval", "forgetting", option, "not-a-number");
        act.Should().Throw<ArgumentException>().WithMessage($"*{option}*integer*");
    }

    [Fact]
    public void RunsIsCarried_AndBandingWithoutASeedIsRefusedBeforeAnySpend()
    {
        Parse("--typedmemeval", "forgetting", "--runs", "3", "--random-seed", "42")
            .Runs.Should().Be(3);

        // TypedMemEvalRunSet.Summarize refuses to band runs that drew different questions; an
        // unseeded multi-run would discover that only AFTER paying for every run.
        var act = () => Parse("--typedmemeval", "forgetting", "--runs", "3");
        act.Should().Throw<ArgumentException>().WithMessage("*--runs*--random-seed*");
    }

    [Fact]
    public void OracleAndControlFlagsAreCarried()
    {
        Parse("--typedmemeval", "forgetting", "--oracle").Oracle.Should().BeTrue();
        Parse("--typedmemeval", "prospective", "--control").Control.Should().BeTrue();
    }

    [Theory]
    [InlineData("episodic")]
    [InlineData("all")]
    public void ControlIsTheProspectivePairsArmOnly(string vertical)
    {
        var act = () => Parse("--typedmemeval", vertical, "--control");

        act.Should().Throw<ArgumentException>().WithMessage("*--control*prospective*");
    }

    [Fact]
    public void AnUnknownOptionIsRefusedWithASuggestion()
    {
        // The same failure mode the shared validator exists for: a typo must stop the run, not be
        // silently ignored while the report claims a measurement nobody configured.
        var act = () => Parse("--typedmemeval", "forgetting", "--max-question", "4");

        act.Should().Throw<ArgumentException>().WithMessage("*Did you mean --max-questions?*");
    }

    [Fact]
    public void TheMainVerbAdvertisesTheTypedMemEvalSwitch()
    {
        // Dispatch happens before the main parser runs, so without this listing a typo'd verb
        // switch would fall through to the default verb and silently measure something else.
        var known = (string[])typeof(LongMemEvalProgram)
            .GetField("KnownOptions", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

        known.Should().Contain("--typedmemeval");
    }

    [Fact]
    public void EveryAdvertisedOptionIsCarriedOnTheOptionsRecord()
    {
        // THE drift guard, in the ExtractionSeedCommandLineTests shape: an option listed in
        // KnownOptions and absent from the options record is the validator lying. The map below
        // must name every known option, so adding an option without carrying it fails here.
        var carriedBy = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["--typedmemeval"] = "Verticals",
            ["--max-questions"] = "MaxQuestions",
            ["--random-seed"] = "RandomSeed",
            ["--answer-seed"] = "AnswerSeed",
            ["--runs"] = "Runs",
            ["--oracle"] = "Oracle",
            ["--control"] = "Control",
            // 30.9c. Both Wave-C switches are carried by the same record property, which is the
            // point: a feature flag and the schema extension it needs must travel together, or the
            // DDL those writes need is absent and the feature reads as broken rather than dark.
            ["--working-memory"] = "Phase30",
            ["--arithmetic-memory"] = "Phase30",
            // 30.9c re-measure arms. Each is a retrieval-side lever with its own record property, so
            // an arm that was requested but not carried fails here rather than producing a report
            // indistinguishable from the control it was supposed to differ from.
            ["--rescue-short-owner-results"] = "RescueShortOwnerResults",
            ["--fact-weighted-budget"] = "FactWeightedBudget",
        };

        TypedMemEvalProgram.KnownOptions.Should().BeEquivalentTo(
            carriedBy.Keys,
            "every advertised option must be mapped to the record property that honours it");
        var properties = typeof(TypedMemEvalProgram.TypedMemEvalRunOptions)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(property => property.Name);
        properties.Should().Contain(carriedBy.Values);
    }
}
