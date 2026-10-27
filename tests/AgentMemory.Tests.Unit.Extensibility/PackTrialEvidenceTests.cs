using System.Text.Json;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Modules;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// PACK-evidence-aef: a trial decision as an AEF 1.0 run. Result ids and sums follow the AEF specification exactly (its own
/// vectors), the run is written closed and unsealed for an assurer to seal, and it carries the decision whole. (AEF's
/// reference verifier accepted runs written this way, sealed and signed by AgentEval: strategy evidence aef-evidence-2026-10-27.)
/// </summary>
public sealed class PackTrialEvidenceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pack-aef-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    // AEF 1.0 conformance/result-ids.json.
    [InlineData("01928f3e-7c1a-7b2e-9a51-3f2c0d4e8a10", "case-17", "triage", null, "r_479d157f3423e95d566bcbfc0c6d2461")]
    [InlineData("01928f3e-7c1a-7b2e-9a51-3f2c0d4e8a10", "case-17", "triage/helpfulness", null, "r_1264eeb36620c9cbe97b71ffdbcfd331")]
    [InlineData("r-1", "case-3", "booking", 0, "r_26ddaab93e84b418bd4ce6031ad25658")]
    public void Result_ids_are_the_AEF_hash(string runId, string caseId, string path, int? trial, string expected) =>
        PackTrialEvidence.ResultId(runId, caseId, path, trial).Should().Be(expected);

    [Fact]
    public void Sums_are_exact_and_rounded_once()
    {
        PackTrialEvidence.ExactSum([1e20, 1, -1e20]).Should().Be(1);
        PackTrialEvidence.ExactSum(Enumerable.Repeat(0.1, 10)).Should().Be(1.0);
        PackTrialEvidence.ExactSum([]).Should().Be(0);
        (PackTrialEvidence.ExactSum([0.5, 1.0, 0.6]) / 5).Should().Be(2.1 / 5, "[SUM-5]: one binary64 division");
    }

    private static PackActivation Decision(bool active = true)
    {
        var tasks = Enumerable.Range(0, 40).Select(i => new PackTrialTask(i % 4 == 0 ? 0 : 2, i % 3 == 0 ? 0 : Math.Min(1, i % 4 == 0 ? 0 : 2))).ToList();
        var with = new PackTrialMeasurement
        {
            Tasks = 40, Hits = tasks.Count(t => t.Correct > 0), Offered = tasks.Sum(t => t.Offered), Correct = tasks.Sum(t => t.Correct), P95Ms = 24.5, PerTask = tasks,
        };
        var without = new PackTrialMeasurement { Tasks = 40, Hits = 20, Offered = 80, Correct = 20, P95Ms = 1 };
        return new PackActivation
        {
            ModuleId = "io.agentmemory.packs.codemap", Version = "0.1.2", ManifestDigest = "jcs-sha256:" + new string('a', 64), PackDigest = "sha256:" + new string('b', 64),
            Active = active, Reason = active ? "every target met" : "precision 0.2 below 0.25", DecidedAt = new DateTimeOffset(2026, 10, 27, 2, 57, 8, TimeSpan.Zero),
            Checks = [new PackTrialCheck("hitRate", 0.35, 0.5, true, 0.6), new PackTrialCheck("addedP95Ms", 250, 23.5, true)], With = with, Without = without,
        };
    }

    [Fact]
    public void A_decision_becomes_a_closed_unsealed_run_with_its_tasks_its_gate_and_its_record()
    {
        var folder = Path.Combine(_root, "run");
        var decision = Decision();

        var runId = PackTrialEvidence.WriteRun(decision, folder, signedRecord: "{\"payloadType\":\"x\"}"u8.ToArray());

        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/'))
            .Should().BeEquivalentTo(["run.json", "results.ndjson", "metrics.json", "summary.json", "gates.ndjson", "ext/activation.dsse.json"]);
        using var run = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "run.json")));
        run.RootElement.GetProperty("runId").GetString().Should().Be(runId);
        run.RootElement.GetProperty("status").GetString().Should().Be("completed");
        run.RootElement.GetProperty("subject").GetProperty("ref").GetString().Should().Be("pack:io.agentmemory.packs.codemap@sha256:" + new string('b', 64));
        run.RootElement.GetProperty("contentCapture").GetString().Should().Be("off");

        var lines = File.ReadAllText(Path.Combine(folder, "results.ndjson")).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        lines.Should().HaveCount(41, "40 tasks with the pack, and the baseline kept in total");
        foreach (var line in lines)
            line.GetProperty("resultId").GetString().Should().Be(PackTrialEvidence.ResultId(runId, line.GetProperty("caseId").GetString()!, line.GetProperty("path").GetString()!));
        lines.Count(l => l.GetProperty("state").GetString() == "passed").Should().Be(decision.With!.Hits);

        using var summary = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "summary.json")));
        var hit = summary.RootElement.GetProperty("lanes")[0].GetProperty("metrics")[0];
        hit.GetProperty("value").GetDouble().Should().Be((double)decision.With.Hits / 40);
        using var gate = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "gates.ndjson")));
        gate.RootElement.GetProperty("outcome").GetString().Should().Be("ship");
        gate.RootElement.GetProperty("ext").GetProperty("io.agentmemory.trial").GetProperty("checks").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public void A_pack_kept_off_is_no_ship_and_a_folder_in_use_is_refused()
    {
        var folder = Path.Combine(_root, "off");
        PackTrialEvidence.WriteRun(Decision(active: false), folder);
        using var gate = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "gates.ndjson")));
        gate.RootElement.GetProperty("outcome").GetString().Should().Be("no_ship");
        gate.RootElement.GetProperty("exitCode").GetInt32().Should().Be(1);

        FluentActions.Invoking(() => PackTrialEvidence.WriteRun(Decision(), folder)).Should().Throw<ArgumentException>().WithMessage("*not empty*");
        FluentActions.Invoking(() => PackTrialEvidence.WriteRun(Decision() with { With = null }, Path.Combine(_root, "none"))).Should().Throw<ArgumentException>();
    }
}
