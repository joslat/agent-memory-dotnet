using System.Text;
using System.Text.Json.Nodes;
using System.IO.Compression;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>PACK-composer: advice on a loaded pack, about what a host will do with it later; never a refusal.</summary>
public sealed class PackAdviceTests
{
    private static readonly string MartialArts = Path.Combine(AppContext.BaseDirectory, "Packs", "MartialArts");
    private static readonly PackSigningKey Key = PackAuthoring.CreateKey();

    // DKP-4: a card that says who owns the pack and what it is for; the advice tests below are about everything else.
    internal const string Card = """
        { "format": "agentmemory.pack.card/1", "owner": { "name": "Example Dojo", "contact": "https://example.com/dojo" },
          "intendedUse": "Techniques and lessons for one dojo's study coach.", "limitations": "Not medical or safety advice.",
          "boundaries": ["Do not use it to grade students."], "risks": ["Lessons may lag the syllabus."],
          "whenToUse": "Ask it for a technique's steps or a lesson's notes when a question names a technique, a kata or a lesson." }
        """;

    // What the sample pack was made from (feature 09), so each test reads only its own advice.
    private const string Derivation = """{"actions":["extract"],"format":"agentmemory.pack.derivation/1","inputs":[{"digest":"sha256:abababababababababababababababababababababababababababababababab","name":"syllabus"}],"tool":{"name":"dojo-notes","version":"1.0.0"}}""";

    private static LoadedPack Load(Action<JsonObject>? change = null, string? trial = null, string? card = Card, bool derived = true, string? derivationJson = null)
    {
        var source = new Dictionary<string, byte[]>(PackAuthoring.FromModuleBundle(Directory.EnumerateFiles(MartialArts, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(MartialArts, f).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes, StringComparer.Ordinal)), StringComparer.Ordinal);
        if (card is not null) source["card.json"] = Encoding.UTF8.GetBytes(card.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
        if (change is not null)
        {
            var manifest = JsonNode.Parse(source["pack.json"])!.AsObject();
            change(manifest);
            source["pack.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        }
        if (trial is not null) source["evals/trial.json"] = Encoding.UTF8.GetBytes(trial);
        if (derived) source["META/derivation.json"] = Encoding.UTF8.GetBytes(derivationJson ?? Derivation);
        var trust = PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(Key.PublicKeyPem, "io.agentmemory")));
        return PackLoader.Load(PackAuthoring.Build(source, Key.PrivateKeyPem), trust);
    }

    private static IEnumerable<string> Rules(LoadedPack pack, DateTimeOffset? now = null) => PackAdvice.Of(pack, now ?? DateTimeOffset.UtcNow).Select(a => a.Rule);

    [Fact]
    public void Verified_derivation_is_readable_and_absence_is_distinct()
    {
        var property = typeof(LoadedPack).GetProperty("Derivation");
        property.Should().NotBeNull("hosts need verified provenance, not only a presence flag");
        var derived = property!.GetValue(Load());
        derived.Should().NotBeNull();
        var json = System.Text.Json.JsonSerializer.SerializeToElement(derived);
        json.GetProperty("ToolName").GetString().Should().Be("dojo-notes");
        json.GetProperty("Inputs")[0].GetProperty("Name").GetString().Should().Be("syllabus");
        property.GetValue(Load(derived: false)).Should().BeNull();
    }

    [Fact]
    public void Verified_derivation_collections_are_immutable_and_tampered_metadata_is_never_exposed()
    {
        var loaded = Load(); var derivation = loaded.Derivation!;
        FluentActions.Invoking(() => ((IList<PackDerivationSource>)derivation.Inputs).Clear()).Should().Throw<NotSupportedException>();
        FluentActions.Invoking(() => ((IList<string>)derivation.Actions).Clear()).Should().Throw<NotSupportedException>();
        FluentActions.Invoking(() => ((IDictionary<string, string>)derivation.Parameters).Add("forged", "value")).Should().Throw<NotSupportedException>();
        var source = new Dictionary<string, byte[]>(PackAuthoring.FromModuleBundle(Directory.EnumerateFiles(MartialArts, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(MartialArts, f).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes, StringComparer.Ordinal)), StringComparer.Ordinal);
        source["META/derivation.json"] = Encoding.UTF8.GetBytes(Derivation);
        var archive = PackAuthoring.Build(source, Key.PrivateKeyPem);
        using var changed = new MemoryStream();
        using (var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read))
        using (var output = new ZipArchive(changed, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var entry in zip.Entries)
            {
                using var target = output.CreateEntry(entry.FullName).Open();
                if (entry.FullName == "META/derivation.json") target.Write(Encoding.UTF8.GetBytes(Derivation.Replace("dojo-notes", "forged-tool", StringComparison.Ordinal)));
                else { using var original = entry.Open(); original.CopyTo(target); }
            }
        var trust = PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(Key.PublicKeyPem, "io.agentmemory")));
        var refused = PackInspection.Inspect(changed.ToArray(), trust);
        refused.Loaded.Should().BeFalse(); refused.Pack.Should().BeNull(); refused.Problems.Should().NotBeEmpty();
        loaded.Derivation!.ToolName.Should().Be("dojo-notes");
    }

    [Fact]
    public void Verified_derivation_preserves_all_optional_fields_without_inheriting_parent_authority()
    {
        var value = JsonNode.Parse(Derivation)!.AsObject();
        value["inputs"]![0]!["licence"] = "CC-BY-4.0";
        value["inputs"]![0]!["confidenceMicro"] = 800000;
        value["model"] = new JsonObject { ["id"] = "local-extractor", ["version"] = "2026.11" };
        value["parameters"] = new JsonObject { ["language"] = "en", ["mode"] = "careful" };
        value["parent"] = new JsonObject { ["id"] = "io.agentmemory.unavailable-parent", ["digest"] = "sha256:" + new string('c', 64) };
        value["promptDigest"] = "sha256:" + new string('d', 64);
        var canonical = Encoding.UTF8.GetString(Jcs.Canonical(Encoding.UTF8.GetBytes(value.ToJsonString())));
        var loaded = Load(derivationJson: canonical);
        var info = loaded.Derivation!;
        info.Inputs.Single().Licence.Should().Be("CC-BY-4.0");
        info.Inputs.Single().ConfidenceMicro.Should().Be(800000);
        info.ToolName.Should().Be("dojo-notes"); info.ToolVersion.Should().Be("1.0.0");
        info.ModelId.Should().Be("local-extractor"); info.ModelVersion.Should().Be("2026.11");
        info.Parameters.Should().BeEquivalentTo(new Dictionary<string, string> { ["language"] = "en", ["mode"] = "careful" });
        info.ParentId.Should().Be("io.agentmemory.unavailable-parent"); info.ParentDigest.Should().Be("sha256:" + new string('c', 64));
        info.PromptDigest.Should().Be("sha256:" + new string('d', 64)); info.Actions.Should().Equal("extract");
        loaded.Signers.Should().ContainSingle("the absent parent is lineage information, not an inherited signer or required dependency");
    }

    [Fact]
    public void A_knowledge_pack_that_says_nothing_of_what_it_was_made_from_is_advised()
    {
        // Feature 09: a host caps a record's confidence by its source only when the pack names its sources.
        Rules(Load(derived: false)).Should().Contain("derivation.missing");
        Rules(Load()).Should().NotContain("derivation.missing");
    }

    [Fact]
    public void A_countersignature_keeps_the_digest_and_counts_where_the_policy_trusts_the_role()
    {
        // PACK-composer phase 3: a curator signs a built pack again; a host that requires a curator then loads it.
        var source = new Dictionary<string, byte[]>(PackAuthoring.FromModuleBundle(Directory.EnumerateFiles(MartialArts, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(MartialArts, f).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes, StringComparer.Ordinal)), StringComparer.Ordinal);
        var built = PackAuthoring.Build(source, Key.PrivateKeyPem);
        var curator = PackKey.Create();
        var policy = new JsonObject
        {
            ["format"] = "agentmemory.pack.trust/1.1", ["requireRoles"] = new JsonArray("publisher", "curator"),
            ["keys"] = new JsonArray(
                new JsonObject { ["keyId"] = PackKey.KeyIdOf(Key.PublicKeyPem), ["publicKey"] = Key.PublicKeyPem, ["idPrefixes"] = new JsonArray("io.agentmemory"), ["roles"] = new JsonArray("publisher") },
                new JsonObject { ["keyId"] = curator.KeyId, ["publicKey"] = curator.PublicKeyPem, ["idPrefixes"] = new JsonArray("io.agentmemory"), ["roles"] = new JsonArray("curator") }),
        };
        var trust = PackTrust.Parse(Encoding.UTF8.GetBytes(policy.ToJsonString()));

        FluentActions.Invoking(() => PackLoader.Load(built, trust)).Should().Throw<PackLoadException>("a curator is required and none signed yet");
        var countersigned = PackAuthoring.Countersign(built, curator.PrivateKeyPem, "curator", trust);
        var loaded = PackLoader.Load(countersigned, trust);

        loaded.Digest.Should().Be(PackLoader.Load(built, PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(Key.PublicKeyPem, "io.agentmemory")))).Digest);
        loaded.Signers.Should().HaveCount(2);
        var tampered = (byte[])built.Clone();
        tampered[tampered.Length / 2] ^= 0x01;
        FluentActions.Invoking(() => PackAuthoring.Countersign(tampered, curator.PrivateKeyPem, "curator", trust)).Should().Throw<PackLoadException>();
    }

    [Fact]
    public void A_knowledge_pack_without_a_trial_is_told_a_host_requiring_trials_refuses_it()
    {
        var pack = Load();

        Rules(pack).Should().Equal("trial.missing");
        PackAdvice.Of(pack, DateTimeOffset.UtcNow).Single().Pack.Should().Be("io.agentmemory.packs.martialarts");
    }

    [Fact]
    public void A_trial_without_an_upgrade_rule_or_with_few_tasks_is_advised()
    {
        var pack = Load(trial: """{ "measure": "test-impact", "tasks": 20, "targets": { "hitRateMicro": 300000 } }""");

        Rules(pack).Should().Equal("trial.upgrade", "trial.small");
    }

    [Fact]
    public void A_full_trial_and_a_distant_validity_give_no_advice()
    {
        var pack = Load(
            m => m["validUntil"] = DateTimeOffset.UtcNow.AddDays(400).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            trial: """{ "measure": "test-impact", "tasks": 40, "targets": { "hitRateMicro": 300000 }, "upgrade": { "noRegression": true, "toleranceMicro": 20000 } }""");

        Rules(pack).Should().BeEmpty();
        Rules(pack, DateTimeOffset.UtcNow.AddDays(380)).Should().Equal("validity.soon");
    }

    [Fact]
    public void A_pack_without_a_card_or_with_the_template_one_is_advised_and_a_real_card_is_read_back()
    {
        const string trial = """{ "measure": "test-impact", "tasks": 40, "targets": { "hitRateMicro": 300000 }, "upgrade": { "noRegression": true } }""";

        Rules(Load(trial: trial, card: null)).Should().Equal("card.missing");
        Rules(Load(trial: trial, card: Card.Replace("Example Dojo", "Who owns this pack", StringComparison.Ordinal))).Should().Equal("card.template");
        var pack = Load(trial: trial);
        Rules(pack).Should().BeEmpty();
        pack.Card.Should().NotBeNull();
        pack.Card!.Owner.Should().Be("Example Dojo");
        pack.Card.OwnerContact.Should().Be("https://example.com/dojo");
        pack.Card.Boundaries.Should().Equal("Do not use it to grade students.");
        pack.Card.IsTemplate.Should().BeFalse();
    }

    [Fact]
    public void A_card_that_breaks_the_format_refuses_the_pack_it_is_signed_into()
    {
        // The card is listed and signed like any other file; a pack whose card is malformed does not build.
        FluentActions.Invoking(() => Load(card: Card.Replace("\"Example Dojo\"", "\"Example\\u001b[2J Dojo\"", StringComparison.Ordinal)))
            .Should().Throw<PackLoadException>().Which.Problems.Should().Contain(p => p.Contains("owner.name"));
        FluentActions.Invoking(() => Load(card: Card.Replace("\"risks\"", "\"rating\": \"five\", \"risks\"", StringComparison.Ordinal)))
            .Should().Throw<PackLoadException>().Which.Problems.Should().Contain(p => p.Contains("unknown field 'rating'"));
    }

    [Fact]
    public void A_federated_pack_carries_who_published_it_and_for_whom_through_loading_inspection_and_advice()
    {
        const string publisher = "https://pod.example/supplier/profile/card#me";
        const string buyer = "https://pod.example/buyer/profile/card#me";
        var source = new Dictionary<string, byte[]>(PackAuthoring.FromModuleBundle(Directory.EnumerateFiles(MartialArts, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(MartialArts, f).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes, StringComparer.Ordinal)), StringComparer.Ordinal);
        var manifest = JsonNode.Parse(source["pack.json"])!.AsObject();
        manifest["extensions"] = new JsonObject
        {
            ["critical"] = new JsonArray("federation"),
            ["federation"] = new JsonObject { ["format"] = "agentmemory.pack.federation/1", ["publisher"] = publisher, ["audience"] = new JsonArray(buyer), ["purpose"] = "Study notes" },
        };
        source["pack.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        var archive = PackAuthoring.Build(source, Key.PrivateKeyPem);
        var policy = JsonNode.Parse(PackAuthoring.TrustPolicy(Key.PublicKeyPem, "io.agentmemory"))!.AsObject();
        policy["keys"]![0]!["webId"] = publisher;
        policy["host"] = new JsonObject { ["webIds"] = new JsonArray(buyer) };
        var trust = PackTrust.Parse(Encoding.UTF8.GetBytes(policy.ToJsonString()));

        var pack = PackLoader.Load(archive, trust);
        var inspection = PackInspection.Inspect(archive, trust);

        pack.Publication.Should().Be(new PackPublication(publisher, pack.Publication!.Audience, "Study notes"));
        pack.Publication.Audience.Should().Equal(buyer);
        inspection.Loaded.Should().BeTrue();
        inspection.Publication!.Publisher.Should().Be(publisher);
        inspection.Checks.Should().Contain(c => c.Name == "federation" && c.Passed);
        PackAdvice.Of(pack, DateTimeOffset.UtcNow).Should().Contain(a => a.Rule == "federation.placement" && a.Text.Contains("for 1 WebID(s)"));

        // A host outside the audience still sees the claim, unverified, and why it does not load.
        policy["host"] = new JsonObject { ["webIds"] = new JsonArray("https://pod.example/stranger/profile/card#me") };
        var stranger = PackInspection.Inspect(archive, PackTrust.Parse(Encoding.UTF8.GetBytes(policy.ToJsonString())));
        stranger.Loaded.Should().BeFalse();
        stranger.Publication!.Publisher.Should().Be(publisher);
        stranger.Problems.Should().Contain(p => p.Contains("this host is https://pod.example/stranger/profile/card#me"));
    }
}
