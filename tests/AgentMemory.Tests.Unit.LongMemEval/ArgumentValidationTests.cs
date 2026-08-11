using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// An unrecognised option must stop the run, not be ignored.
/// </summary>
/// <remarks>
/// <para>
/// This cost a real measurement. `--mode structured` was passed where the option is
/// `--memory-mode`; the argument was accepted and dropped, the run executed in Raw mode with no
/// extraction, and the resulting report looked like a successful measurement of something nobody
/// asked for. The vector-yield block read <c>Searches = 0</c>, which was the correct answer to the
/// question the run actually asked.
/// </para>
/// <para>
/// The harness already fails closed on sealed manifests, extraction accounting and schema state —
/// all of which caught genuine problems. It did not fail closed on its own command line, which is
/// the one input a human types by hand and therefore the one most likely to be wrong.
/// </para>
/// </remarks>
public sealed class ArgumentValidationTests
{
    private static readonly string[] Known = ["--dataset", "--questions", "--memory-mode", "--seed"];

    [Fact]
    public void AnUnknownOptionIsRejected()
    {
        var act = () => LongMemEvalArgumentValidator.Validate(
            ["--dataset", "d.json", "--mode", "structured"], Known);

        act.Should().Throw<ArgumentException>().WithMessage("*--mode*");
    }

    [Fact]
    public void TheErrorSUGGESTSTheIntendedOption()
    {
        // ASSERTS THE SUGGESTION, not merely that the name appears. The first version of this test
        // checked for "*--memory-mode*", which the printed known-options list satisfies on its own -
        // so it passed while the suggestion logic was NOT firing for the exact typo it exists to
        // catch. Levenshtein puts `--mode` 7 edits from `--memory-mode`, which no sane threshold
        // admits; containment catches it, because an abbreviation-style mistake is a substring.
        var act = () => LongMemEvalArgumentValidator.Validate(
            ["--dataset", "d.json", "--mode", "structured"], Known);

        act.Should().Throw<ArgumentException>().WithMessage("*Did you mean --memory-mode?*");
    }

    [Fact]
    public void ASingleCharacterSlipIsStillSuggestedByDistance()
    {
        // Containment alone would miss this, so both rules earn their place.
        var act = () => LongMemEvalArgumentValidator.Validate(["--datase", "d.json"], Known);

        act.Should().Throw<ArgumentException>().WithMessage("*Did you mean --dataset?*");
    }

    [Fact]
    public void KnownOptionsPassUntouched()
    {
        var act = () => LongMemEvalArgumentValidator.Validate(
            ["--dataset", "d.json", "--questions", "12", "--memory-mode", "structured"], Known);

        act.Should().NotThrow();
    }

    [Fact]
    public void OptionVALUESAreNeverMistakenForOptions()
    {
        // A value that begins with a dash - a negative number, or a path - must not be read as an
        // unknown option. Validating tokens without knowing which are values would trade a silent
        // failure for a false alarm, which is not an improvement.
        var act = () => LongMemEvalArgumentValidator.Validate(
            ["--seed", "-1", "--dataset", "--weird-looking-value"], Known);

        act.Should().NotThrow();
    }

    [Fact]
    public void AnUnknownOptionWithNoCloseMatchStillReportsTheKnownSet()
    {
        var act = () => LongMemEvalArgumentValidator.Validate(
            ["--dataset", "d.json", "--zzzzzz"], Known);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*--zzzzzz*").And.Message.Should().Contain("--questions");
    }
}
