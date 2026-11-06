using System.Text;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.PackFormat;

/// <summary>The JSON profile, the Git-folder form and the host ledger (red-team M1, M2, H4).</summary>
public sealed class ProfileAndLedgerTests
{
    [Theory]
    [InlineData("{\"a\":1,\"a\":2}", "repeats the key")]
    [InlineData("\uFEFF{}", "byte order mark")]
    [InlineData("{\"a\":1}\r", "carriage return")]
    [InlineData("{\"a\":\"e\u0301\"}", "NFC")]
    [InlineData("{\"a\":9007199254740993}", "2^53")]
    [InlineData("{\"a\":\"\\ud800\"}", "surrogate")]
    public void The_profile_refuses_what_a_strict_parser_elsewhere_would(string json, string reason)
    {
        PackJson.Check(Encoding.UTF8.GetBytes(json)).Should().Contain(reason);
    }

    [Fact]
    public void Review7_the_authentication_every_load_path_runs_holds_the_host_s_kept_revocations()
    {
        // D10: a key this host's ledger revoked passed the step that picks a loader, so its pack.json was parsed and shown as
        // authenticated before the loader refused it.
        var sample = new SamplePack();
        var content = new PackContent(sample.Signed(), "test");
        var ledger = new HostLedger();
        ledger.Record([], [new RevokedKey(sample.Key.KeyId, null)]);

        PackVerifier.Authenticated(content, sample.Policy(), SamplePack.Now).Manifest.Should().NotBeNull();
        var (manifest, problems) = PackVerifier.Authenticated(content, sample.Policy(), SamplePack.Now, ledger);
        manifest.Should().BeNull();
        problems.Should().NotBeEmpty().And.BeEquivalentTo(PackVerifier.VerifyOne(content, sample.Policy(), SamplePack.Now, ledger).Problems,
            "the step that picks a loader refuses for the reason the full verification gives");
    }
    [Fact]
    public void A_record_with_a_repeated_key_fails_the_pack()
    {
        var sample = new SamplePack();
        var files = sample.Signed(extraRecord: "{\"key\":\"n2\",\"key\":\"n3\"}\n");
        sample.Verify(files).Problems.Should().Contain(p => p.Contains("line 2 repeats the key 'key'"));
    }

    [Fact]
    public void A_checkout_that_turned_LF_into_CRLF_is_named_as_the_cause()
    {
        var sample = new SamplePack();
        var files = sample.Signed();
        files["records/note.jsonl"] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(files["records/note.jsonl"]).Replace("\n", "\r\n"));
        sample.Verify(files).Problems.Should().ContainSingle().Which.Should().Contain("CRLF").And.Contain("-text");
    }

    [Fact]
    public void A_Git_LFS_pointer_is_named_as_the_cause()
    {
        var sample = new SamplePack();
        var files = sample.Signed();
        files["ontology.json"] = SamplePack.Utf8("version https://git-lfs.github.com/spec/v1\noid sha256:00\nsize 10\n");
        sample.Verify(files).Problems.Should().ContainSingle().Which.Should().Contain("Git LFS pointer");
    }

    [Fact]
    public void A_folder_verifies_with_its_version_control_files_beside_it()
    {
        var sample = new SamplePack();
        var root = Directory.CreateTempSubdirectory("ampack-").FullName;
        try
        {
            foreach (var (path, bytes) in sample.Signed())
            {
                var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, bytes);
            }
            File.WriteAllText(Path.Combine(root, ".gitattributes"), PackBuilder.GitAttributes);
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            File.WriteAllText(Path.Combine(root, ".git", "HEAD"), "ref: refs/heads/main\n");

            sample.Verify(PackReader.ReadFolder(root).Files).Problems.Should().BeEmpty();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void The_ledger_refuses_a_downgrade_even_after_the_pack_was_removed()
    {
        var ledger = new HostLedger();
        ledger.Record("io.example.notes", "1.2.0", "sha256:b");
        // Removing the pack from the store does not touch the ledger.
        ledger.Refusal("io.example.notes", "1.1.0", "sha256:a").Should().Contain("downgrade");
        ledger.Refusal("io.example.notes", "1.1.0", "sha256:a", allowDowngrade: true).Should().BeNull();
        ledger.Refusal("io.example.notes", "1.3.0", "sha256:c").Should().BeNull();
    }

    [Fact]
    public void The_ledger_refuses_a_known_version_with_other_content_including_build_metadata_variants()
    {
        var ledger = new HostLedger();
        ledger.Record("io.example.notes", "1.0.0+a", "sha256:a");
        ledger.Refusal("io.example.notes", "1.0.0+a", "sha256:a").Should().BeNull("a reinstall of the same content");
        ledger.Refusal("io.example.notes", "1.0.0+a", "sha256:x").Should().Contain("other content");
        ledger.Refusal("io.example.notes", "1.0.0+b", "sha256:x").Should().Contain("other content");
    }

    [Fact]
    public void The_ledger_survives_a_save_and_a_load()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ampack-ledger-{Guid.NewGuid():N}.json");
        try
        {
            var ledger = new HostLedger();
            ledger.Record("io.example.notes", "2.0.0", "sha256:b");
            ledger.Save(path);
            HostLedger.Load(path).Refusal("io.example.notes", "1.0.0", "sha256:a").Should().Contain("downgrade");
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("1.0.0-alpha", "1.0.0", -1)]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta", -1)]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11", -1)]
    [InlineData("1.0.0+x", "1.0.0+y", 0)]
    [InlineData("2.0.0", "10.0.0", -1)]
    public void SemVer_precedence_follows_the_spec(string a, string b, int expected)
    {
        SemVer.TryParse(a, out var left).Should().BeTrue();
        SemVer.TryParse(b, out var right).Should().BeTrue();
        Math.Sign(left.CompareTo(right)).Should().Be(expected);
    }
}
