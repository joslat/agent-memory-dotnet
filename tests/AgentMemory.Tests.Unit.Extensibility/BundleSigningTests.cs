using System.Security.Cryptography;
using System.Text;
using AgentMemory.Extensibility.Bundles;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>Signed bundles (Extensibility 0.9 §A6, applied to knowledge packs in 0.13 §K6).</summary>
public sealed class BundleSigningTests
{
    private static readonly BundleKey Developer = BundleSigning.CreateKey();

    private static Dictionary<string, byte[]> Content() => new(StringComparer.Ordinal)
    {
        ["manifest.json"] = Encoding.UTF8.GetBytes("""{"id":"io.example.pack","version":"1.0.0"}"""),
        ["seed/records.jsonl"] = Encoding.UTF8.GetBytes("""{"type":"T","key":"a"}""" + "\n"),
    };

    private static Dictionary<string, byte[]> Signed(BundleKey? key = null, Dictionary<string, byte[]>? content = null) =>
        new(BundleSigning.Sign(content ?? Content(), "io.example.pack", "1.0.0", (key ?? Developer).PrivateKeyPem), StringComparer.Ordinal);

    private static BundleTrust Trust(params BundleKey[] keys)
    {
        var trust = new BundleTrust();
        foreach (var key in keys.Length == 0 ? [Developer] : keys) trust.DeveloperKeys.Add(key.PublicKeyPem);
        return trust;
    }

    [Fact]
    public void ASignedBundleVerifiesWithItsDeveloperKeyAndKeepsOnlyTheContent()
    {
        var signed = Signed();
        signed.Keys.Should().Contain([BundleSigning.FilesListName, BundleSigning.SignatureName]);

        var result = BundleVerifier.Verify(signed, Trust());

        result.Problems.Should().BeEmpty();
        var bundle = result.Bundle!;
        bundle.Id.Should().Be("io.example.pack");
        bundle.Version.Should().Be("1.0.0");
        bundle.Signer.Should().Be(BundleSigner.Developer);
        bundle.KeyId.Should().Be(Developer.KeyId);
        bundle.Digest.Should().Be(Convert.ToHexString(SHA256.HashData(signed[BundleSigning.FilesListName])).ToLowerInvariant());
        bundle.Files.Keys.Should().BeEquivalentTo(["manifest.json", "seed/records.jsonl"]);
        bundle.Files["manifest.json"].ToArray().Should().Equal(Content()["manifest.json"]);
    }

    [Fact]
    public void TheVerifiedBytesAreACopyThatLaterChangesToTheInputCannotReach()
    {
        var signed = Signed();
        var bundle = BundleVerifier.Verify(signed, Trust()).Bundle!;
        signed["manifest.json"][0] ^= 0xFF;
        bundle.Files["manifest.json"].ToArray().Should().Equal(Content()["manifest.json"]);
    }

    [Fact]
    public void TheKeyIdIsTheFirstSixteenHexCharactersOfTheSha256OfThePublicKeyInfo()
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(Developer.PublicKeyPem);
        var expected = Convert.ToHexString(SHA256.HashData(ecdsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant()[..16];
        Developer.KeyId.Should().Be(expected);
        BundleSigning.KeyIdOf(Developer.PublicKeyPem).Should().Be(expected);
    }

    [Fact]
    public void OneChangedByteIsRefused()
    {
        var signed = Signed();
        signed["seed/records.jsonl"][2] ^= 0x01;
        Refused(signed).Should().Contain(p => p.Contains("seed/records.jsonl", StringComparison.Ordinal));
    }

    [Fact]
    public void AnExtraFileIsRefused()
    {
        var signed = Signed();
        signed["seed/extra.jsonl"] = [1, 2, 3];
        Refused(signed).Should().Contain(p => p.Contains("seed/extra.jsonl", StringComparison.Ordinal));
    }

    [Fact]
    public void AMissingFileIsRefused()
    {
        var signed = Signed();
        signed.Remove("seed/records.jsonl");
        Refused(signed).Should().Contain(p => p.Contains("seed/records.jsonl", StringComparison.Ordinal));
    }

    [Fact]
    public void AKeyOutsideTheTrustSetIsRefused()
    {
        var other = BundleSigning.CreateKey();
        Refused(Signed(other)).Should().Contain(p => p.Contains("not trusted", StringComparison.Ordinal));
    }

    [Fact]
    public void ARevokedKeyOrDigestIsRefused()
    {
        var signed = Signed();
        var revokedKey = Trust();
        revokedKey.RevokedKeyIds.Add(Developer.KeyId);
        BundleVerifier.Verify(signed, revokedKey).Problems.Should().Contain(p => p.Contains("revoked", StringComparison.Ordinal));

        var digest = BundleVerifier.Verify(signed, Trust()).Bundle!.Digest;
        var revokedDigest = Trust();
        revokedDigest.RevokedDigests.Add(digest);
        BundleVerifier.Verify(signed, revokedDigest).Problems.Should().Contain(p => p.Contains("revoked", StringComparison.Ordinal));
    }

    [Fact]
    public void APinMustEqualTheDigest()
    {
        var signed = Signed();
        var digest = BundleVerifier.Verify(signed, Trust()).Bundle!.Digest;
        BundleVerifier.Verify(signed, Trust(), pin: digest).IsVerified.Should().BeTrue();
        BundleVerifier.Verify(signed, Trust(), pin: new string('0', 64)).Problems
            .Should().Contain(p => p.Contains("pin", StringComparison.Ordinal));
    }

    [Fact]
    public void ATamperedFilesListFailsTheSignature()
    {
        var signed = Signed();
        var list = Encoding.UTF8.GetString(signed[BundleSigning.FilesListName]).Replace("1.0.0", "1.0.1", StringComparison.Ordinal);
        signed[BundleSigning.FilesListName] = Encoding.UTF8.GetBytes(list);
        Refused(signed).Should().Contain(p => p.Contains("signature", StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutBothTrustFilesNothingVerifies()
    {
        var noSignature = Signed();
        noSignature.Remove(BundleSigning.SignatureName);
        Refused(noSignature).Should().NotBeEmpty();
        var noList = Signed();
        noList.Remove(BundleSigning.FilesListName);
        Refused(noList).Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("../escape.json")]
    [InlineData("seed\\records.jsonl")]
    [InlineData("/absolute.json")]
    [InlineData("seed//double.json")]
    [InlineData("C:/drive.json")]
    [InlineData("space name.json")]
    [InlineData(".hidden")]
    public void UnsafePathsCannotBeSigned(string path)
    {
        var content = Content();
        content[path] = [1];
        var sign = () => BundleSigning.Sign(content, "io.example.pack", "1.0.0", Developer.PrivateKeyPem);
        sign.Should().Throw<ArgumentException>().WithMessage("*" + path + "*");
    }

    [Fact]
    public void PathsThatDifferOnlyByCaseCannotBeSigned()
    {
        var content = Content();
        content["Manifest.json"] = [1];
        var sign = () => BundleSigning.Sign(content, "io.example.pack", "1.0.0", Developer.PrivateKeyPem);
        sign.Should().Throw<ArgumentException>().WithMessage("*case*");
    }

    [Fact]
    public void TrustFileNamesAreReservedInTheContent()
    {
        var content = Content();
        content[BundleSigning.SignatureName] = [1];
        var sign = () => BundleSigning.Sign(content, "io.example.pack", "1.0.0", Developer.PrivateKeyPem);
        sign.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AnOfficialKeyVerifiesAsOfficialWithoutAnyConfiguration()
    {
        var official = BundleSigning.CreateKey();
        var result = BundleVerifier.Verify(Signed(official), new BundleTrust(), pin: null, officialKeys: [official.PublicKeyPem]);
        result.Problems.Should().BeEmpty();
        result.Bundle!.Signer.Should().Be(BundleSigner.Official);
    }

    [Fact]
    public void ADirectoryReadsAsTheSameBundleWithSlashSeparators()
    {
        var root = Directory.CreateTempSubdirectory("bundle-").FullName;
        try
        {
            foreach (var (path, bytes) in Signed())
            {
                var file = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllBytes(file, bytes);
            }
            var read = BundleFiles.ReadDirectory(root);
            read.Keys.Should().Contain("seed/records.jsonl");
            BundleVerifier.Verify(read, Trust()).IsVerified.Should().BeTrue();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static IReadOnlyList<string> Refused(Dictionary<string, byte[]> files)
    {
        var result = BundleVerifier.Verify(files, Trust());
        result.IsVerified.Should().BeFalse();
        result.Bundle.Should().BeNull();
        return result.Problems;
    }
}
