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
    [Fact]
    public async Task PreflightOnlyExecutionRejectsAnOutputPathBeforeProviderWork()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"agentmemory-lme-preflight-{Guid.NewGuid():N}");
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
                "--preflight-only",
                "--output", output
            ]);

            exitCode.Should().Be(1);
            File.Exists(output).Should().BeFalse(
                "preflight-only execution can never create or accept a report");
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

    [Fact]
    public async Task CheckpointExecutionRejectsAnOutputPathBeforeProviderWork()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"agentmemory-lme-checkpoint-{Guid.NewGuid():N}");
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
                "--checkpoint-questions", "3",
                "--output", output
            ]);

            exitCode.Should().Be(1);
            File.Exists(output).Should().BeFalse(
                "checkpoint execution can never create or accept a report");
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

    [Fact]
    public async Task CheckpointExecutionRejectsPreflightModeBeforeProviderWork()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"agentmemory-lme-checkpoint-mode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var dataset = Path.Combine(directory, "dataset.json");
        await File.WriteAllTextAsync(dataset, "[]");

        try
        {
            var exitCode = await LongMemEvalPreparedPairProgram.RunAsync(
            [
                "--dataset", dataset,
                "--questions", "10",
                "--checkpoint-questions", "3",
                "--preflight-only"
            ]);

            exitCode.Should().Be(1);
        }
        finally
        {
            if (File.Exists(dataset))
                File.Delete(dataset);
            if (Directory.Exists(directory))
                Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task CellProbeDryRunReportsBrokenPairingWhenWindowLengthsDiffer()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"agentmemory-lme-cell-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var cell = Path.Combine(directory, "cell.json");
        var pair = Path.Combine(directory, "pair.json");
        await File.WriteAllTextAsync(
            cell,
            """
            [
              { "question_id": "q1", "haystack_sessions": [[{ "content": "Payment logged against job: $10 for cable." }]] },
              { "question_id": "q2", "haystack_sessions": [[{ "content": "Payment logged against job: $11 for tape." }]] }
            ]
            """);
        await File.WriteAllTextAsync(
            pair,
            """
            [
              { "question_id": "q1", "haystack_sessions": [[{ "content": "Payment logged against job: $10 for cable." }]] }
            ]
            """);

        var originalOut = Console.Out;
        using var output = new StringWriter();
        Console.SetOut(output);
        try
        {
            await CellProbeProgram.RunAsync(
            [
                "--cell-probe", cell,
                "--dry-run",
                "--pair-with", pair
            ]);

            output.ToString().Should().Contain("PAIRING BROKEN")
                .And.NotContain("PAIRING OK");
        }
        finally
        {
            Console.SetOut(originalOut);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
