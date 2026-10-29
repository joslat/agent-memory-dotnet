using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.PackFormat;

/// <summary>
/// The first full ampack review's low findings in the format, each fixed and held here: folders that differ only by case,
/// a folder reader bounded while it walks, records parsed only once the signatures hold, sets that carry sets, composites
/// with records, envelopes that sign nothing here, revocation lists told apart by what they sign, signer keys by curve, catalogs
/// pruned by version, and (found on the way) a malformed ledger named as malformed when it is saved.
/// </summary>
public sealed class Review1LowFindingsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("review1-low-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static Dictionary<string, byte[]> Signed(Dictionary<string, byte[]> source, PackKey key, string role = "publisher")
    {
        var (files, _) = PackBuilder.Build(source);
        PackBuilder.Sign(files, key.PrivateKeyPem, role, SamplePack.SignedAt);
        return files;
    }

    [Fact]
    public void L1_folders_that_differ_only_by_case_and_a_file_named_like_a_folder_are_refused()
    {
        PackPaths.Problems(["records/a.jsonl", "records/b.jsonl"]).Should().BeEmpty();
        PackPaths.Problems(["assets/Icons/a.png", "assets/icons/b.png"]).Should().ContainSingle(p => p.Contains("Folders differ only by case"));
        PackPaths.Problems(["notes", "notes/a.json"]).Should().ContainSingle(p => p.Contains("both a file and a folder"));
    }

    [Fact]
    public void L2_a_pack_folder_deeper_than_a_pack_path_may_be_is_refused_while_it_is_walked()
    {
        var folder = Path.Combine(_root, "deep");
        var leaf = Path.Combine([folder, .. Enumerable.Range(0, PackPaths.MaximumSegments).Select(i => "d" + i)]);
        Directory.CreateDirectory(leaf);
        File.WriteAllText(Path.Combine(leaf, "x.json"), "{}");

        FluentActions.Invoking(() => PackReader.ReadFolder(folder)).Should().Throw<InvalidDataException>().WithMessage("*deeper than a pack path*");
    }

    [Fact]
    public void L3_records_are_parsed_only_once_the_signatures_hold()
    {
        var sample = new SamplePack();
        var files = sample.Signed(extraRecord: "{\"key\":\"a\",\"key\":\"b\"}\n");
        var untrusted = new SamplePack().Policy();

        sample.Verify(files, untrusted).Problems.Should().NotContain(p => p.Contains("records/note.jsonl"), "an untrusted pack's records are never parsed");
        sample.Verify(files).Problems.Should().ContainSingle(p => p.Contains("records/note.jsonl") && p.Contains("line 2"));
    }

    [Fact]
    public void L5_a_set_carries_packs_not_sets_and_no_member_shares_its_id()
    {
        var sample = new SamplePack();
        var policy = sample.Policy();
        Dictionary<string, byte[]> Set(string id, params (string Id, Dictionary<string, byte[]> Files)[] members)
        {
            var source = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["pack.json"] = Utf8($$"""{"format":"agentmemory.pack/1.2","id":"{{id}}","version":"1.0.0","kind":"set","contains":[{{string.Join(",", members.Select(m => $"\"{m.Id}\""))}}]}"""),
            };
            foreach (var (member, files) in members)
                foreach (var (path, bytes) in files) source[$"packs/{member}/{path}"] = bytes;
            return Signed(source, sample.Key);
        }
        IReadOnlyList<string> Problems(Dictionary<string, byte[]> files) => PackVerifier.VerifyWhole(new PackContent(files, "set"), policy, SamplePack.Now).Problems;
        var notes = sample.Signed();

        Problems(Set("io.example.bundle", ("io.example.notes", notes))).Should().BeEmpty();
        Problems(Set("io.example.outer", ("io.example.bundle", Set("io.example.bundle", ("io.example.notes", notes)))))
            .Should().ContainSingle(p => p.Contains("a set carries packs, not sets"));
        var sameId = new SamplePack("io.example.bundle");
        Problems(Set("io.example.bundle", ("io.example.bundle", Signed(sameId.Source(), sample.Key))))
            .Should().ContainSingle(p => p.Contains("carries a member with its own id"));
    }

    [Fact]
    public void L6_a_composite_holds_no_records_and_its_members_json_holds_members_only()
    {
        var lockBytes = Utf8("{\"format\":\"agentmemory.pack.lock/1.1\",\"members\":[{\"digest\":\"sha256:" + new string('a', 64) + "\",\"id\":\"io.example.notes\",\"version\":\"1.0.0\"}]}");
        IReadOnlyList<string> Problems(string members, bool records)
        {
            var source = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["pack.json"] = Utf8("""{"format":"agentmemory.pack/1.2","id":"io.example.family","version":"1.0.0","kind":"composite","contains":["io.example.notes"]}"""),
                ["members.json"] = Utf8(members),
                [PackPaths.LockFile] = lockBytes,
            };
            if (records) source["records/x.jsonl"] = Utf8("{\"key\":\"x\"}\n");
            var (files, _) = PackBuilder.Build(source);
            return PackVerifier.CheckIntegrity(new PackContent(files, "composite")).Problems;
        }

        Problems("""{"members":[{"id":"io.example.notes"}]}""", records: false).Should().BeEmpty();
        Problems("""{"members":[{"id":"io.example.notes"}]}""", records: true).Should().ContainSingle(p => p.Contains("no records of its own"));
        Problems("""{"members":[{"id":"io.example.notes"}],"order":"alphabetical"}""", records: false).Should().ContainSingle(p => p.Contains("members.json is malformed"));
    }

    [Fact]
    public void L7_an_envelope_that_signs_nothing_here_refuses_the_pack_and_an_untrusted_countersignature_does_not()
    {
        var sample = new SamplePack();
        var files = sample.Signed();
        var stranger = PackKey.Create();

        // A curator's envelope over another list, carried beside this pack.
        var other = Signed(sample.Source(extraRecord: "{\"key\":\"n2\",\"title\":\"Second\",\"text\":\"Bye\"}\n"), stranger, "curator");
        var foreign = other.Single(f => PackPaths.IsSignature(f.Key));
        sample.Verify(new Dictionary<string, byte[]>(files) { [foreign.Key] = foreign.Value }).Problems
            .Should().ContainSingle(p => p.Contains("signs another META/files.json"));

        // An envelope with no signature entry.
        var own = files.Single(f => PackPaths.IsSignature(f.Key));
        var empty = JsonNode.Parse(own.Value)!.AsObject();
        empty["signatures"] = new JsonArray();
        sample.Verify(new Dictionary<string, byte[]>(files) { ["META/signatures/empty.dsse.json"] = Utf8(empty.ToJsonString()) }).Problems
            .Should().ContainSingle(p => p.Contains("holds no signature"));

        // A countersignature of this very list by a key the host does not trust: ignored, as before.
        var countersigned = new Dictionary<string, byte[]>(files);
        PackBuilder.Sign(countersigned, stranger.PrivateKeyPem, "curator", SamplePack.SignedAt);
        sample.Verify(countersigned).IsVerified.Should().BeTrue();
    }

    [Fact]
    public void L8_a_run_takes_at_most_64_revocation_lists_and_one_list_in_two_envelopes_counts_once()
    {
        var sample = new SamplePack();
        var revoker = PackKey.Create();
        var policy = sample.Policy() with
        {
            Keys = [.. sample.Policy().Keys, new TrustedKey(revoker.KeyId, revoker.PublicKeyPem, ["io.example"], [Revocations.Role], null, null)],
        };
        var digest = "sha256:" + new string('b', 64);
        var list = Revocations.Sign([digest], [], SamplePack.Now.AddMinutes(-1), SamplePack.Now.AddDays(7), revoker.PrivateKeyPem);
        var reencoded = Utf8("{ " + Encoding.UTF8.GetString(list)[1..]);
        reencoded.Should().NotEqual(list);

        var applied = Revocations.ApplyAll(policy, [list, reencoded], SamplePack.Now, null, out _);
        applied.RevokedDigests.Should().ContainSingle().Which.Should().Be(digest);
        FluentActions.Invoking(() => Revocations.ApplyAll(policy, [.. Enumerable.Repeat(list, Revocations.MaximumDocuments + 1)], SamplePack.Now, null, out _))
            .Should().Throw<InvalidDataException>().WithMessage("*more than 64*");
    }

    [Fact]
    public void L9_a_signer_key_is_P256_by_its_curve_and_a_revocation_window_is_whole_seconds()
    {
        using var brainpool = ECDsa.Create(ECCurve.NamedCurves.brainpoolP256r1);
        brainpool.KeySize.Should().Be(256, "the old check by size alone let this curve through");
        FluentActions.Invoking(() => PackSigner.FromPem(brainpool.ExportPkcs8PrivateKeyPem())).Should().Throw<ArgumentException>().WithMessage("*P-256*");

        var revoker = PackKey.Create();
        FluentActions.Invoking(() => Revocations.Sign([], [], SamplePack.Now, SamplePack.Now.AddMilliseconds(500), revoker.PrivateKeyPem))
            .Should().Throw<ArgumentException>().WithMessage("*whole seconds*");
    }

    [Fact]
    public void L12_a_catalog_past_what_readers_read_drops_the_oldest_versions_first_and_never_hides_a_pack()
    {
        static AgentMemory.Packs.Solid.CatalogEntry Entry(string id, string version) =>
            new(id, version, "sha256:" + Convert.ToHexString(SHA256.HashData(Utf8(id + version))).ToLowerInvariant(), $"https://pod.example/{id}@{version}.ampack",
                $"https://pod.example/{id}@{version}.receipt.dsse.json", null);
        var distinct = Enumerable.Range(0, AgentMemory.Packs.Solid.SolidPacks.MaximumEntries - 1).Select(i => Entry($"io.example.p{i}", "1.0.0")).ToList();

        var pruned = AgentMemory.Packs.Solid.Catalog.Pruned([.. distinct, Entry("io.example.p0", "1.1.0"), Entry("io.example.p0", "1.2.0")]);
        pruned.Should().HaveCount(AgentMemory.Packs.Solid.SolidPacks.MaximumEntries);
        pruned.Where(e => e.Id == "io.example.p0").Select(e => e.Version).Should().BeEquivalentTo(["1.1.0", "1.2.0"], "the oldest version goes first");

        FluentActions.Invoking(() => AgentMemory.Packs.Solid.Catalog.Pruned([.. distinct, Entry("io.example.extra-1", "1.0.0"), Entry("io.example.extra-2", "1.0.0")]))
            .Should().Throw<InvalidDataException>().WithMessage("*distinct packs*");
    }

    [Fact]
    public void Saving_over_a_malformed_ledger_says_it_is_malformed()
    {
        var path = Path.Combine(_root, "ledger.json");
        File.WriteAllText(path, "{\"format\":1}");

        FluentActions.Invoking(() => HostLedger.Load(Path.Combine(_root, "none.json")).Save(path))
            .Should().Throw<InvalidDataException>().WithMessage("*malformed*");
    }
}
