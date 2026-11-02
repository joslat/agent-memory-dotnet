using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentMemory.Extensibility;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Packs.CodeMap;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// PACK-bind (from the 26 Oct review, 1.1, 2.9, 2.18, 2.19, 4.17): a trial decision binds to the pack's content as well as its
/// declarations; the declarations' digest is RFC 8785 canonical and named apart from a pack's digest; a definition pack is
/// named by its digest in the fingerprint; and a snapshot's records keep the pack that carried them, with its signer.
/// </summary>
public sealed class PackBindTests : IDisposable
{
    private const string Base = "com.example.base";
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
    private static readonly string PackA = "sha256:" + new string('a', 64);
    private static readonly string PackB = "sha256:" + new string('b', 64);
    private readonly string _folder = Directory.CreateTempSubdirectory("pack-bind-").FullName;
    private readonly ECDsa _host = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static readonly ModuleManifest Trialled = ModuleManifest.Parse(
        """{"format":"agentmemory.module/v1alpha1","id":"com.example.base","version":"1.0.0","sdk":"[0.1,0.2)","trial":{"measure":"test-impact","tasks":40,"targets":{"hitRateMicro":500000}}}""");

    private static PackTrialMeasurement Arm(int hits) => new() { Tasks = 40, Hits = hits, Correct = hits, Offered = 2 * hits, P95Ms = 10 };

    public void Dispose()
    {
        _host.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void A_decision_for_one_pack_does_not_count_for_other_content_under_the_same_declarations()
    {
        Write(PackTrialGate.Decide(Trialled, Arm(30), Arm(0), Now, PackA));

        Gated(m => m.AddDefinitionPack(Trialled, PackA, "developer 1a2b")).Modules.Select(m => m.Manifest.Id).Should().Equal(Base);
        Gated(m => m.AddDefinitionPack(Trialled, PackB, "developer 1a2b")).SwitchedOff.Should().ContainSingle()
            .Which.Reason.Should().Contain("other content", "the same declarations with other records are another pack");
        Gated(m => m.Add(Trialled, _ => { })).SwitchedOff.Should().ContainSingle()
            .Which.Reason.Should().Contain("other content", "a pack's decision does not switch on compiled code that declares the same");
    }

    [Fact]
    public void A_decision_for_compiled_code_does_not_count_for_a_pack()
    {
        Write(PackTrialGate.Decide(Trialled, Arm(30), Arm(0), Now));

        Gated(m => m.Add(Trialled, _ => { })).Modules.Should().ContainSingle();
        Gated(m => m.AddDefinitionPack(Trialled, PackA, "developer 1a2b")).SwitchedOff.Should().ContainSingle();
    }

    [Fact]
    public void The_record_signs_the_pack_digest_as_its_subject_and_a_changed_one_is_ignored()
    {
        var decision = PackTrialGate.Decide(Trialled, Arm(30), Arm(0), Now, PackA);
        var statement = JsonDocument.Parse(PackActivationFolder.Statement(decision)).RootElement;
        statement.GetProperty("subject")[0].GetProperty("digest").GetProperty("sha256").GetString().Should().Be(new string('a', 64));

        // A record whose subject names one pack and whose predicate names another does not verify as a record at all.
        var forged = Encoding.UTF8.GetString(PackActivationFolder.Statement(decision)).Replace(PackA, PackB, StringComparison.Ordinal);
        WriteRaw(Encoding.UTF8.GetBytes(forged), "forged");

        new PackActivationFolder(_folder, _host.ExportSubjectPublicKeyInfoPem()).Ignored.Should().ContainSingle();
    }

    [Fact]
    public void A_record_from_before_PACK_bind_still_verifies_but_counts_for_nothing()
    {
        var old = PackTrialGate.Decide(Trialled, Arm(30), Arm(0), Now) with
        {
            ManifestDigest = "sha256:" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(Trialled))).ToLowerInvariant(),
        };
        Write(old);

        var activations = new PackActivationFolder(_folder, _host.ExportSubjectPublicKeyInfoPem());

        activations.Ignored.Should().BeEmpty();
        Compose(m => m.Add(Trialled, _ => { }), activations).SwitchedOff.Should().ContainSingle().Which.Reason.Should().Contain("trial it again");
    }

    [Fact]
    public void The_manifest_digest_is_canonical_and_named_apart_from_a_pack_digest()
    {
        var digest = PackTrialGate.DigestOf(Trialled);
        digest.Should().StartWith("jcs-sha256:").And.HaveLength("jcs-sha256:".Length + 64);
        var canonical = CanonicalJson.Canonical(JsonSerializer.SerializeToUtf8Bytes(Trialled));
        digest.Should().Be("jcs-sha256:" + Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant());
        Encoding.UTF8.GetString(canonical).Should().NotContain(" ").And.StartWith("{\"");

        FluentActions.Invoking(() => PackTrialGate.Decide(Trialled, Arm(30), Arm(0), Now, "sha256:ABC")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => PackTrialGate.Decide(Trialled, Arm(30), Arm(0), Now, digest)).Should().Throw<ArgumentException>(
            "a manifest digest is not a pack digest");
    }

    [Theory]
    [InlineData(0.0, "0")]
#pragma warning disable xUnit1025 // -0.0 equals 0.0 to the analyzer, but it is the case under test: negative zero prints "0"
    [InlineData(-0.0, "0")]
#pragma warning restore xUnit1025
    [InlineData(1.0, "1")]
    [InlineData(100.0, "100")]
    [InlineData(0.5, "0.5")]
    [InlineData(0.05, "0.05")]
    [InlineData(123.456, "123.456")]
    [InlineData(-4.5, "-4.5")]
    [InlineData(0.000001, "0.000001")]
    [InlineData(1e-7, "1e-7")]
    [InlineData(1e20, "100000000000000000000")]
    [InlineData(1e21, "1e+21")]
    [InlineData(333333333.33333329, "333333333.3333333")]
    [InlineData(9007199254740992.0, "9007199254740992")]
    [InlineData(5e-324, "5e-324")]
    [InlineData(1.7976931348623157e308, "1.7976931348623157e+308")]
    public void Numbers_print_as_ECMAScript_prints_them(double value, string expected) =>
        CanonicalJson.Number(value).Should().Be(expected);

    [Fact]
    public void Canonical_JSON_sorts_members_and_escapes_like_RFC_8785()
    {
        var text = Encoding.UTF8.GetString(CanonicalJson.Canonical(Encoding.UTF8.GetBytes(
            "{ \"b\": [1, 2.50, \"\\u20ac\\n\\u0001\"], \"a\": {\"z\": true, \"\\u00e9\": null, \"A\": false} }")));

        text.Should().Be("{\"a\":{\"A\":false,\"z\":true,\"é\":null},\"b\":[1,2.5,\"€\\n\\u0001\"]}");
    }

    [Fact]
    public void A_definition_pack_is_named_by_its_digest_in_the_fingerprint()
    {
        var set = Compose(m => m.AddDefinitionPack(Trialled, PackA, "developer 1a2b"), null);
        var other = Compose(m => m.AddDefinitionPack(Trialled, PackB, "developer 1a2b"), null);

        set.Modules.Single().Implementation.Should().Be($"definition-pack; digest={PackA}; signer=developer 1a2b");
        set.Fingerprint.Should().NotBe(other.Fingerprint, "another pack is another composition, even with the same declarations");
    }

    [Fact]
    public async Task A_snapshots_records_keep_the_pack_that_carried_them_with_its_signer()
    {
        var key = PackAuthoring.CreateKey();
        var trust = PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(key.PublicKeyPem, "io.example")));
        const string store = "scip-dotnet nuget Acme.Map . Acme/Map/MapStore#";
        var builder = new CodeMapBuilder();
        builder.AddProject("src/Map/Map.csproj", "Acme.Map");
        builder.AddFile("src/Map/MapStore.cs", "src/Map/Map.csproj");
        builder.AddFile("tests/MapStoreTests.cs", null);
        builder.AddSymbol(store, "MapStore", "Acme.Map.MapStore", CodeMapSymbolKind.Class, "Acme.Map", "src/Map/MapStore.cs", 3, ["Remove"]);
        builder.AddTest("scip-dotnet nuget Acme.Map.Tests . Acme/Map/Tests/MapStoreTests#Remove_keeps_history().", "MapStoreTests.Remove_keeps_history",
            "tests/MapStoreTests.cs", 7, "xunit", [store]);
        var source = new Dictionary<string, byte[]>(PackAuthoring.FromSnapshot(builder.Build("git:local"), "io.example.acme-map", "1.0.0", commit: "0a1b2c3d4e5f"),
            StringComparer.Ordinal);
        var loaded = PackLoader.Load(PackAuthoring.Build(source, key.PrivateKeyPem), trust);

        var snapshot = loaded.ToSnapshot(CodeMapPack.Manifest);

        snapshot.Carrier.Should().Be(new PackCarrier { Id = "io.example.acme-map", Version = "1.0.0", Digest = loaded.Digest, Signer = loaded.Signers.Single() });
        snapshot.Stamped().Should().OnlyContain(r => r.Source.Pack!.Id == CodeMapPack.Id && r.Source.Pack.Version == "git:0a1b2c3d4e5f"
            && r.Source.Pack.Carrier == snapshot.Carrier, "the record belongs to the definition, at the commit, carried by the signed pack");
        var memory = PackMemory.From([], [snapshot]);
        var hits = await memory.WalkAsync([AgentMemory.Extensibility.Context.ContextAnchor.Path("src/Map/MapStore.cs")], "remove keeps history", CancellationToken.None);
        hits.Should().Contain(h => h.Type == CodeMapPack.TestType);
        hits.Where(h => h.Type == CodeMapPack.TestType).Should().OnlyContain(h => h.Pack!.Carrier!.Digest == loaded.Digest, "the walk names the signed pack too");
    }

    // Composed with trials required, from the records in the folder.
    private ModuleSet Gated(Action<ModuleSetBuilder> add) => Compose(add, new PackActivationFolder(_folder, _host.ExportSubjectPublicKeyInfoPem()));

    private static ModuleSet Compose(Action<ModuleSetBuilder> add, IPackActivations? activations)
    {
        var services = new ServiceCollection();
        services.AddAgentMemoryModules(m =>
        {
            add(m);
            if (activations is not null) m.RequireTrials(activations);
        });
        return services.BuildServiceProvider().GetRequiredService<ModuleSet>();
    }

    private void Write(PackActivation activation) => WriteRaw(PackActivationFolder.Statement(activation), activation.DecidedAt.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private void WriteRaw(byte[] statement, string name)
    {
        var sig = _host.SignData(PackActivationFolder.Pae(statement), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        File.WriteAllText(Path.Combine(_folder, $"{name}.dsse.json"), JsonSerializer.Serialize(new
        {
            payloadType = "application/vnd.in-toto+json", payload = Convert.ToBase64String(statement),
            signatures = new[] { new { keyid = "host", sig = Convert.ToBase64String(sig) } },
        }));
    }
}
