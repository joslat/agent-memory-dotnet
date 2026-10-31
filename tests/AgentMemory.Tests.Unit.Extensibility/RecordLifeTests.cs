#pragma warning disable AMEXT001
using System.Text;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Extensibility.Reading;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// Feature 07: a record says when it is true in the world and when a host stops using it, and a type how its records age. The
/// pack reader resolves each record's effective life within the pack, so a store keeps instants and no rule.
/// </summary>
public sealed class RecordLifeTests
{
    private static readonly DateTimeOffset June = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly ModuleOntology Ontology = ModuleManifest.Parse("""
        {"format":"agentmemory.module/v1alpha1","id":"com.example.prices","version":"1.0.0","sdk":"[0.1,0.2)",
         "ontology":{"labelPrefix":"ComExamplePrices","types":[
           {"name":"ComExamplePricesPrice","scope":"shared","properties":{"amount":{"type":"string"}},"lifecycle":"refreshes","ttlDays":30},
           {"name":"ComExamplePricesRule","scope":"shared","properties":{"rule":{"type":"string"}},"lifecycle":"pinned"},
           {"name":"ComExamplePricesNote","scope":"shared","properties":{"text":{"type":"string"}}}]}}
        """).Ontology!;

    private static (List<ModuleProposal> Records, List<string> Problems) Read(params string[] lines)
    {
        var problems = new List<string>();
        var records = KnowledgePack.Lines("records/r.jsonl", Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n"),
            line => new AgentMemory.Extensibility.Ingestion.PackSource { Id = "com.example.prices", Version = "1.0.0", Digest = "sha256:" + new string('a', 64), File = "records/r.jsonl", Line = line }, problems);
        return (KnowledgePack.Lives(Ontology, records, problems), problems);
    }

    [Fact]
    public void A_record_s_instants_are_read_and_its_liveness_follows_them()
    {
        var (records, problems) = Read("""{"type":"ComExamplePricesNote","key":"n","validFrom":"2026-06-01T00:00:00Z","validTo":"2026-07-01T00:00:00Z","expiresAt":"2026-06-20T00:00:00Z","reviewBy":"2026-06-10T00:00:00Z"}""");

        problems.Should().BeEmpty();
        var life = records.Single().Life!;
        life.ValidFromUtc.Should().Be(June);
        life.ReviewByUtc.Should().Be(June.AddDays(9));
        life.IsLiveAt(June.AddSeconds(-1)).Should().BeFalse("not yet true");
        life.IsLiveAt(June).Should().BeTrue("valid from the instant itself");
        life.IsLiveAt(June.AddDays(19)).Should().BeFalse("expired, whatever its validity");
        ModuleRecordLife.IsLive(null, June).Should().BeTrue("a record without a life is always live");
    }

    [Theory]
    [InlineData("""{"type":"ComExamplePricesNote","key":"n","validFrom":"2026-06-01"}""", "an instant in UTC")]
    [InlineData("""{"type":"ComExamplePricesNote","key":"n","validFrom":"2026-06-01T00:00:00+02:00"}""", "an instant in UTC")]
    [InlineData("""{"type":"ComExamplePricesNote","key":"n","validFrom":"2026-06-01T00:00:00.5Z"}""", "an instant in UTC")]
    [InlineData("""{"type":"ComExamplePricesNote","key":"n","validFrom":"2026-06-02T00:00:00Z","validTo":"2026-06-01T00:00:00Z"}""", "after validFrom")]
    [InlineData("""{"type":"ComExamplePricesNote","key":"n","supersedes":["n"]}""", "keys of other records")]
    [InlineData("""{"type":"ComExamplePricesNote","key":"n","supersedes":["x"]}""", "does not hold")]
    [InlineData("""{"type":"ComExamplePricesPrice","key":"p"}""", "states its validFrom")]
    [InlineData("""{"type":"ComExamplePricesRule","key":"r","expiresAt":"2026-06-01T00:00:00Z"}""", "never expires")]
    public void A_malformed_life_is_refused_with_its_line(string line, string says)
    {
        Read(line).Problems.Should().ContainSingle().Which.Should().Contain("records/r.jsonl:1").And.Contain(says);
    }

    [Fact]
    public void A_refreshing_record_ends_its_type_s_days_after_it_begins()
    {
        var (records, problems) = Read("""{"type":"ComExamplePricesPrice","key":"p","validFrom":"2026-06-01T00:00:00Z"}""",
            """{"type":"ComExamplePricesPrice","key":"q","validFrom":"2026-06-01T00:00:00Z","validTo":"2026-06-05T00:00:00Z"}""");

        problems.Should().BeEmpty();
        records[0].Life!.ValidToUtc.Should().Be(June.AddDays(30));
        records[1].Life!.ValidToUtc.Should().Be(June.AddDays(4), "a stated validTo stands");
    }

    [Fact]
    public void A_superseded_record_ends_where_its_successor_begins()
    {
        var (records, problems) = Read("""{"type":"ComExamplePricesNote","key":"old"}""",
            """{"type":"ComExamplePricesNote","key":"new","validFrom":"2026-06-01T00:00:00Z","supersedes":["old"]}""",
            """{"type":"ComExamplePricesNote","key":"gone","validFrom":"2026-05-01T00:00:00Z"}""",
            """{"type":"ComExamplePricesNote","key":"always","supersedes":["gone"]}""");

        problems.Should().BeEmpty();
        records[0].Life!.ValidToUtc.Should().Be(June, "the old record is true until the new one begins");
        records[0].Life!.IsLiveAt(June).Should().BeFalse();
        records[2].Life!.IsLiveAt(June).Should().BeFalse("superseded by a record that always was, it is never live");
    }

    [Fact]
    public void A_cycle_of_supersession_and_a_record_superseded_twice_are_refused()
    {
        Read("""{"type":"ComExamplePricesNote","key":"a","supersedes":["b"]}""", """{"type":"ComExamplePricesNote","key":"b","supersedes":["a"]}""")
            .Problems.Should().Contain(p => p.Contains("cycle"));
        Read("""{"type":"ComExamplePricesNote","key":"a"}""", """{"type":"ComExamplePricesNote","key":"b","supersedes":["a"]}""",
            """{"type":"ComExamplePricesNote","key":"c","supersedes":["a"]}""").Problems.Should().Contain(p => p.Contains("superseded twice"));
    }

    [Fact]
    public void The_host_validates_a_type_s_lifecycle()
    {
        static IReadOnlyList<string> Problems(string type) => ModuleManifest.Parse(
            """{"format":"agentmemory.module/v1alpha1","id":"com.example.prices","version":"1.0.0","sdk":"[0.1,0.2)","ontology":{"labelPrefix":"ComExamplePrices","types":[{"name":"ComExamplePricesPrice","scope":"shared","properties":{"amount":{"type":"string"}}""" + type + "}]}}").Validate();

        Problems(""","lifecycle":"decays" """).Should().Contain(p => p.Contains(".lifecycle"));
        Problems(""","lifecycle":"refreshes" """).Should().Contain(p => p.Contains(".ttlDays"));
        Problems(""","ttlDays":5 """).Should().Contain(p => p.Contains("only a refreshes type"));
        Problems(""","lifecycle":"refreshes","ttlDays":30 """).Should().BeEmpty();
    }
}
