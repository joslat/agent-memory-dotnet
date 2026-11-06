using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Packs.Work;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// CMP-8: the work pack reads waves (with how much of each is done), what blocks what, and which release first shipped each
/// item, from the ledger and the repository's tags; and the same from GitHub issues and milestones exported by <c>gh</c>.
/// </summary>
public sealed class WorkWavesAndReleasesTests
{
    private const string Ledger = """
        {"waves":[{"order":1,"phase":"Planning"},{"order":2,"phase":"Packs"}],
         "dependencies":[{"before":"W-1","after":"W-2"},{"before":"M-10–M-12","after":"W-2"}],
         "tasks":[
          {"id":"W-1","task":"Plan the packs","status":"Done","wave":1,"step":1,"estimate":"2 h","actual":"3 h"},
          {"id":"W-2","task":"Build the packs","status":"Doing","wave":2,"step":1},
          {"id":"W-3","task":"Release the packs","status":"Pending","wave":2,"step":2}]}
        """;

    private static readonly WorkCommit[] Commits =
    [
        new("c1", "W-1: plan", ["docs/plan.md"]), new("c2", "W-2: first pack", ["src/A.cs"]), new("c3", "W-2: second pack", ["src/B.cs"]),
    ];

    private static readonly WorkRelease[] Releases = [new("v1.0.0", "2026-10-01", "c1", ["c1"]), new("v1.1.0", "2026-10-20", "c3", ["c2", "c3"])];

    private static string Text(ModuleProposal record, string property) => record.Properties[property].ToString();

    [Fact]
    public void A_ledger_reads_into_waves_blocked_links_and_first_releases()
    {
        var snapshot = WorkLedgerReader.Read(Ledger, "tasks.json", Commits, "git:abc", null, Releases);

        var waves = snapshot.Records.Where(r => r.Type == WorkPack.WaveType).ToDictionary(r => r.Key);
        Text(waves["wave:2"], "name").Should().Be("Packs");
        Text(waves["wave:2"], "items").Should().Be("2");
        Text(waves["wave:1"], "done").Should().Be("1");

        var w2 = snapshot.Records.Single(r => r.Key == "W-2");
        w2.Links.Select(l => (l.Relation, l.Target)).Should().Contain([(WorkPack.InWave, "wave:2"), (WorkPack.BlockedBy, "W-1"), (WorkPack.ReleasedIn, "release:v1.1.0")]);
        w2.Links.Should().NotContain(l => l.Target == "M-10–M-12", "a dependency on something the ledger does not name as an item is left out");
        snapshot.Records.Single(r => r.Key == "W-1").Links.Should().Contain(l => l.Relation == WorkPack.ReleasedIn && l.Target == "release:v1.0.0");
        snapshot.Records.Single(r => r.Key == "W-3").Links.Should().NotContain(l => l.Relation == WorkPack.ReleasedIn);
        Text(snapshot.Records.Single(r => r.Key == "W-1"), "estimate").Should().Be("2 h");

        var release = snapshot.Records.Single(r => r.Key == "release:v1.1.0");
        Text(release, "items").Should().Be("1");
        Text(release, "commit").Should().Be("c3");
    }

    [Fact]
    public void GitHub_issues_and_milestones_read_into_items_in_waves_with_their_commits_and_releases()
    {
        const string issues = """
            [{"number":1,"title":"Map the code","state":"CLOSED","milestone":{"number":7,"title":"Preview","dueOn":"2026-10-15T00:00:00Z"},"labels":[{"name":"packs"}],"body":"Why.","url":"https://example.com/1"},
             {"number":2,"title":"Walk the map","state":"OPEN","milestone":{"number":8,"title":"Release","dueOn":"2026-11-01T00:00:00Z"},"labels":[],"body":"","url":"https://example.com/2"},
             {"number":3,"title":"No milestone","state":"OPEN","milestone":null,"labels":[],"body":"","url":"https://example.com/3"}]
            """;
        const string milestones = """[{"number":8,"title":"Release","state":"open","due_on":"2026-11-01T00:00:00Z"},{"number":7,"title":"Preview","state":"closed","due_on":"2026-10-15T00:00:00Z"}]""";
        WorkCommit[] commits = [new("g1", "Map the code (#1)", ["src/Map.cs"]), new("g2", "Start the walk, refs #2 and #99", ["src/Walk.cs"])];
        WorkRelease[] releases = [new("v0.1.0", null, "g1", ["g1"])];

        var snapshot = GitHubWorkReader.Read(issues, milestones, commits, "github", null, releases);

        var first = snapshot.Records.Single(r => r.Key == "#1");
        Text(first, "status").Should().Be("Done");
        Text(first, "labels").Should().Be("packs");
        first.Links.Select(l => (l.Relation, l.Target)).Should().BeEquivalentTo([
            (WorkPack.Changed, "src/Map.cs"), (WorkPack.InWave, "milestone:7"), (WorkPack.ReleasedIn, "release:v0.1.0")]);
        snapshot.Records.Single(r => r.Key == "#2").Links.Should().Contain(l => l.Relation == WorkPack.Changed && l.Target == "src/Walk.cs");
        Text(snapshot.Records.Single(r => r.Key == "milestone:7"), "order").Should().Be("1", "the milestone due first comes first");
        Text(snapshot.Records.Single(r => r.Key == "milestone:7"), "done").Should().Be("1");
        Text(snapshot.Records.Single(r => r.Key == "milestone:8"), "state").Should().Be("open");
        snapshot.Records.Single(r => r.Key == "#3").Links.Should().BeEmpty();
    }
}
