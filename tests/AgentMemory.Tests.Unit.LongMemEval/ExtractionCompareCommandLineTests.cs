using System.Reflection;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// 30.6 sub-step 0. The predicate-vocabulary A/B flags survive the command line and reach the extractor.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written because the identical defect was found on this exact verb three tasks ago.</b>
/// <c>--extraction-seed</c> was listed in <c>KnownOptions</c>, so the argument validator — the one
/// component whose entire job is refusing options that would be ignored — accepted it, and nothing read
/// it. A run that asked to be seeded silently was not, and the only way to discover that was to pay for
/// a run and read the artifact.
/// </para>
/// <para>
/// So both directions are held here: the flag is parsed, <i>and</i> the parsed value reaches
/// <c>UsePredicateVocabulary</c> on the extraction options. Neither link is sufficient alone.
/// </para>
/// </remarks>
public sealed class ExtractionCompareCommandLineTests
{
    private static readonly Type Program =
        typeof(AgentMemory.LongMemEval.LongMemEvalPreparationManifest).Assembly
            .GetType("AgentMemory.LongMemEval.LongMemEvalExtractionCompareProgram")!;

    private static object Parse(params string[] args)
    {
        var parse = Program.GetMethod("ParseOptions", BindingFlags.NonPublic | BindingFlags.Static)!;
        try
        {
            return parse.Invoke(null, [args])!;
        }
        catch (TargetInvocationException exception)
        {
            throw exception.InnerException!;
        }
    }

    private static T Get<T>(object options, string name) =>
        (T)options.GetType().GetProperty(name)!.GetValue(options)!;

    private static string ResolveOutput(object options)
    {
        var resolve = Program.GetMethod(
            "ResolveOutputPath", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)resolve.Invoke(
            null, [options, new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero)])!;
    }

    // ── the flags parse ───────────────────────────────────────────────

    [Fact]
    public void TheVocabularyAbModeIsRecognised()
    {
        Get<bool>(Parse("--dataset", "d.json", "--vocabulary-ab"), "VocabularyAb").Should().BeTrue();
    }

    [Fact]
    public void ThePredicateVocabularyFlagIsRecognised()
    {
        Get<bool>(Parse("--dataset", "d.json", "--use-predicate-vocabulary"), "UsePredicateVocabulary")
            .Should().BeTrue();
    }

    [Fact]
    public void BothDefaultToOff()
    {
        // The state every archived extraction-compare artifact was produced in. A default flip here
        // would make new runs quietly incomparable with the archive.
        var options = Parse("--dataset", "d.json");

        Get<bool>(options, "VocabularyAb").Should().BeFalse();
        Get<bool>(options, "UsePredicateVocabulary").Should().BeFalse();
    }

    [Fact]
    public void TheTwoMeasurementModesRefuseToRunTogether()
    {
        // --repeat measures an arm against ITSELF; --vocabulary-ab measures two arms against each
        // other. Running both would produce one artifact answering neither question, and the operator
        // would not find out until reading it.
        var act = () => Parse("--dataset", "d.json", "--repeat", "--vocabulary-ab");

        act.Should().Throw<ArgumentException>().WithMessage("*separately*");
    }

    // ── each mode writes to its own artifact ──────────────────────────

    [Theory]
    [InlineData(new[] { "--dataset", "d.json" }, "extraction-compare")]
    [InlineData(new[] { "--dataset", "d.json", "--repeat" }, "extraction-self-agreement")]
    [InlineData(new[] { "--dataset", "d.json", "--vocabulary-ab" }, "predicate-vocabulary-ab")]
    public void EachModeNamesItsOwnArtifact(string[] args, string expectedPrefix)
    {
        // Three different questions must not land on one filename. An A/B overwriting a self-agreement
        // run destroys exactly the baseline the A/B has to be read against.
        ResolveOutput(Parse(args)).Should().Contain(expectedPrefix);
    }

    [Fact]
    public void AnExplicitOutputPathWins()
    {
        ResolveOutput(Parse("--dataset", "d.json", "--vocabulary-ab", "--output", "custom.json"))
            .Should().Be("custom.json");
    }

    // ── the parsed flag actually reaches the extractor ────────────────

    [Fact]
    public void TheRunPathAcceptsThePredicateVocabularyFlag()
    {
        // The link that was broken for --extraction-seed: the option existed on the extractor and the
        // verb never passed it, so the flag was unreachable from the command line however well it
        // parsed.
        var runPath = Program.GetMethod("RunPathAsync", BindingFlags.NonPublic | BindingFlags.Static)!;

        runPath.GetParameters().Select(p => p.Name)
            .Should().Contain("usePredicateVocabulary");
    }

    [Fact]
    public void TheReportRecordsWhichArmAndSeedProducedIt()
    {
        // A number without its configuration is not a measurement. Both fields are on the report type
        // so an artifact can be cited later without trusting anyone's memory of how it was produced.
        var report = Program.GetNestedType(
            "PredicateVocabularyAbReport", BindingFlags.NonPublic | BindingFlags.Public)!;

        report.GetProperty("Arm").Should().NotBeNull();
        report.GetProperty("ExtractionSeed").Should().NotBeNull();
        report.GetProperty("PredicateFragmentationOn").Should().NotBeNull();
        report.GetProperty("SameArmSelfAgreementReference").Should().NotBeNull();
    }
}
