using System.Text;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.PackFormat;

/// <summary>
/// The federated profile (federated packs §3–§5): the publisher WebID is the signer's by the host's own binding, the host is
/// in the audience, and every placement on a store has its own receipt that can only narrow the audience.
/// </summary>
public sealed class FederationTests
{
    private const string Publisher = "https://supplier-a.example/profile/card#org";
    private const string Buyer = "https://buyer-b.example/profile/card#agent";
    private const string Stranger = "https://stranger.example/profile/card#me";
    private const string Location = "https://supplier-a.example/ampack/pack.ampack";

    private static string Extension(string audience, string publisher = Publisher, string extra = "") =>
        ",\"extensions\":{\"critical\":[\"federation\"],\"federation\":{\"format\":\"agentmemory.pack.federation/1\",\"publisher\":\"" + publisher
        + "\",\"audience\":" + audience + extra + "}}";

    private static Dictionary<string, byte[]> Federated(SamplePack sample, string audience = $"[\"{Buyer}\"]", string publisher = Publisher, PackKey? signer = null)
    {
        var source = sample.Source();
        source["pack.json"] = SamplePack.Utf8($$"""{"format":"agentmemory.pack/1.2","id":"{{sample.Id}}","version":"{{sample.Version}}","kind":"knowledge","sdk":"[0.1,0.2)","tier":"open","licence":"MIT"{{Extension(audience, publisher)}}}""");
        var (files, _) = PackBuilder.Build(source);
        PackBuilder.Sign(files, (signer ?? sample.Key).PrivateKeyPem, "publisher", SamplePack.SignedAt);
        return files;
    }

    private static TrustPolicy Policy(SamplePack sample, string? webId = Publisher, IReadOnlyList<string>? hostWebIds = null, IReadOnlyList<string>? roles = null) => new()
    {
        Keys = [new TrustedKey(sample.Key.KeyId, sample.Key.PublicKeyPem, ["io.example"], roles ?? ["publisher"], null, null, webId)],
        HostWebIds = hostWebIds ?? [Buyer],
    };

    private static (PackManifest? Manifest, string? Problem) Parse(string extensions) => PackManifest.Parse(Encoding.UTF8.GetBytes(
        "{\"format\":\"agentmemory.pack/1.2\",\"id\":\"io.example.notes\",\"version\":\"1.0.0\",\"kind\":\"knowledge\"" + extensions + "}"));

    [Theory]
    [InlineData("https://supplier-a.example/profile/card#org")]
    [InlineData("https://pod.example:8443/alice/profile/card#me")]
    [InlineData("https://a.example/")]
    [InlineData("http://localhost:3000/alice/profile/card#me")]
    [InlineData("http://127.0.0.1/x")]
    [InlineData("http://[::1]:3000/x#me")]
    public void WebIds_in_the_grammar_are_accepted(string webId) => WebId.IsValid(webId).Should().BeTrue();

    [Theory]
    [InlineData("http://supplier-a.example/profile/card#org")]          // plain http off loopback
    [InlineData("https://supplier-a.example")]                          // no path
    [InlineData("https://user@supplier-a.example/card#me")]             // user information
    [InlineData("https://supplier-a.example/card#a#b")]                 // two fragments
    [InlineData("https://supplier-a.example/card me")]                  // whitespace
    [InlineData("https://supplier-a.example/card>#me")]                 // would break Turtle
    [InlineData("https://supplier-a.example/card\"#me")]
    [InlineData("https://supplier-a.example/cärd#me")]             // non-ASCII
    [InlineData("HTTPS://supplier-a.example/card#me")]                  // the scheme is lowercase
    [InlineData("https://-bad.example/card")]
    [InlineData("urn:uuid:4f0c21a0-cf5d-45c6-a195-8101e60a7c73")]
    [InlineData("")]
    public void Anything_else_is_not_a_WebId(string text) => WebId.IsValid(text).Should().BeFalse();

    [Fact]
    public void A_WebId_has_at_most_512_characters() =>
        WebId.IsValid("https://a.example/" + new string('x', 512 - 18)).Should().BeTrue();

    [Fact]
    public void A_WebId_of_513_characters_is_refused() =>
        WebId.IsValid("https://a.example/" + new string('x', 513 - 18)).Should().BeFalse();

    [Fact]
    public void The_federation_extension_is_understood_and_read()
    {
        var (manifest, problem) = Parse(Extension($"[\"{Buyer}\"]", extra: ",\"purpose\":\"Supply-chain handoff\""));

        problem.Should().BeNull();
        manifest!.Federation!.Publisher.Should().Be(Publisher);
        manifest.Federation.Audience.Should().Equal(Buyer);
        manifest.Federation.Purpose.Should().Be("Supply-chain handoff");
        Parse(Extension("\"public\"")).Manifest!.Federation!.IsPublic.Should().BeTrue();
    }

    [Theory]
    [InlineData(""","extensions":{"federation":{"format":"agentmemory.pack.federation/1","publisher":"https://a.example/p#me","audience":"public"}}""", "without marking it critical")]
    [InlineData(""","extensions":{"critical":["federation"],"federation":[]}""", "malformed 'federation'")]
    [InlineData(""","extensions":{"critical":["federation"],"federation":{"format":"agentmemory.pack.federation/2","publisher":"https://a.example/p#me","audience":"public"}}""", "must name format")]
    [InlineData(""","extensions":{"critical":["federation"],"federation":{"format":"agentmemory.pack.federation/1","audience":"public"}}""", "no publisher")]
    [InlineData(""","extensions":{"critical":["federation"],"federation":{"format":"agentmemory.pack.federation/1","publisher":"http://a.example/p#me","audience":"public"}}""", "not a WebID")]
    [InlineData(""","extensions":{"critical":["federation"],"federation":{"format":"agentmemory.pack.federation/1","publisher":"https://a.example/p#me"}}""", "no audience")]
    [InlineData(""","extensions":{"critical":["federation"],"federation":{"format":"agentmemory.pack.federation/1","publisher":"https://a.example/p#me","audience":[]}}""", "1 to 64")]
    [InlineData(""","extensions":{"critical":["federation"],"federation":{"format":"agentmemory.pack.federation/1","publisher":"https://a.example/p#me","audience":"everyone"}}""", "1 to 64")]
    [InlineData(""","extensions":{"critical":["federation"],"federation":{"format":"agentmemory.pack.federation/1","publisher":"https://a.example/p#me","audience":["https://b.example/p#me","https://b.example/p#me"]}}""", "twice")]
    [InlineData(""","extensions":{"critical":["federation"],"federation":{"format":"agentmemory.pack.federation/1","publisher":"https://a.example/p#me","audience":["not a webid"]}}""", "entry 1")]
    [InlineData(""","extensions":{"critical":["federation"],"federation":{"format":"agentmemory.pack.federation/1","publisher":"https://a.example/p#me","audience":"public","purpose":""}}""", "1 to 300")]
    [InlineData(""","extensions":{"critical":["federation"],"federation":{"format":"agentmemory.pack.federation/1","publisher":"https://a.example/p#me","audience":"public","maxScope":"partners"}}""", "unknown field 'maxScope'")]
    public void A_malformed_federation_extension_refuses_the_pack(string extensions, string why) =>
        Parse(extensions).Problem.Should().Contain(why);

    [Fact]
    public void An_audience_of_65_WebIds_is_refused()
    {
        var audience = "[" + string.Join(",", Enumerable.Range(0, 65).Select(i => $"\"https://b{i}.example/p#me\"")) + "]";
        Parse(Extension(audience)).Problem.Should().Contain("1 to 64");
    }

    [Fact]
    public void A_federated_pack_loads_on_a_host_in_its_audience_signed_by_the_publishers_bound_key()
    {
        var sample = new SamplePack();
        var steps = new List<PackStep>();

        var result = PackVerifier.VerifyOne(new PackContent(Federated(sample), "test"), Policy(sample), SamplePack.Now, explain: steps.Add);

        result.Problems.Should().BeEmpty();
        result.Pack!.Manifest.Federation!.Publisher.Should().Be(Publisher);
        steps.Should().Contain(s => s.Name == "federation" && s.Passed);
    }

    [Fact]
    public void A_host_outside_the_audience_refuses_the_pack_and_says_who_it_was_for()
    {
        var sample = new SamplePack();

        var result = sample.Verify(Federated(sample), Policy(sample, hostWebIds: [Stranger]));

        result.IsVerified.Should().BeFalse();
        result.Problems.Should().ContainSingle(p => p.Contains("published for " + Buyer) && p.Contains("this host is " + Stranger));
    }

    [Fact]
    public void A_host_that_names_no_WebId_of_its_own_refuses_a_pack_for_named_WebIds_but_takes_a_public_one()
    {
        var sample = new SamplePack();

        sample.Verify(Federated(sample), Policy(sample, hostWebIds: [])).Problems.Should().ContainSingle(p => p.Contains("names none of its own"));
        sample.Verify(Federated(sample, "\"public\""), Policy(sample, hostWebIds: [])).Problems.Should().BeEmpty();
    }

    [Fact]
    public void A_trusted_key_bound_to_no_WebId_does_not_publish_a_federated_pack()
    {
        var sample = new SamplePack();

        sample.Verify(Federated(sample), Policy(sample, webId: null)).Problems.Should().ContainSingle(p => p.Contains("No publisher signature from a key this host bound to"));
    }

    [Fact]
    public void A_trusted_key_bound_to_another_WebId_cannot_publish_in_this_ones_name()
    {
        var sample = new SamplePack();

        var problems = sample.Verify(Federated(sample), Policy(sample, webId: "https://other.example/profile/card#org")).Problems;

        problems.Should().Contain(p => p.Contains("is bound to https://other.example/profile/card#org"));
        problems.Should().Contain(p => p.Contains("No publisher signature from a key this host bound to " + Publisher));
    }

    [Fact]
    public void A_second_publisher_bound_to_another_WebId_refuses_the_pack_even_beside_the_right_one()
    {
        var sample = new SamplePack();
        var intruder = PackKey.Create();
        var files = Federated(sample);
        PackBuilder.Sign(files, intruder.PrivateKeyPem, "publisher", SamplePack.SignedAt);
        var policy = Policy(sample) with
        {
            Keys = [.. Policy(sample).Keys, new TrustedKey(intruder.KeyId, intruder.PublicKeyPem, ["io.example"], ["publisher"], null, null, "https://intruder.example/p#me")],
        };

        sample.Verify(files, policy).Problems.Should().ContainSingle(p => p.Contains("is bound to https://intruder.example/p#me"));
    }

    [Fact]
    public void A_reader_without_the_federation_profile_would_refuse_the_pack_rather_than_ignore_its_audience()
    {
        // What the extension mechanism promises any older reader: a critical extension it does not understand refuses the pack.
        PackManifest.UnderstoodExtensions.Should().Contain("federation");
        Parse(Extension("\"public\"").Replace("\"federation\"", "\"federation-v9\"")).Problem.Should().Contain("does not understand");
    }

    [Fact]
    public void A_trust_policy_binds_keys_to_WebIds_and_names_the_hosts_own()
    {
        var key = PackKey.Create();
        var policy = TrustPolicy.Parse(Encoding.UTF8.GetBytes($$"""
            {"format":"agentmemory.pack.trust/1.1","keys":[{"keyId":"{{key.KeyId}}","publicKey":{{System.Text.Json.JsonSerializer.Serialize(key.PublicKeyPem)}},"idPrefixes":["io.example"],"roles":["publisher"],"webId":"{{Publisher}}"}],"host":{"webIds":["{{Buyer}}"]} }
            """.Trim()));

        policy.Keys.Single().WebId.Should().Be(Publisher);
        policy.HostWebIds.Should().Equal(Buyer);
    }

    [Theory]
    [InlineData(""","webId":"http://supplier-a.example/p#me" """, "", "not a WebID")]
    [InlineData(""","webId":5""", "", "webId is a string")]
    [InlineData("", ""","host":{"webIds":[]}""", "1 to 16")]
    [InlineData("", ""","host":{"webIds":["nope"]}""", "not a WebID")]
    [InlineData("", ""","host":{"webIds":["https://b.example/p#me","https://b.example/p#me"]}""", "twice")]
    [InlineData("", ""","host":{"webIds":["https://b.example/p#me"],"name":"buyer"}""", "only 'webIds'")]
    [InlineData("", ""","host":["https://b.example/p#me"]""", "only 'webIds'")]
    public void A_malformed_WebId_binding_makes_the_policy_malformed(string keyField, string topField, string why)
    {
        var key = PackKey.Create();
        var json = $$"""{"format":"agentmemory.pack.trust/1.1","keys":[{"keyId":"{{key.KeyId}}","publicKey":{{System.Text.Json.JsonSerializer.Serialize(key.PublicKeyPem)}},"idPrefixes":["io.example"],"roles":["publisher"]{{keyField}}}]{{topField}}}""";

        var act = () => TrustPolicy.Parse(Encoding.UTF8.GetBytes(json));

        act.Should().Throw<InvalidDataException>().WithMessage($"*{why}*");
    }

    [Fact]
    public void A_revoker_key_is_never_bound_to_a_WebId()
    {
        var key = PackKey.Create();
        var json = $$"""{"format":"agentmemory.pack.trust/1.1","keys":[{"keyId":"{{key.KeyId}}","publicKey":{{System.Text.Json.JsonSerializer.Serialize(key.PublicKeyPem)}},"idPrefixes":["io.example"],"roles":["revoker"],"webId":"{{Publisher}}"}]}""";

        var act = () => TrustPolicy.Parse(Encoding.UTF8.GetBytes(json));

        act.Should().Throw<InvalidDataException>().WithMessage("*revoker*");
    }

    // ---- Publication receipts (federated packs §5) ----

    private sealed record Placement(SamplePack Sample, VerifiedPack Pack, byte[] Archive, TrustPolicy Policy);

    private static Placement Place(string audience = $"[\"{Buyer}\"]")
    {
        var sample = new SamplePack();
        var files = Federated(sample, audience);
        var policy = Policy(sample);
        var pack = sample.Verify(files, policy).Pack ?? throw new InvalidOperationException("The sample federated pack must verify.");
        return new Placement(sample, pack, PackBuilder.Pack(files), policy);
    }

    private static PublicationReceipt Receipt(Placement p, IReadOnlyList<string>? audience = null, string access = "wac", string location = Location,
        byte[]? archive = null, DateTimeOffset? at = null, bool everyone = false) =>
        Placed(p, everyone ? null : audience ?? [Buyer], access, location, archive, at);

    // The probe record a real publication would sign: anonymous refused unless public, the stranger refused unless in the
    // audience, the buyer read the bytes back.
    private static PublicationReceipt Placed(Placement p, IReadOnlyList<string>? audience, string access = "wac", string location = Location,
        byte[]? archive = null, DateTimeOffset? at = null) =>
        new(p.Pack.Id, p.Pack.Version, p.Pack.Digest, location, FilesList.Hex(archive ?? p.Archive), audience, access,
            audience is null ? [] : audience.Contains(Stranger) ? [PublicationReceipt.Anonymous] : [PublicationReceipt.Anonymous, Stranger],
            audience is null || audience.Contains(Buyer) ? [Buyer] : [], at ?? SamplePack.SignedAt.AddMinutes(1));

    private static byte[] Sign(PublicationReceipt receipt, PackKey key)
    {
        using var signer = PackSigner.FromPem(key.PrivateKeyPem);
        return receipt.Sign(signer);
    }

    private static IReadOnlyList<string> Check(Placement p, byte[] envelope, string location = Location, byte[]? archive = null, TrustPolicy? policy = null) =>
        PublicationReceipt.Verify(envelope, p.Pack, archive ?? p.Archive, location, policy ?? p.Policy, SamplePack.Now).Problems;

    [Fact]
    public void A_receipt_for_this_place_these_bytes_and_this_host_verifies()
    {
        var p = Place();
        var envelope = Sign(Receipt(p), p.Sample.Key);

        var (receipt, signedBy, problems) = PublicationReceipt.Verify(envelope, p.Pack, p.Archive, Location, p.Policy, SamplePack.Now);

        problems.Should().BeEmpty();
        signedBy.Should().Be(p.Sample.Key.KeyId, "a fetch re-checks the receipt's key in the publisher's profile too");
        receipt!.Location.Should().Be(Location);
        receipt.Audience.Should().Equal(Buyer);
        receipt.Denied.Should().Equal(PublicationReceipt.Anonymous, Stranger);
        PublicationReceipt.ArchiveNameFor(p.Pack.Digest).Should().Be("sha256-" + p.Pack.Digest[7..] + ".ampack");
        PublicationReceipt.FileNameFor(p.Pack.Digest).Should().EndWith(".receipt.dsse.json");
    }

    [Fact]
    public void A_receipt_does_not_vouch_for_another_place()
    {
        var p = Place();

        Check(p, Sign(Receipt(p), p.Sample.Key), location: "https://mirror.example/ampack/pack.ampack")
            .Should().ContainSingle(x => x.Contains("not at https://mirror.example/ampack/pack.ampack"));
    }

    [Fact]
    public void A_receipt_does_not_vouch_for_other_bytes()
    {
        var p = Place();
        var other = p.Archive.ToArray();
        other[^1] ^= 1;

        Check(p, Sign(Receipt(p), p.Sample.Key), archive: other).Should().ContainSingle(x => x.Contains("SHA-256 differs"));
    }

    [Fact]
    public void A_placement_can_only_narrow_the_packs_audience()
    {
        var p = Place();

        Check(p, Sign(Receipt(p, audience: [Buyer, Stranger]), p.Sample.Key)).Should().Contain(x => x.Contains("wider audience"));
        Check(p, Sign(Receipt(p, access: "public", everyone: true), p.Sample.Key)).Should().Contain(x => x.Contains("wider audience"));
    }

    [Fact]
    public void A_public_pack_may_be_placed_publicly_or_for_named_readers()
    {
        var p = Place("\"public\"");

        Check(p, Sign(Receipt(p, access: "public", everyone: true), p.Sample.Key)).Should().BeEmpty();
        Check(p, Sign(Receipt(p), p.Sample.Key)).Should().BeEmpty();
    }

    [Fact]
    public void A_host_outside_the_placements_audience_does_not_take_the_pack_from_there()
    {
        var p = Place();

        Check(p, Sign(Receipt(p), p.Sample.Key), policy: p.Policy with { HostWebIds = [Stranger] })
            .Should().ContainSingle(x => x.Contains("placement is for " + Buyer));
    }

    [Fact]
    public void A_receipt_signed_by_a_key_not_bound_to_the_publisher_does_not_count()
    {
        var p = Place();
        var other = PackKey.Create();
        var policy = p.Policy with
        {
            Keys = [.. p.Policy.Keys, new TrustedKey(other.KeyId, other.PublicKeyPem, ["io.example"], ["publisher"], null, null, "https://mirror.example/p#me")],
        };

        Check(p, Sign(Receipt(p), other), policy: policy).Should().ContainSingle(x => x.Contains("not bound to the pack's publisher"));
        Check(p, Sign(Receipt(p), PackKey.Create())).Should().ContainSingle(x => x.Contains("not in the trust policy"));
    }

    [Fact]
    public void A_receipt_needs_the_publisher_role_and_a_live_key()
    {
        var p = Place();

        Check(p, Sign(Receipt(p), p.Sample.Key), policy: p.Policy with { Keys = [p.Policy.Keys[0] with { Roles = ["curator"] }] })
            .Should().ContainSingle(x => x.Contains("may not sign as publisher"));
        Check(p, Sign(Receipt(p), p.Sample.Key), policy: p.Policy with { RevokedKeys = [new RevokedKey(p.Sample.Key.KeyId, null)] })
            .Should().ContainSingle(x => x.Contains("is revoked"));
        Check(p, Sign(Receipt(p, at: SamplePack.Now.AddHours(1)), p.Sample.Key)).Should().ContainSingle(x => x.Contains("in the future"));
    }

    [Fact]
    public void An_edited_receipt_does_not_verify()
    {
        var p = Place();
        var envelope = Encoding.UTF8.GetString(Sign(Receipt(p), p.Sample.Key));
        var payload = System.Text.Json.Nodes.JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(
            System.Text.Json.Nodes.JsonNode.Parse(envelope)!["payload"]!.GetValue<string>())))!;
        payload["predicate"]!["location"] = "https://mirror.example/ampack/pack.ampack";
        var edited = System.Text.Json.Nodes.JsonNode.Parse(envelope)!;
        edited["payload"] = Convert.ToBase64String(Jcs.Canonical(payload));

        Check(p, Jcs.Canonical(edited), location: "https://mirror.example/ampack/pack.ampack")
            .Should().ContainSingle(x => x.Contains("does not verify"));
    }

    [Fact]
    public void A_receipt_is_for_federated_packs_only()
    {
        var sample = new SamplePack();
        var files = sample.Signed();
        var pack = sample.Verify(files).Pack!;

        PublicationReceipt.Verify("{}"u8, pack, PackBuilder.Pack(files), Location, sample.Policy(), SamplePack.Now).Problems
            .Should().ContainSingle(x => x.Contains("for a federated pack"));
    }

    [Theory]
    [InlineData("wac", true)]
    [InlineData("public", false)]
    [InlineData("ftp", false)]
    public void Access_is_public_exactly_when_the_audience_is(string access, bool ok)
    {
        var p = Place();
        var act = () => Sign(Receipt(p, access: access), p.Sample.Key);

        if (ok) act.Should().NotThrow();
        else act.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("http://supplier-a.example/ampack/x.ampack")]
    [InlineData("https://supplier-a.example/ampack/x.ampack#frag")]
    [InlineData("ampack/x.ampack")]
    public void A_receipt_names_an_absolute_https_location_without_a_fragment(string location)
    {
        var p = Place();
        var act = () => Sign(Receipt(p, location: location), p.Sample.Key);

        act.Should().Throw<InvalidDataException>().WithMessage("*location*");
    }

    [Theory]
    [InlineData("\"extra\":1,", "other fields than this format defines")]
    public void A_receipt_with_a_field_the_format_does_not_define_is_refused(string insert, string why)
    {
        var p = Place();
        var envelope = System.Text.Json.Nodes.JsonNode.Parse(Encoding.UTF8.GetString(Sign(Receipt(p), p.Sample.Key)))!;
        var statement = Encoding.UTF8.GetString(Convert.FromBase64String(envelope["payload"]!.GetValue<string>()));
        var changed = Encoding.UTF8.GetBytes(statement.Replace("\"predicate\":{", "\"predicate\":{" + insert, StringComparison.Ordinal));

        PublicationReceipt.Read(changed).Problem.Should().Contain(why);
    }

    [Theory]
    [InlineData(300, true)]
    [InlineData(301, false)]
    public void A_purpose_counts_characters_as_both_verifiers_do(int emoji, bool ok)
    {
        var purpose = string.Concat(Enumerable.Repeat("\U0001F600", emoji));

        Parse(Extension("\"public\"", extra: ",\"purpose\":\"" + purpose + "\"")).Problem.Should().Be(ok ? null : "The federation purpose is display-safe text of 1 to 300 characters.");
    }

    [Fact]
    public void A_receipt_whose_probe_record_disagrees_with_its_placement_is_never_signed()
    {
        var p = Place();
        PublicationReceipt Record(IReadOnlyList<string>? audience, IReadOnlyList<string> denied, IReadOnlyList<string> allowed) =>
            new(p.Pack.Id, p.Pack.Version, p.Pack.Digest, Location, FilesList.Hex(p.Archive), audience, audience is null ? "public" : "wac", denied, allowed, SamplePack.SignedAt);

        foreach (var (receipt, why) in new[]
        {
            (Record([Buyer], [Stranger], [Buyer]), "anonymous was refused"),
            (Record([Buyer], [PublicationReceipt.Anonymous], [Stranger]), "outside its audience as allowed"),
            (Record([Buyer], [PublicationReceipt.Anonymous, Buyer], []), "in its audience as refused"),
            (Record(null, [PublicationReceipt.Anonymous], []), "public receipt records nobody as refused"),
            (Record(null, [Stranger], []), "public receipt records nobody as refused"),
            (Record(["not a webid"], [PublicationReceipt.Anonymous], []), "not a WebID"),
        })
            FluentActions.Invoking(() => Sign(receipt, p.Sample.Key)).Should().Throw<InvalidDataException>().WithMessage($"*{why}*");
    }

    private static Dictionary<string, byte[]> FederatedSet(SamplePack sample, string memberAudience, bool memberFederated = true, PackKey? memberKey = null)
    {
        var member = new SamplePack("io.example.member", "1.0.0");
        var memberSource = member.Source();
        if (memberFederated)
            memberSource["pack.json"] = SamplePack.Utf8($$"""{"format":"agentmemory.pack/1.2","id":"io.example.member","version":"1.0.0","kind":"knowledge","sdk":"[0.1,0.2)","tier":"open","licence":"MIT"{{Extension(memberAudience)}}}""");
        var (memberFiles, _) = PackBuilder.Build(memberSource);
        PackBuilder.Sign(memberFiles, (memberKey ?? sample.Key).PrivateKeyPem, "publisher", SamplePack.SignedAt);
        var set = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["pack.json"] = SamplePack.Utf8($$"""{"format":"agentmemory.pack/1.2","id":"io.example.bundle","version":"1.0.0","kind":"set","contains":["io.example.member"]{{Extension($"[\"{Buyer}\"]")}}}"""),
        };
        foreach (var (path, bytes) in memberFiles) set["packs/io.example.member/" + path] = bytes;
        var (files, _) = PackBuilder.Build(set);
        PackBuilder.Sign(files, sample.Key.PrivateKeyPem, "publisher", SamplePack.SignedAt);
        return files;
    }

    [Fact]
    public void A_federated_sets_members_are_federated_by_the_same_publisher_for_no_wider_an_audience()
    {
        var sample = new SamplePack();
        var policy = Policy(sample);
        IReadOnlyList<string> Problems(Dictionary<string, byte[]> files) => PackVerifier.VerifyWhole(new PackContent(files, "set"), policy, SamplePack.Now).Problems;

        Problems(FederatedSet(sample, $"[\"{Buyer}\"]")).Should().BeEmpty();
        Problems(FederatedSet(sample, $"[\"{Buyer}\"]", memberFederated: false)).Should().ContainSingle(x => x.Contains("not federated itself"));
        Problems(FederatedSet(sample, $"[\"{Buyer}\",\"{Stranger}\"]")).Should().ContainSingle(x => x.Contains("wider audience than its set"));
    }

    [Fact]
    public void Review9_A1_a_federated_sets_member_rules_need_no_key_so_the_writer_and_lint_hold_them_too()
    {
        // build and sign wrote, and lint passed, a federated set whose member was not federated or was for a wider audience;
        // every reader refused it without a key. The trust-free check holds the member rules now.
        var sample = new SamplePack();
        foreach (var (files, why) in new[]
                 {
                     (FederatedSet(sample, $"[\"{Buyer}\"]", memberFederated: false), "not federated itself"),
                     (FederatedSet(sample, $"[\"{Buyer}\",\"{Stranger}\"]"), "wider audience than its set"),
                 })
        {
            PackVerifier.CheckIntegrity(new PackContent(files, "lint")).Problems.Should().ContainSingle(x => x.Contains(why));
            FluentActions.Invoking(() => PackBuilder.Pack(files)).Should().Throw<InvalidDataException>().WithMessage($"*{why}*");
        }
        PackVerifier.CheckIntegrity(new PackContent(FederatedSet(sample, $"[\"{Buyer}\"]"), "lint")).Problems.Should().BeEmpty();
    }

    [Fact]
    public void A_set_is_verified_with_every_member_so_a_receipt_never_vouches_for_an_untrusted_member()
    {
        var sample = new SamplePack();
        var files = FederatedSet(sample, $"[\"{Buyer}\"]", memberKey: PackKey.Create());
        var policy = Policy(sample);

        PackVerifier.VerifyOne(new PackContent(files, "set"), policy, SamplePack.Now).IsVerified.Should().BeTrue("the outer signature alone holds");
        PackVerifier.VerifyWhole(new PackContent(files, "set"), policy, SamplePack.Now).Problems
            .Should().ContainSingle(x => x.StartsWith("packs/io.example.member/", StringComparison.Ordinal) && x.Contains("No valid publisher signature"));
    }
}
