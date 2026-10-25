using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using AgentMemory.Extensibility.Bundles;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Packs;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>Opening knowledge packs (Extensibility 0.13 §K1-K3): verified first, then every record checked.</summary>
public sealed class KnowledgePackTests
{
    private static readonly BundleKey Key = BundleSigning.CreateKey();

    internal static readonly string[] Seed =
    [
        """{"type":"DojoKata","key":"shotokan/heian-shodan","properties":{"name":"Heian Shodan","summary":"First Heian kata: down block, lunge punch","moves":21},"links":[{"relation":"DOJO_KATA_FOR_RANK","target":"shotokan/8-kyu"}]}""",
        """{"type":"DojoRank","key":"shotokan/8-kyu","properties":{"name":"8th kyu"}}""",
        """{"type":"DojoKata","key":"shotokan/heian-nidan","properties":{"name":"Heian Nidan","summary":"Second Heian kata: side kick, back stance"},"links":[{"relation":"DOJO_KATA_FOR_RANK","target":"shotokan/7-kyu"}]}""",
        """{"type":"DojoRank","key":"shotokan/7-kyu","properties":{"name":"7th kyu"}}""",
    ];

    internal static Dictionary<string, byte[]> Content(string manifest, IEnumerable<string> seed) => new(StringComparer.Ordinal)
    {
        [KnowledgePack.ManifestFile] = Encoding.UTF8.GetBytes(manifest),
        ["seed/records.jsonl"] = Encoding.UTF8.GetBytes(string.Join("\n", seed) + "\n"),
    };

    internal static IReadOnlyDictionary<string, byte[]> Signed(string? manifest = null, IEnumerable<string>? seed = null, string version = "1.0.0",
        Action<Dictionary<string, byte[]>>? change = null)
    {
        var content = Content(manifest ?? KnowledgePackManifestTests.Pack, seed ?? Seed);
        change?.Invoke(content);
        return BundleSigning.Sign(content, "io.example.dojo", version, Key.PrivateKeyPem);
    }

    internal static BundleTrust Trust()
    {
        var trust = new BundleTrust();
        trust.DeveloperKeys.Add(Key.PublicKeyPem);
        return trust;
    }

    private static IReadOnlyList<string> Problems(Func<KnowledgePack> open) =>
        FluentActions.Invoking(open).Should().Throw<KnowledgePackException>().Which.Problems;

    [Fact]
    public void APackOpensWithPackProvenanceAndItsTargetsFirst()
    {
        var pack = KnowledgePack.Open(Signed(), Trust());

        pack.Id.Should().Be("io.example.dojo");
        pack.Version.Should().Be("1.0.0");
        pack.Bundle.Signer.Should().Be(BundleSigner.Developer);
        pack.Proposals.Select(p => p.Key).Should().Equal("shotokan/8-kyu", "shotokan/7-kyu", "shotokan/heian-shodan", "shotokan/heian-nidan");
        var kata = pack.Proposals.Single(p => p.Key == "shotokan/heian-shodan");
        kata.Source.MessageIds.Should().BeEmpty();
        kata.Source.Excerpt.Should().Be("seed/records.jsonl:1");
        kata.Source.Pack.Should().Be(new PackSource { Id = "io.example.dojo", Version = "1.0.0", Digest = pack.Digest, File = "seed/records.jsonl", Line = 1 });
        kata.Links.Single().Target.Should().Be("shotokan/8-kyu");
    }

    [Fact]
    public void AnUnsignedOrChangedPackIsRefusedBeforeAnyRecordIsRead()
    {
        var changed = new Dictionary<string, byte[]>(Signed(), StringComparer.Ordinal);
        changed["seed/records.jsonl"] = Encoding.UTF8.GetBytes(Seed[1] + "\n");
        Problems(() => KnowledgePack.Open(changed, Trust())).Should().Contain(p => p.Contains("does not match", StringComparison.Ordinal));
        Problems(() => KnowledgePack.Open(Content(KnowledgePackManifestTests.Pack, Seed), Trust())).Should().NotBeEmpty();
        Problems(() => KnowledgePack.Open(Signed(), new BundleTrust())).Should().Contain(p => p.Contains("not trusted", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"type":"DojoRank","key":"r","properties":{"name":"x"},"extra":1}""", "unknown field")]
    [InlineData("""{"type":"DojoRank","properties":{"name":"x"}}""", "string type and key")]
    [InlineData("""{"type":"DojoRank","key":"r","properties":{"title":"x"}}""", "undeclared property")]
    [InlineData("""{"type":"DojoRank","key":"r","properties":{}}""", "required value missing")]
    [InlineData("""{"type":"DojoKata","key":"k","properties":{"name":"x"},"links":[{"relation":"DOJO_KATA_FOR_RANK","target":"nowhere"}]}""", "does not hold")]
    [InlineData("""{"type":"DojoRank","key":"shotokan/8-kyu","properties":{"name":"again"}}""", "repeats DojoRank")]
    [InlineData("""{"type":"DojoBelt","key":"b","properties":{}}""", "type the pack declares")]
    [InlineData("""not json""", "not valid JSON")]
    public void EveryBadRecordIsNamedWithItsLine(string line, string problem)
    {
        var problems = Problems(() => KnowledgePack.Open(Signed(seed: [.. Seed, line]), Trust()));
        problems.Should().Contain(p => p.StartsWith("seed/records.jsonl:5", StringComparison.Ordinal) && p.Contains(problem, StringComparison.Ordinal));
    }

    [Fact]
    public void ABlankLineInsideTheSeedIsRefused()
    {
        Problems(() => KnowledgePack.Open(Signed(seed: [Seed[1], "", Seed[3]]), Trust()))
            .Should().Contain(p => p.Contains("seed/records.jsonl:2: blank line", StringComparison.Ordinal));
    }

    [Fact]
    public void ABundleHoldsOnlyTheManifestAndItsSeedFiles()
    {
        Problems(() => KnowledgePack.Open(Signed(change: c => c["notes.txt"] = [1]), Trust()))
            .Should().Contain(p => p.Contains("'notes.txt' is neither", StringComparison.Ordinal));
    }

    [Fact]
    public void TheManifestMustBeTheSignedPackAndAPack()
    {
        Problems(() => KnowledgePack.Open(Signed(version: "1.0.1"), Trust())).Should().Contain(p => p.Contains("signed files list names", StringComparison.Ordinal));
        var module = JsonNode.Parse(KnowledgePackManifestTests.Pack)!.AsObject();
        module.Remove("seed");
        Problems(() => KnowledgePack.Open(Signed(manifest: module.ToJsonString()), Trust())).Should().Contain(p => p.Contains("not a knowledge pack", StringComparison.Ordinal));
    }

    [Fact]
    public void APackOpensFromTheBundleFolderOfANuGetPackage()
    {
        using var package = new MemoryStream();
        using (var zip = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, bytes) in Signed())
                using (var entry = zip.CreateEntry(KnowledgePack.PackageFolder + path).Open()) entry.Write(bytes);
            using (var nuspec = zip.CreateEntry("AgentMemory.Packs.Dojo.nuspec").Open()) nuspec.Write("<package/>"u8);
        }
        package.Position = 0;
        KnowledgePack.OpenPackage(package, Trust()).Proposals.Should().HaveCount(4);
    }

    [Fact]
    public void ATurnCannotWriteAPackSource()
    {
        var pack = KnowledgePack.Open(Signed(), Trust());
        var provider = new IngestionProviderDescriptor { Id = "io.example.dojo:writer", Trigger = IngestionTrigger.OnRequest };
        var request = new IngestionRequest { SessionId = "s", Trigger = IngestionTrigger.OnRequest, ProviderIds = ["io.example.dojo:writer"],
            Messages = [new IngestedMessage { Id = "m1", Role = "user", Text = "hi" }] };
        var forged = pack.Proposals[0] with { Source = pack.Proposals[0].Source with { MessageIds = ["m1"] } };
        ModuleProposalAdmission.Admit(pack.Resolution, provider, request, forged).Errors
            .Should().Contain(e => e.Contains("only the knowledge pack importer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheImporterRefusesADowngradeUnlessAskedAndOtherwiseImports()
    {
        var pack = KnowledgePack.Open(Signed(), Trust());
        var store = new Store { State = new() { PackId = pack.Id, Version = "2.0.0", LiveRecords = 4 } };
        var importer = new KnowledgePackImporter(store);
        (await FluentActions.Awaiting(() => importer.ImportAsync(pack)).Should().ThrowAsync<KnowledgePackException>())
            .Which.Problems.Single().Should().Contain("newer than 1.0.0");
        store.Imports.Should().Be(0);
        (await importer.ImportAsync(pack, allowDowngrade: true)).Version.Should().Be("1.0.0");
        store.Imports.Should().Be(1);
    }

    private sealed class Store : IKnowledgePackStore
    {
        public required KnowledgePackState State { get; init; }
        public int Imports { get; private set; }
        public Task<KnowledgePackState> GetStateAsync(ModuleManifest pack, CancellationToken cancellationToken = default) => Task.FromResult(State);
        public Task<KnowledgePackImportResult> ImportAsync(KnowledgePack pack, DateTimeOffset atUtc, CancellationToken cancellationToken = default)
        { Imports++; return Task.FromResult(new KnowledgePackImportResult { PackId = pack.Id, Version = pack.Version, Digest = pack.Digest }); }
        public Task<KnowledgePackImportResult> RemoveAsync(ModuleManifest pack, DateTimeOffset atUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(new KnowledgePackImportResult { PackId = pack.Id });
    }
}
