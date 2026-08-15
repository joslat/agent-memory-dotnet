using System.Reflection;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// 30.1. <c>--extraction-seed</c> must survive the argument parser on every verb that accepts it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This guard exists because its absence was the defect.</b> <c>--extraction-seed</c> was already
/// listed in the main verb's <c>KnownOptions</c>, so <c>LongMemEvalArgumentValidator</c> accepted it —
/// and nothing read it. A run that asked to be seeded silently was not, and the argument validator, the
/// one component whose entire job is refusing options that would be ignored, waved it through because
/// "known" and "honoured" were never the same list.
/// </para>
/// <para>
/// Guarding the profile wiring is not enough on its own: it proves the value reaches the extractor once
/// something supplies it, not that the command line supplies it. Both links are needed, and this is the
/// one that was broken.
/// </para>
/// </remarks>
public sealed class ExtractionSeedCommandLineTests
{
    private static object ParsePreparedPair(params string[] args)
    {
        var parse = typeof(LongMemEvalPreparedPairProgram)
            .GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Static)!;
        try
        {
            return parse.Invoke(null, [args])!;
        }
        catch (TargetInvocationException exception)
        {
            throw exception.InnerException!;
        }
    }

    private static int? SeedOf(object options) =>
        (int?)options.GetType().GetProperty("ExtractionSeed")!.GetValue(options);

    [Fact]
    public void PreparedPairAcceptsAndHonoursTheSeed()
    {
        SeedOf(ParsePreparedPair("--prepared-pair", "--dataset", "d.json", "--extraction-seed", "20260815"))
            .Should().Be(20260815);
    }

    [Fact]
    public void PreparedPairDefaultsToNoSeed()
    {
        // The state every sealed corpus was built in. If this ever defaulted to a value, a rebuild
        // would silently stop being comparable with the archive.
        SeedOf(ParsePreparedPair("--prepared-pair", "--dataset", "d.json")).Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-7)]
    public void ASeedMayBeZeroOrNegative(int seed)
    {
        // A sampling seed is not a count. Reusing the positive-integer parser here -- the obvious
        // shortcut, since almost every other numeric option is a count -- would reject two thirds of
        // the legal seed space and report it as a usage error.
        SeedOf(ParsePreparedPair(
                "--prepared-pair", "--dataset", "d.json", "--extraction-seed",
                seed.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .Should().Be(seed);
    }

    [Fact]
    public void ANonNumericSeedIsRefusedRatherThanSilentlyDropped()
    {
        // The failure mode this whole file is about: a value the parser cannot read must stop the run,
        // not fall back to "unseeded" while the operator believes otherwise.
        var parse = () => ParsePreparedPair(
            "--prepared-pair", "--dataset", "d.json", "--extraction-seed", "not-a-number");

        parse.Should().Throw<ArgumentException>().WithMessage("*--extraction-seed*integer*");
    }

    [Fact]
    public void TheSeedIsCarriedOnThePreparedPairOptionsRecord()
    {
        // Reflection-based, matching LongMemEvalPreparedBatchBridgeContractTests: the option must exist
        // on the record that the profile, the manifest and the drift check all read from.
        typeof(LongMemEvalPreparedPairProgram)
            .GetNestedType("PreparedPairOptions", BindingFlags.NonPublic)!
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(property => property.Name)
            .Should().Contain("ExtractionSeed");
    }

    [Fact]
    public void TheMainVerbAlsoCarriesTheSeedOnItsOptionsRecord()
    {
        // The verb where the defect actually lived: --extraction-seed was in KnownOptions and read by
        // nobody.
        var options = typeof(LongMemEvalProgram)
            .GetNestedType("Options", BindingFlags.NonPublic);

        options.Should().NotBeNull();
        options!.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(property => property.Name)
            .Should().Contain("ExtractionSeed");
    }

    [Fact]
    public void EveryVerbThatAdvertisesTheSeedActuallyParsesIt()
    {
        // THE drift guard, generalised. An option listed in a verb's KnownOptions is a promise that the
        // verb honours it -- the validator's whole purpose is to reject options that would be ignored,
        // so a name in that list and absent from the options record is the validator lying.
        foreach (var (program, optionsTypeName) in new[]
                 {
                     (typeof(LongMemEvalProgram), "Options"),
                     (typeof(LongMemEvalPreparedPairProgram), "PreparedPairOptions"),
                 })
        {
            var known = (string[])program
                .GetField("KnownOptions", BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null)!;
            if (!known.Contains("--extraction-seed", StringComparer.Ordinal)) continue;

            program.GetNestedType(optionsTypeName, BindingFlags.NonPublic)!
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(property => property.Name)
                .Should().Contain("ExtractionSeed",
                    $"{program.Name} advertises --extraction-seed in KnownOptions, so it must honour it");
        }
    }
}
