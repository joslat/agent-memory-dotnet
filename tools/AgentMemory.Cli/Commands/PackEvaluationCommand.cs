using System.Text.Json;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Validation;

namespace AgentMemory.Cli.Commands;

/// <summary>
/// <c>evaluate --pack core|&lt;file&gt;|&lt;directory&gt;</c> (root PLAN 40.57): runs validation packs against the configured
/// store and writes a JSON report. Deterministic: no model, no judge; each pack carries what extraction yields. Every
/// run writes under a fresh prefix, so it never meets data already in the store, and deletes nothing.
/// </summary>
public sealed class PackEvaluationCommand(Action<Neo4jOptions> configureNeo4j, TextWriter output)
{
    public async Task<int> ExecuteAsync(string? packArg, string? outputPath)
    {
        IReadOnlyList<ValidationPack> packs;
        try
        {
            packs = Load(packArg);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            output.WriteLine($"error: evaluate --pack: {ex.Message}");
            return 1;
        }
        if (packs.Count == 0)
        {
            output.WriteLine($"error: evaluate --pack '{packArg}' found no packs.");
            return 1;
        }

        var generatedAt = DateTimeOffset.UtcNow;
        var runner = new ValidationPackRunner(configureNeo4j);
        var results = new List<PackRunResult>();
        foreach (var pack in packs)
        {
            var result = await runner.RunAsync(pack).ConfigureAwait(false);
            results.Add(result);
            var failed = result.Failures.ToList();
            output.WriteLine($"pack {pack.Id}: {(result.Passed ? "pass" : "FAIL")} ({result.Checks.Count - failed.Count}/{result.Checks.Count} checks)");
            foreach (var failure in failed) output.WriteLine($"  FAIL {failure.Id}: {failure.Detail}");
        }

        var destination = outputPath ?? Path.Combine("artifacts", "evaluation", $"packs-{generatedAt:yyyyMMdd-HHmmss}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var report = new { GeneratedAtUtc = generatedAt, Format = ValidationPackReader.Format, Packs = results.Select(r => new
        {
            r.PackId, r.Title, r.RunPrefix, r.Passed, r.Checks, r.Routes,
        }) };
        await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }))
            .ConfigureAwait(false);
        var passed = results.Count(r => r.Passed);
        output.WriteLine($"evaluation: {passed}/{results.Count} packs passed.");
        output.WriteLine($"evaluation report: {destination}");
        return passed == results.Count ? 0 : 1;
    }

    /// <summary><c>core</c> (the packs shipped with the library), a pack file, or every <c>*.json</c> in a directory.</summary>
    internal static IReadOnlyList<ValidationPack> Load(string? packArg) =>
        packArg is null or "core" ? ValidationPackReader.Core()
        : Directory.Exists(packArg) ? [.. Directory.GetFiles(packArg, "*.json").Order(StringComparer.Ordinal).Select(ValidationPackReader.ReadFile)]
        : [ValidationPackReader.ReadFile(packArg)];
}
