using System.Reflection;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// 30.11 reachability. The flags parse, the parsed values reach the adapter, and the defaults are the
/// historical call.
/// </summary>
/// <remarks>
/// <para>
/// The same pair of links <c>--extraction-seed</c> had exactly one of: it was in <c>KnownOptions</c>, so
/// the validator accepted it, and nothing read it — a run that asked to be seeded silently was not, and
/// the only way to find out was to pay for a run and inspect the artifact.
/// </para>
/// <para>
/// The default assertions matter as much as the wiring ones. Every sealed measurement in the archive was
/// taken with one unvoted, unforced answer call; a default that quietly changed either would make new
/// runs incomparable with all of them, which is a worse outcome than the feature not working.
/// </para>
/// </remarks>
public sealed class AnswerVotingReachabilityTests
{
    private static readonly Assembly Tool =
        typeof(AgentMemory.LongMemEval.LongMemEvalPreparationManifest).Assembly;

    private static object Parse(params string[] args)
    {
        var program = Tool.GetType("LongMemEvalProgram")!;
        var parse = program.GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Static)!;
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

    private static readonly string[] Minimal = ["--dataset", "d.json"];

    // ── the flags parse ───────────────────────────────────────────────

    [Fact]
    public void TheVoteCountParses()
    {
        Get<int>(Parse([.. Minimal, "--answer-votes", "3"]), "AnswerVotes").Should().Be(3);
    }

    [Fact]
    public void QuoteForcingParses()
    {
        Get<bool>(Parse([.. Minimal, "--quote-forcing"]), "QuoteForcing").Should().BeTrue();
    }

    [Fact]
    public void TheDefaultsAreTheHistoricalCall()
    {
        // One answer call, no format constraint -- the state every archived measurement was taken in.
        var options = Parse(Minimal);

        Get<int>(options, "AnswerVotes").Should().Be(1);
        Get<bool>(options, "QuoteForcing").Should().BeFalse();
    }

    [Fact]
    public void BothFlagsAreKnownToTheArgumentValidator()
    {
        // The validator's whole job is refusing options that would be ignored. An option it does not
        // know is rejected outright, so a flag readable by the parser but absent from this list is
        // unusable -- the mirror image of the --extraction-seed defect.
        var program = Tool.GetType("LongMemEvalProgram")!;
        var known = (string[])program
            .GetField("KnownOptions", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

        known.Should().Contain("--answer-votes");
        known.Should().Contain("--quote-forcing");
    }

    // ── the parsed values reach the adapter ───────────────────────────

    [Fact]
    public void TheAdapterOptionsCarryBoth()
    {
        var adapterOptions = Tool.GetType("AgentMemory.LongMemEval.LongMemEvalAdapterOptions")!;

        adapterOptions.GetProperty("AnswerVotes").Should().NotBeNull();
        adapterOptions.GetProperty("QuoteForcing").Should().NotBeNull();
    }

    [Fact]
    public void TheAdapterDefaultsToOneUnforcedCall()
    {
        var adapterOptions = Tool.GetType("AgentMemory.LongMemEval.LongMemEvalAdapterOptions")!;
        var instance = Activator.CreateInstance(adapterOptions)!;

        adapterOptions.GetProperty("AnswerVotes")!.GetValue(instance).Should().Be(1);
        adapterOptions.GetProperty("QuoteForcing")!.GetValue(instance).Should().Be(false);
    }

    [Fact]
    public void TheAdapterReadsBothOptions()
    {
        // The link that was broken for --extraction-seed: the option existed and the code never read
        // it. Source inspection, because "does this code path read this option" is not observable at
        // runtime without a live answer model.
        var source = AdapterSource();

        source.Should().Contain("_options.AnswerVotes");
        source.Should().Contain("_options.QuoteForcing");
    }

    [Fact]
    public void TheParsedValuesAreHandedToTheAdapter()
    {
        // THE middle link, and this test exists because its absence was proved: commenting out the
        // Program.cs assignment left every other test in this class green. Parsing the flag and reading
        // the option are two ends of a chain with a third piece between them, and that piece is exactly
        // where --extraction-seed was severed.
        var source = LiveLines(ProgramSource());

        source.Should().Contain("AnswerVotes = options.AnswerVotes");
        source.Should().Contain("QuoteForcing = options.QuoteForcing");
    }

    /// <summary>The source with comment lines stripped.</summary>
    /// <remarks>
    /// A substring assertion over raw source is satisfied by a <b>commented-out</b> occurrence, which is
    /// the most likely way this wire actually gets severed — someone disables a line while debugging and
    /// it never comes back. Found by probing: commenting the assignment left this test green until the
    /// filter was added.
    /// </remarks>
    private static string LiveLines(string source) =>
        string.Join(
            '\n',
            source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    [Fact]
    public void VotingClustersOnTheAnswerNotTheWholeQuoteForcedResponse()
    {
        // If it clustered on the raw response, two votes agreeing on the answer while citing different
        // quotes would count as a disagreement, and the two halves of this feature would fight each
        // other -- quote-forcing would make voting report noise it invented.
        AdapterSource().Should().Contain("AnswerTextOf(");
    }

    private static string AdapterSource() => ToolSource("AgentMemoryLongMemEvalAdapter.cs");

    private static string ProgramSource() => ToolSource("Program.cs");

    private static string ToolSource(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentMemory.slnx")))
            directory = directory.Parent;
        directory.Should().NotBeNull("the test must run inside the repository");

        return File.ReadAllText(Path.Combine(
            directory!.FullName, "tools", "AgentMemory.LongMemEval", fileName));
    }
}
