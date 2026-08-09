using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// Phase 1.4. The checkpoint fingerprint identifies the configuration whose cold-build wall time the
/// checkpoint projects. It carried PreparationWorkers but neither provider-concurrency knob, so two
/// runs with materially different concurrency shared a fingerprint and their projections could be
/// compared as though equivalent.
/// </summary>
public sealed class LongMemEvalCheckpointFingerprintTests
{
    [Theory]
    [InlineData("MaxConcurrentBatchesPerExtraction")]
    [InlineData("MaxConcurrentExtractionBatches")]
    [InlineData("PreparationWorkers")]
    public void EveryConcurrencyKnobIsPartOfTheCheckpointIdentity(string knob)
    {
        // Asserted against the source because the fingerprint is computed inline from an anonymous
        // object; the property must appear inside the hashed payload, not merely exist on options.
        var source = File.ReadAllText(SourcePath());
        var start = source.IndexOf("var checkpointFingerprint", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "the checkpoint fingerprint must exist");
        var end = source.IndexOf("Console.WriteLine", start, StringComparison.Ordinal);
        var payload = source[start..end];

        payload.Should().Contain(knob,
            $"{knob} changes the wall time the checkpoint projects, so it must change its identity");
    }

    [Fact]
    public void TheProjectionInputsAreAlsoPartOfTheIdentity()
    {
        // A projection compared against one computed under a different batch budget would be
        // meaningless, so these must be pinned too.
        var source = File.ReadAllText(SourcePath());
        var start = source.IndexOf("var checkpointFingerprint", StringComparison.Ordinal);
        var end = source.IndexOf("Console.WriteLine", start, StringComparison.Ordinal);
        var payload = source[start..end];

        payload.Should().Contain("MaxSessionsPerBatch");
        payload.Should().Contain("MaxInputTokens");
    }

    private static string SourcePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !Directory.Exists(Path.Combine(directory.FullName, "tools")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the repository root must be locatable from the test binary");
        return Path.Combine(
            directory!.FullName, "tools", "AgentMemory.LongMemEval",
            "LongMemEvalPreparedPairProgram.cs");
    }
}
