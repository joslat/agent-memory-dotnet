#pragma warning disable AMEXT001
using System.Text;
using AgentMemory.Extensibility.Modules;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>Feature 12: retrieval trials on a host's recorded questions (R-TRIAL-054, R-TRIAL-055).</summary>
public sealed class RetrievalTrialTests
{
    private static readonly ModuleManifest Retrieval = ModuleManifest.Parse(
        """{"format":"agentmemory.module/v1alpha1","id":"com.example.notes","version":"1.0.0","sdk":"[0.1,0.2)","trial":{"measure":"retrieval","tasks":10,"targets":{"hitRateMicro":500000,"noHarmMicro":800000}}}""");

    [Fact]
    public void A_task_file_is_read_strictly_and_named_by_its_digest()
    {
        var bytes = Encoding.UTF8.GetBytes("""
            {"format":"agentmemory.trial.tasks/1","tasks":[
              {"question":"When is the Lyon review?","at":"2026-10-01T09:00:00Z","expected":["review/lyon"],"ability":"fact"},
              {"question":"What is the capital of Mars?","at":"2026-10-02T09:00:00Z","expected":[],"ability":"abstain"},
              {"question":"Which file holds the map?","at":"2026-10-03T09:00:00Z","anchors":[{"kind":"path","value":"src/Map.cs"}],"expected":["map"]}]}
            """);
        var file = RetrievalTaskFile.Read(bytes);
        file.Tasks.Should().HaveCount(3);
        file.Tasks[1].Abstains.Should().BeTrue();
        file.Tasks[2].Anchors.Should().ContainSingle().Which.Value.Should().Be("src/Map.cs");
        file.Digest.Should().StartWith("sha256:").And.HaveLength(71);
        foreach (var bad in new[]
        {
            """{"format":"agentmemory.trial.tasks/2","tasks":[]}""",
            """{"format":"agentmemory.trial.tasks/1","tasks":[{"question":"q","at":"2026-10-01T09:00:00+02:00","expected":["k"]}]}""",
            """{"format":"agentmemory.trial.tasks/1","tasks":[{"question":"q","at":"2026-10-01T09:00:00Z","expected":[]}]}""",
            """{"format":"agentmemory.trial.tasks/1","tasks":[{"question":"q","at":"2026-10-01T09:00:00Z","expected":["k","k"]}]}""",
            """{"format":"agentmemory.trial.tasks/1","tasks":[{"question":"q","at":"2026-10-01T09:00:00Z","expected":["k"],"answer":"x"}]}""",
        })
            FluentActions.Invoking(() => RetrievalTaskFile.Read(Encoding.UTF8.GetBytes(bad))).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void No_harm_is_the_share_of_abstention_tasks_the_pack_stays_quiet_on()
    {
        Retrieval.Validate().Should().BeEmpty("retrieval is a measure, and noHarm its target");
        ModuleManifest.Parse("""{"format":"agentmemory.module/v1alpha1","id":"com.example.notes","version":"1.0.0","sdk":"[0.1,0.2)","trial":{"measure":"test-impact","targets":{"noHarmMicro":800000}}}""")
            .Validate().Should().Contain(e => e.Contains("noHarm is a retrieval trial's target"));
        PackTrialMeasurement Arm(int abstentions, int quiet) => new()
        {
            Tasks = 10, Hits = 8, Offered = 10, Correct = 8, P95Ms = 1, PerTask = [.. Enumerable.Range(0, 10).Select(i => new PackTrialTask(1, i < 8 ? 1 : 0))],
            Abstentions = abstentions, Quiet = quiet,
        };
        var none = new PackTrialMeasurement { Tasks = 10, Hits = 0, Offered = 0, Correct = 0, P95Ms = 1, PerTask = [.. Enumerable.Range(0, 10).Select(_ => new PackTrialTask(0, 0))], Abstentions = 10, Quiet = 10 };
        Check(PackTrialGate.Decide(Retrieval, Arm(10, 9), none, DateTimeOffset.UnixEpoch)).Met.Should().BeTrue("quiet on 9 of 10 meets 0.8");
        Check(PackTrialGate.Decide(Retrieval, Arm(10, 7), none, DateTimeOffset.UnixEpoch)).Met.Should().BeFalse("quiet on 7 of 10 is below 0.8");
        Check(PackTrialGate.Decide(Retrieval, Arm(0, 0), none, DateTimeOffset.UnixEpoch)).Met.Should().BeFalse("no abstention task, no evidence");

        static PackTrialCheck Check(PackActivation decision) => decision.Checks.Single(c => c.Target == "noHarm");
    }
}
