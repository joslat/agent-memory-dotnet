using AgentEval.Memory.External.Models;
using AgentEval.Memory.External.TypedMemEval;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// The double-count guard: the twelve time-grounded probe questions were carried into
/// TypedMemEval-Prospective, so a result set spanning both corpora double-counts them in any
/// total. AgentEval ships the detector (<see cref="TypedMemEvalRunSet.DetectSeedOverlap"/>);
/// these tests hold the promise that OUR report assembly actually consults it — the
/// ship-but-unreachable shape is the one this repo keeps finding, so the wire itself is guarded.
/// </summary>
public sealed class TypedMemEvalSeedOverlapGuardTests
{
    private static ExternalBenchmarkResult ResultFor(string datasetIdentifier) => new()
    {
        BenchmarkId = "seed-overlap-guard-fixture",
        BenchmarkName = "seed overlap guard fixture",
        OverallAccuracy = null,
        TaskAveragedAccuracy = null,
        PerTypeResults = [],
        QuestionResults = [],
        Duration = TimeSpan.Zero,
        Options = new ExternalBenchmarkOptions(),
        Provenance = new BenchmarkRunProvenance
        {
            Mode = RunProvenanceMode.Full,
            DatasetIdentifier = datasetIdentifier
        }
    };

    [Fact]
    public void WarnsWithAgentEvalsOwnText_WhenBothOverlappingCorporaAppear()
    {
        var results = new[]
        {
            ResultFor(TypedMemEvalRunSet.TimeGroundedCorpusId),
            ResultFor(TypedMemEvalVerticals.For(TypedMemEvalVertical.Prospective).CorpusId),
        };
        using var output = new StringWriter();

        TypedMemEvalProgram.WarnOnSeedOverlap(results, output).Should().BeTrue();

        // The message is upstream's, not ours: assert the load-bearing phrase, not the wording.
        output.ToString().Should().Contain("typedmemeval: WARNING")
            .And.Contain("double-count");
    }

    [Fact]
    public void StaysSilent_ForTypedMemEvalOnlyResultSets()
    {
        var results = TypedMemEvalVerticals.All
            .Select(descriptor => ResultFor(descriptor.CorpusId))
            .ToArray();
        using var output = new StringWriter();

        TypedMemEvalProgram.WarnOnSeedOverlap(results, output).Should().BeFalse();

        output.ToString().Should().BeEmpty(
            "a guard that warns on every run trains its reader to ignore it");
    }

    [Fact]
    public void StaysSilent_ForTheTimeGroundedCorpusAlone()
    {
        using var output = new StringWriter();

        TypedMemEvalProgram
            .WarnOnSeedOverlap([ResultFor(TypedMemEvalRunSet.TimeGroundedCorpusId)], output)
            .Should().BeFalse();

        output.ToString().Should().BeEmpty();
    }

    /// <summary>
    /// The wire, not the method: <c>RunAsync</c> must call the guard where results are assembled.
    /// Raw-source assertion with comment lines stripped — a substring guard that a comment can
    /// satisfy is the likeliest way this wire actually gets cut (the Wave C–E review found exactly
    /// that failure on the voting guard).
    /// </summary>
    [Fact]
    public void TheVerbsResultAssemblyConsultsTheGuard()
    {
        var source = File.ReadAllLines(TypedMemEvalProgramSourcePath())
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .ToArray();

        source.Count(line => line.Contains("WarnOnSeedOverlap(", StringComparison.Ordinal))
            .Should().BeGreaterThan(
                1,
                "the definition alone is ship-but-unreachable; RunAsync must call it where " +
                "results are assembled");
    }

    private static string TypedMemEvalProgramSourcePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "AgentMemory.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the repo root (AgentMemory.slnx) must be findable from the test bin");
        return Path.Combine(
            directory!.FullName, "tools", "AgentMemory.LongMemEval", "TypedMemEvalProgram.cs");
    }
}
