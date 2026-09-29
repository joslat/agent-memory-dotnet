using System.Text.RegularExpressions;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// Every self-parsing verb rejects an option it does not read, and accepts every option it does.
/// </summary>
/// <remarks>
/// Sixteen verbs dispatched before any validator and ignored unknown flags. The fix is a table, and a
/// table can go stale. So the second half of this class reads each verb's source and requires every
/// <c>"--…"</c> literal it contains to be in the table. A verb that learns an option without the table
/// learning it fails here, and never rejects a valid command at run time.
/// </remarks>
public sealed class VerbOptionTableTests
{
    public static TheoryData<string> Verbs() => new(LongMemEvalVerbOptions.Verbs.Keys);

    [Theory]
    [MemberData(nameof(Verbs))]
    public void EveryVerbRejectsAnOptionItDoesNotRead(string verb)
    {
        var error = new StringWriter();

        // After a value, because the shared validator treats the token straight after a known option
        // as that option's value, even when it begins with dashes (see OptionVALUESAreNeverMistakenForOptions).
        var exit = LongMemEvalVerbOptions.Reject([verb, "value", "--not-an-option", "x"], verb, error);

        exit.Should().Be(1);
        error.ToString().Should().Contain("'--not-an-option'");
    }

    [Theory]
    [MemberData(nameof(Verbs))]
    public void EveryVerbAcceptsEveryOptionItReads(string verb)
    {
        var (_, options) = LongMemEvalVerbOptions.Verbs[verb];
        string[] args = [verb, .. options.SelectMany(option => new[] { option, "value" })];

        LongMemEvalVerbOptions.Reject(args, verb, TextWriter.Null).Should().BeNull();
    }

    [Fact]
    public void TheCensusStillTakesPositionalReportPaths() =>
        LongMemEvalVerbOptions.Reject(["--census", "a.json", "b.json", "dir"], "--census", TextWriter.Null)
            .Should().BeNull();

    [Theory]
    [MemberData(nameof(Verbs))]
    public void TheTableKnowsEveryOptionTheVerbsSourceReads(string verb)
    {
        var (sourceFile, options) = LongMemEvalVerbOptions.Verbs[verb];
        if (sourceFile is null) return; // handled inline in Program with no options of its own

        var literals = Regex.Matches(ToolSource(sourceFile), "\"(--[a-z][a-z0-9-]*)\"")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal);

        literals.Should().BeSubsetOf(
            [verb, .. options],
            $"{sourceFile} reads every one of these, so rejecting any of them would break a valid command");
    }

    /// <summary>A table entry with no guard at its dispatch site protects nothing.</summary>
    [Theory]
    [MemberData(nameof(Verbs))]
    public void EveryDispatchSiteConsultsTheTable(string verb) =>
        ToolSource("Program.cs").Should().Contain($"LongMemEvalVerbOptions.Reject(args, \"{verb}\")");

    private static string ToolSource(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentMemory.slnx")))
            directory = directory.Parent;
        directory.Should().NotBeNull("the repository root must be reachable from the test binaries");
        return File.ReadAllText(Path.Combine(
            directory!.FullName, "tools", "AgentMemory.LongMemEval", fileName));
    }
}
