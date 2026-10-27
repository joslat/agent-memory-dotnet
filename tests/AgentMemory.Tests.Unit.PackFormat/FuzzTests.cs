using System.Text;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.PackFormat;

/// <summary>
/// PACK-harden: bounded mutation fuzzing of every reader a pack meets before it is trusted. Each mutated input must be read,
/// or refused with a reason; never a crash, another exception type, or a pack that verifies with changed bytes. Seeded, so a
/// failure reproduces; the budget is a few thousand inputs per reader, run on every test pass.
/// </summary>
public sealed class FuzzTests
{
    private const int Budget = 3_000;
    private static readonly SamplePack Sample = new();
    private static readonly Dictionary<string, byte[]> Signed = Sample.Signed();
    private static readonly byte[] Archive = StrictZip.Write(Signed);

    // One random change: flip bits, overwrite, insert, delete, truncate or duplicate a span.
    private static byte[] Mutate(byte[] input, Random random)
    {
        var bytes = new List<byte>(input);
        for (var edits = random.Next(1, 4); edits > 0 && bytes.Count > 0; edits--)
        {
            var at = random.Next(bytes.Count);
            switch (random.Next(6))
            {
                case 0: bytes[at] ^= (byte)(1 << random.Next(8)); break;
                case 1: bytes[at] = (byte)random.Next(256); break;
                case 2: bytes.Insert(at, (byte)random.Next(256)); break;
                case 3: bytes.RemoveAt(at); break;
                case 4: bytes.RemoveRange(at, bytes.Count - at); break;
                default:
                    var length = Math.Min(random.Next(1, 32), bytes.Count - at);
                    bytes.InsertRange(at, bytes.GetRange(at, length));
                    break;
            }
        }
        return [.. bytes];
    }

    [Fact]
    public void A_mutated_archive_is_read_or_refused_and_never_verifies_with_changed_content()
    {
        var random = new Random(4_2026);
        var verified = 0;
        for (var run = 0; run < Budget; run++)
        {
            var mutated = Mutate(Archive, random);
            Dictionary<string, byte[]> files;
            try { files = StrictZip.Read(mutated, PackLimits.Default); }
            catch (InvalidDataException) { continue; }
            var result = PackVerifier.Verify(new PackContent(files, "fuzz"), Sample.Policy(), SamplePack.Now);
            if (!result.IsVerified) continue;
            verified++;
            // A mutation the profile tolerates must not change what the pack holds.
            files.Where(f => FilesList.IsListed(f.Key)).Should().BeEquivalentTo(Signed.Where(f => FilesList.IsListed(f.Key)),
                $"run {run}: a verified pack holds exactly the signed content");
        }
        verified.Should().BeLessThan(Budget / 10, "almost every change to an archive is refused");
    }

    [Fact]
    public void Mutated_JSON_is_checked_or_refused_with_a_reason_by_every_parser()
    {
        var random = new Random(1_3);
        byte[][] seeds =
        [
            Signed["pack.json"], Signed["META/files.json"], Signed["ontology.json"],
            Encoding.UTF8.GetBytes("{\"format\":\"agentmemory.pack.lock/1.1\",\"members\":[{\"digest\":\"sha256:" + new string('0', 64) + "\",\"id\":\"io.example.notes\",\"version\":\"1.0.0\"}]}"),
            Encoding.UTF8.GetBytes("{\"a\":[1,2.5,-3e2,\"x\\u00e9\",true,null,{\"b\":{}}]}"),
        ];
        for (var run = 0; run < Budget; run++)
        {
            var input = Mutate(seeds[run % seeds.Length], random);
            FluentActions.Invoking(() => PackJson.Check(input)).Should().NotThrow($"run {run}");
            FluentActions.Invoking(() => PackJson.CheckLines(input)).Should().NotThrow($"run {run}");
            FluentActions.Invoking(() => PackManifest.Parse(input)).Should().NotThrow($"run {run}");
            FluentActions.Invoking(() => FilesList.Parse(input)).Should().NotThrow($"run {run}");
            FluentActions.Invoking(() => PackLock.Parse(input)).Should().NotThrow($"run {run}");
            FluentActions.Invoking(() => Jcs.CheckCanonical(input)).Should().NotThrow($"run {run}");
        }
    }

    [Fact]
    public void A_mutated_trust_policy_parses_or_is_refused_as_malformed()
    {
        var random = new Random(4_15);
        var key = Sample.Key;
        var policy = Encoding.UTF8.GetBytes($$$"""{"format":"agentmemory.pack.trust/1.1","keys":[{"keyId":"{{{key.KeyId}}}","publicKey":{{{System.Text.Json.JsonSerializer.Serialize(key.PublicKeyPem)}}},"idPrefixes":["io.example"],"roles":["publisher"],"notAfter":"2099-01-01T00:00:00Z"}],"revoked":{"digests":[],"expires":"2099-01-01T00:00:00Z"}}""");
        TrustPolicy.Parse(policy).Keys.Should().ContainSingle();
        for (var run = 0; run < Budget; run++)
        {
            var input = Mutate(policy, random);
            try { TrustPolicy.Parse(input); }
            catch (InvalidDataException) { }
        }
    }

    [Fact]
    public void A_mutated_signature_envelope_never_counts_and_never_crashes()
    {
        var random = new Random(1_9);
        var (path, envelope) = Signed.Single(f => f.Key.StartsWith(PackPaths.SignaturesFolder, StringComparison.Ordinal));
        var listBytes = Signed[PackPaths.FilesList];
        var list = FilesList.Parse(listBytes).List!;
        var counted = 0;
        for (var run = 0; run < Budget; run++)
        {
            var input = Mutate(envelope, random);
            var (valid, _, _) = Dsse.Verify(path, input, listBytes, list, Sample.Policy(), SamplePack.Now);
            if (valid.Count > 0) counted++;
        }
        // A change that keeps the envelope valid can only be one that leaves the signed payload and the signature as they were.
        counted.Should().BeLessThan(Budget / 20);
    }
}
