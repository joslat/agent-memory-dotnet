using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace AgentMemory.Tests.Integration.Samples;

/// <summary>
/// HARNESS-sample: the CopilotAgent sample run end to end, against its own Neo4j, the GitHub Copilot CLI and a real model
/// (BYOK). It answers the row's question by counting: memory around each outer turn, and inside Copilot's own loop.
/// </summary>
public sealed class CopilotSampleIntegrationTests(Neo4jTestFixture neo4j, ITestOutputHelper trace) : IClassFixture<Neo4jTestFixture>
{
    [LiveCopilotSampleFact]
    public async Task The_sample_remembers_across_Copilot_sessions_and_counts_where_memory_went_in()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "AgentMemory.slnx"))) root = root.Parent ?? throw new InvalidOperationException("Repository root not found.");
        var configuration = typeof(CopilotSampleIntegrationTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var sample = Path.Combine(root.FullName, "samples", "AgentMemory.Sample.CopilotAgent", "bin", configuration, "net10.0", "AgentMemory.Sample.CopilotAgent.dll");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(sample);
        start.Environment["Neo4j__Uri"] = neo4j.ConnectionUri;
        start.Environment["Neo4j__Username"] = neo4j.Username;
        start.Environment["Neo4j__Password"] = neo4j.Password;

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        var text = await output + await error;
        trace.WriteLine(text);

        process.ExitCode.Should().Be(0);
        text.Should().Contain("Memory around the outer turn: 3 recalls, 3 stores").And.Contain("Memory inside Copilot's loop: 3 prompt hooks.");
        var lastAnswer = text.Split('\n').Last(l => l.StartsWith("ASSISTANT", StringComparison.Ordinal));
        lastAnswer.Should().ContainEquivalentOf("aisle", "a new Copilot session knows the preference only from durable memory");
    }
}

/// <summary>Runs only when asked (COPILOT_SAMPLE_LIVE=1): it needs the Copilot CLI and a model key, and spends a little on the model.</summary>
public sealed class LiveCopilotSampleFactAttribute : FactAttribute
{
    public LiveCopilotSampleFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("COPILOT_SAMPLE_LIVE") != "1")
            Skip = "The Copilot sample test is opt-in: set COPILOT_SAMPLE_LIVE=1. It runs the GitHub Copilot CLI with a real model (BYOK), which costs a little.";
        else if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BITDEER_API_KEY")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AI_INFERENCE_PROVIDER")))
            Skip = "COPILOT_SAMPLE_LIVE is set but no inference provider is configured (BITDEER_API_KEY, or AI_INFERENCE_PROVIDER).";
    }
}
