using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

public sealed class LongMemEvalDiagnosticCliTests
{
    [Fact]
    public async Task DiagnosticOnlyExecutionRejectsAnOutputPathBeforeProviderWork()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"agentmemory-lme-diagnostic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var dataset = Path.Combine(directory, "dataset.json");
        var output = Path.Combine(directory, "forbidden-report.json");
        await File.WriteAllTextAsync(dataset, "[]");

        try
        {
            var exitCode = await LongMemEvalPreparedPairProgram.RunAsync(
            [
                "--dataset", dataset,
                "--questions", "10",
                "--diagnostic-question", "3",
                "--diagnostic-source-session", "14",
                "--output", output
            ]);

            exitCode.Should().Be(1);
            File.Exists(output).Should().BeFalse(
                "diagnostic-only extraction can never create or accept a report");
        }
        finally
        {
            if (File.Exists(output))
                File.Delete(output);
            if (File.Exists(dataset))
                File.Delete(dataset);
            if (Directory.Exists(directory))
                Directory.Delete(directory);
        }
    }
}
