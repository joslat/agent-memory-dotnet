using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// The guard for the twin verifiers (review 7's root-cause analysis, T1 and C2): the conformance kit and the differential fuzzer
/// are the one class-wide check that made an area go quiet, and they ran only by hand. Here they run in every test run, against
/// the tool this build made, writing into a temporary folder so the tree is never touched. Python 3.12 with <c>cryptography</c>
/// is required: a run without it fails, rather than passing without the check.
/// </summary>
[Trait("Category", "Conformance")]
public sealed class ConformanceKitTests : IDisposable
{
    private const int FuzzerMutants = 200, FuzzerSeed = 20261030;
    private readonly string _out = Directory.CreateTempSubdirectory("ampack-kit-").FullName;

    public void Dispose() => Directory.Delete(_out, recursive: true);

    [Fact]
    public void Both_verifiers_reach_every_vector_s_expected_outcome_and_agree_on_seeded_mutants()
    {
        var root = Root();
        var kit = Path.Combine(root, "conformance", "pack-v1.1");
        var tool = Tool(root);

        var (vectors, vectorsOutput) = Python(Path.Combine(kit, "run_vectors.py"), tool, _out);
        vectors.Should().Be(0, vectorsOutput);
        vectorsOutput.Should().MatchRegex(@"(\d+)/\1 vectors: both verifiers agree with the expected outcome");

        var (fuzzer, fuzzerOutput) = Python(Path.Combine(kit, "differential.py"), tool, _out, "--count", $"{FuzzerMutants}", "--seed", $"{FuzzerSeed}");
        fuzzer.Should().Be(0, fuzzerOutput);
        // Two readers, or three when the Rust reader is built (feature 18): every mutant reaches one verdict either way.
        fuzzerOutput.Should().MatchRegex($@"{FuzzerMutants}/{FuzzerMutants} mutants: (both verifiers( \(Rust reader not built\))?|all three readers) reach the same verdict");
    }

    private static (int Exit, string Output) Python(string script, params string[] args)
    {
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "python" : "python3")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(script)!,
        };
        start.ArgumentList.Add("-I");
        start.ArgumentList.Add(script);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        Process process;
        try { process = Process.Start(start)!; }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new InvalidOperationException("The conformance kit needs Python 3.12 with 'cryptography' on the PATH; without it this guard cannot run.", e);
        }
        using (process)
        {
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            var text = output + error.Result;
            return (process.ExitCode, text.Length > 4000 ? text[^4000..] : text);
        }
    }

    private static string Tool(string root)
    {
        var configuration = typeof(ConformanceKitTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var tool = Path.Combine(root, "tools", "Ampack", "bin", configuration, "net10.0", OperatingSystem.IsWindows() ? "Ampack.exe" : "Ampack");
        File.Exists(tool).Should().BeTrue($"the kit runs the tool this build made ({tool})");
        return tool;
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentMemory.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("The repository root holds AgentMemory.slnx.");
    }
}
