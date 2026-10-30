using System.Diagnostics;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// Exact coverage as the statistics test (review 7, I6 and the root-cause analysis, C2: the statistics stopped producing highs
/// only once a check computed the claim exactly; the remaining guard was Monte Carlo with ceilings too wide to catch a miss of
/// round 6's size). eng/trial-statistics/coverage.py enumerates every outcome over the sufficient statistics for the one-arm
/// precision bound and the paired difference the upgrade chain uses, rare losses included, and fails when a target at the truth
/// passes more often than the nominal rate. The definitions it checks are reference.py's, pinned to the C# by golden.json.
/// Python with numpy is required: a run without it fails, rather than passing without the check.
/// </summary>
[Trait("Category", "Conformance")]
public sealed class StatisticsCoverageTests
{
    [Fact]
    public void Every_bound_passes_a_target_at_the_truth_at_most_its_nominal_rate_exactly_over_every_outcome() =>
        Run("coverage.py", @"(\d+)/\1 bounds pass a target at the truth at most their nominal rate");

    // Review 8, G1: the clustered bounds at the burst regime's worst case, one layout at a time (bursts of exactly 8 at every
    // offset, intraclass correlation 0.6 and 1, spacings 1 to 7); the averaged sweep it replaces passed a bound at 9.4%.
    [Fact]
    public void Every_clustered_bound_holds_its_nominal_rate_at_the_burst_regime_s_worst_case() =>
        Run("bursts.py", @"(\d+)/\1 clustered bounds hold their nominal rate at the burst regime's worst case", "--quick");

    // Review 9, D9: the C# is pinned to golden.json and coverage is computed on reference.py; this holds the link between them.
    [Fact]
    public void Golden_json_is_the_reference_s_own_output() =>
        Run("golden.py", @"golden\.json is reference\.py's output", "--check");

    private static void Run(string script, string expected, params string[] extra)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentMemory.slnx"))) directory = directory.Parent;
        var root = directory!.FullName;
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "python" : "python3")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = root,
        };
        foreach (var arg in new[] { "-I", Path.Combine(root, "eng", "trial-statistics", script), root }.Concat(extra)) start.ArgumentList.Add(arg);
        Process process;
        try { process = Process.Start(start)!; }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new InvalidOperationException("Exact coverage needs Python 3.12 with numpy on the PATH; without it this guard cannot run.", e);
        }
        using (process)
        {
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            process.ExitCode.Should().Be(0, output + error.Result);
            output.Should().MatchRegex(expected);
        }
    }
}
