using System.Text;
using System.Text.Json.Nodes;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// Findings of the independent design-against-code review (PACK-review, 26 Oct) on the host side, each fixed and held here:
/// a bad link is a problem with its line, not a crash; what a pack declares that the host cannot enforce yet (sealed content,
/// code, usage policies) refuses it instead of loading it as open; and a pack set rolled back to an older version is refused
/// by the install ledger like a single pack.
/// </summary>
public sealed class PackReviewFindingsTests : IDisposable
{
    private static readonly string MartialArts = Path.Combine(AppContext.BaseDirectory, "Packs", "MartialArts");
    private readonly PackSigningKey _key = PackAuthoring.CreateKey();
    private readonly string _root = Directory.CreateTempSubdirectory("pack-review-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private PackTrust Trust() => PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(_key.PublicKeyPem, "io.agentmemory", "io.example")));

    private static Dictionary<string, byte[]> Source() => new(PackAuthoring.FromModuleBundle(
        Directory.EnumerateFiles(MartialArts, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(MartialArts, f).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes, StringComparer.Ordinal)), StringComparer.Ordinal);

    private static void Set(Dictionary<string, byte[]> source, string field, JsonNode value)
    {
        var pack = JsonNode.Parse(source["pack.json"])!.AsObject();
        pack[field] = value;
        source["pack.json"] = Encoding.UTF8.GetBytes(pack.ToJsonString());
    }

    [Fact]
    public void A_link_through_an_undeclared_relation_is_a_problem_with_its_line_not_a_crash()
    {
        var source = Source();
        source["records/arts.jsonl"] = [.. source["records/arts.jsonl"],
            .. "{\"type\":\"MartialArtsArt\",\"key\":\"x\",\"properties\":{\"name\":\"X\",\"origin\":\"Y\",\"summary\":\"Z\",\"source\":\"S\"},\"links\":[{\"relation\":\"NOPE\",\"target\":\"shotokan\"}]}\n"u8.ToArray()];

        FluentActions.Invoking(() => PackLoader.Load(PackAuthoring.Build(source, _key.PrivateKeyPem), Trust())).Should().Throw<PackLoadException>()
            .Which.Problems.Should().Contain(p => p.StartsWith("records/arts.jsonl:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_pack_that_declares_protection_the_host_cannot_open_is_refused()
    {
        var source = Source();
        Set(source, "protection", "audience");
        source["META/seal.json"] = "{}"u8.ToArray();

        FluentActions.Invoking(() => PackLoader.Load(PackAuthoring.Build(source, _key.PrivateKeyPem), Trust())).Should().Throw<PackLoadException>()
            .Which.Problems.Should().Contain(p => p.Contains("protected as 'audience'", StringComparison.Ordinal));
    }

    [Fact]
    public void A_pack_that_carries_code_or_a_usage_policy_is_refused_until_the_host_enforces_them()
    {
        var code = Source();
        Set(code, "code", "E2");
        FluentActions.Invoking(() => PackLoader.Load(PackAuthoring.Build(code, _key.PrivateKeyPem), Trust())).Should().Throw<PackLoadException>()
            .Which.Problems.Should().Contain(p => p.Contains("carries code (tier E2)", StringComparison.Ordinal));

        var policy = Source();
        policy["policy.json"] = """{"purpose":["study"],"noExport":true}"""u8.ToArray();
        FluentActions.Invoking(() => PackLoader.Load(PackAuthoring.Build(policy, _key.PrivateKeyPem), Trust())).Should().Throw<PackLoadException>()
            .Which.Problems.Should().Contain(p => p.Contains("usage policy", StringComparison.Ordinal));
    }

    [Fact]
    public void A_pack_set_rolled_back_to_an_older_version_is_refused_by_the_ledger()
    {
        var pack = PackAuthoring.Build(Source(), _key.PrivateKeyPem);
        var ledger = PackInstallLedger.Open(Path.Combine(_root, "ledger.json"));
        var newer = PackFamilies.LoadSet(PackAuthoring.BuildSet("io.example.dojo-set", "1.1.0", [pack], _key.PrivateKeyPem, Trust()), Trust(), ledger);
        ledger.Record(newer);
        newer.Version.Should().Be("1.1.0");

        var older = PackAuthoring.BuildSet("io.example.dojo-set", "1.0.0", [pack], _key.PrivateKeyPem, Trust());
        FluentActions.Invoking(() => PackFamilies.LoadSet(older, Trust(), PackInstallLedger.Open(Path.Combine(_root, "ledger.json"))))
            .Should().Throw<PackLoadException>().Which.Problems.Should().Contain(p => p.Contains("older than 1.1.0", StringComparison.Ordinal));
    }
}
