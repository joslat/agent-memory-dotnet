using AgentMemory.Abstractions.Services;

namespace AgentMemory.Cli.Commands;

/// <summary>
/// <c>integrity [--owner &lt;id&gt;]</c> (root PLAN 40.50, G6): checks the store's integrity rules and prints each with what it
/// found. Read-only. Exit code 1 when an error rule is broken; warnings are printed and do not fail.
/// </summary>
public sealed class IntegrityCommand(IMemoryIntegrityService integrity, TextWriter output)
{
    public async Task<int> ExecuteAsync(string? owner)
    {
        var report = await integrity.CheckAsync(owner).ConfigureAwait(false);
        output.WriteLine($"integrity: {(report.OwnerId is null ? "whole store" : $"owner {report.OwnerId}")}");
        foreach (var rule in report.Rules)
        {
            var mark = rule.Violations == 0 ? "ok  " : rule.Severity == "error" ? "FAIL" : "warn";
            output.WriteLine($"  {mark} {rule.Id}: {rule.Description} ({rule.Violations})");
            foreach (var example in rule.Examples) output.WriteLine($"         {example}");
        }
        output.WriteLine(report.Passed ? "integrity: passed" : "integrity: FAILED");
        return report.Passed ? 0 : 1;
    }
}
