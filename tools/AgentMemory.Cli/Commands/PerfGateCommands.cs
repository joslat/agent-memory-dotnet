using System.Text.Json;
using AgentMemory.Cli.Perf;

namespace AgentMemory.Cli.Commands;

public sealed class PerfBaselineCommand(TextWriter output)
{
    public async Task<int> ExecuteAsync(
        string? updateValue,
        string? reportPath,
        string? outputPath,
        CancellationToken cancellationToken = default)
    {
        if (!bool.TryParse(updateValue, out var update) || !update)
        {
            output.WriteLine("error: perf baseline requires --update.");
            return 1;
        }
        if (string.IsNullOrWhiteSpace(reportPath) || !File.Exists(reportPath))
        {
            output.WriteLine($"error: performance summary not found: {reportPath ?? "(missing --report)"}");
            return 1;
        }

        await using var stream = File.OpenRead(reportPath);
        using var report = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var baseline = PerfBaselineDocument.FromSummary(report.RootElement);
        var path = outputPath ?? PerfBaselineDocument.DefaultPath;
        baseline.Save(path);

        output.WriteLine($"perf baseline: wrote {path}");
        return 0;
    }
}

public sealed class PerfGateCommand(TextWriter output)
{
    public async Task<int> ExecuteAsync(
        string? baselinePath,
        string? reportPath,
        string? allowCounterChangeValue,
        string? pullRequestBody,
        CancellationToken cancellationToken = default)
    {
        var path = baselinePath ?? PerfBaselineDocument.DefaultPath;
        if (string.IsNullOrWhiteSpace(reportPath) || !File.Exists(reportPath))
        {
            output.WriteLine($"error: performance summary not found: {reportPath ?? "(missing --report)"}");
            return 1;
        }

        var allowCounterChange =
            bool.TryParse(allowCounterChangeValue, out var allowed) && allowed;
        var baseline = PerfBaselineDocument.Load(path);
        await using var stream = File.OpenRead(reportPath);
        using var report = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var result = PerfRegressionGate.Evaluate(
            baseline,
            report.RootElement,
            new PerfCounterChangeOverride(allowCounterChange, pullRequestBody));

        output.WriteLine(result.Passed
            ? "perf gate: PASS"
            : "perf gate: FAIL");
        foreach (var change in result.AcknowledgedCounterChanges)
            output.WriteLine($"acknowledged: {change}");
        foreach (var violation in result.Violations)
            output.WriteLine($"error: {violation}");
        return result.Passed ? 0 : 1;
    }
}
