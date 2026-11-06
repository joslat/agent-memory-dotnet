using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.PackFormat;

/// <summary>
/// The second red team's findings on the format library (27 Oct), each held here: no exception escapes the verifier on JSON
/// of the wrong type or an extreme integer; strings keep the Stream-Safe limit, which bounds the NFC work; base64 is strict;
/// a field of the wrong type is refused, never read as absent; a deflate stream needs its final block; versions take ASCII
/// digits. Structure-aware fuzzing puts every JSON type in every field and signs the result again, so the parsers behind
/// the signature check are reached.
/// </summary>
public sealed class RedTeamTests
{
    private readonly SamplePack _sample = new();

    private PackVerification VerifyWith(Func<Dictionary<string, byte[]>, Dictionary<string, byte[]>> change, bool resign = true)
    {
        var source = change(_sample.Source());
        if (!resign) return _sample.Verify(source);
        // Listed and signed directly, not through the builder, which refuses a bad pack.json before it is ever signed.
        var files = new Dictionary<string, byte[]>(source, StringComparer.Ordinal) { [PackPaths.FilesList] = FilesList.Build(_sample.Id, _sample.Version, source) };
        SamplePack.SignRaw(files, _sample.Key.PrivateKeyPem, "publisher", SamplePack.SignedAt);
        return _sample.Verify(files);
    }

    private static Dictionary<string, byte[]> WithManifest(Dictionary<string, byte[]> source, Action<JsonObject> change)
    {
        var manifest = JsonNode.Parse(source["pack.json"])!.AsObject();
        change(manifest);
        source["pack.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        return source;
    }

    [Fact]
    public void The_smallest_64_bit_integer_is_refused_not_an_overflow()
    {
        PackJson.Check("{\"n\":-9223372036854775808}"u8).Should().Contain("beyond ±2^53");
        FluentActions.Invoking(() => Jcs.CheckCanonical("{\"n\":-9223372036854775808}"u8)).Should().NotThrow();
    }

    [Theory]
    [InlineData("validUntil", "1767225600")]
    [InlineData("protection", "null")]
    [InlineData("protection", "[\"licensed\"]")]
    [InlineData("code", "[\"E2\"]")]
    [InlineData("tier", "1")]
    [InlineData("sdk", "1")]
    [InlineData("licence", "{}")]
    [InlineData("display", "\"x\"")]
    [InlineData("requires", "[]")]
    public void A_field_of_the_wrong_type_is_refused_never_read_as_absent(string field, string json)
    {
        var result = VerifyWith(s => WithManifest(s, m => m[field] = JsonNode.Parse(json)));

        result.IsVerified.Should().BeFalse($"{field} = {json}");
        result.Problems.Should().Contain(p => p.Contains($"'{field}'", StringComparison.Ordinal));
    }

    [Fact]
    public void Strings_keep_the_stream_safe_limit_of_30_combining_marks()
    {
        PackJson.Check(Encoding.UTF8.GetBytes("{\"k\":\"a" + new string('́', 30) + "\"}")).Should().NotContain("in a row");
        PackJson.Check(Encoding.UTF8.GetBytes("{\"k\":\"a" + new string('́', 31) + "\"}")).Should().Contain("more than 30 combining marks in a row");
        // The red team's input: a long run of marks of two classes, which an unbounded NFC check sorts in quadratic time.
        var attack = "{\"k\":\"￾a" + new string('́', 20_000) + new string('̖', 20_000) + "\"}";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        PackJson.Check(Encoding.UTF8.GetBytes(attack)).Should().Contain("in a row");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void The_stream_safe_count_is_on_the_NFKD_decomposition_and_covers_keys()
    {
        // Third review: U+0F73 is a starter that decomposes to two marks; a run of them bypassed a per-code-point count.
        PackJson.Check(Encoding.UTF8.GetBytes("{\"k\":\"q" + new string('\u0F73', 16) + "\"}")).Should().Contain("more than 30 combining marks in a row");
        // U+00A8 is a starter that trails one mark once decomposed: with 29 more it is 30, with 30 it is 31.
        PackJson.Check(Encoding.UTF8.GetBytes("{\"k\":\"q\u00A8" + new string('\u0316', 29) + "\"}")).Should().BeNull();
        PackJson.Check(Encoding.UTF8.GetBytes("{\"k\":\"q\u00A8" + new string('\u0316', 30) + "\"}")).Should().Contain("in a row");
        // A key is a string too: the same limit, before the NFC check.
        PackJson.Check(Encoding.UTF8.GetBytes("{\"q" + new string('\u0301', 30) + "\":1}")).Should().BeNull();
        PackJson.Check(Encoding.UTF8.GetBytes("{\"q" + new string('\u0301', 31) + "\":1}")).Should().Contain("key with more than 30 combining marks");
        var attack = "{\"q" + string.Concat(Enumerable.Repeat("\u0301\u0316", 20_000)) + "\":1}";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        PackJson.Check(Encoding.UTF8.GetBytes(attack)).Should().Contain("in a row");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "the limit is checked before the normalizer runs");
    }

    [Fact]
    public void A_long_path_costs_nothing_until_something_is_refused()
    {
        // Fourth review: the path was built for every child, and a key can be as long as the file (26 s for a megabyte).
        var json = "{\"key\":\"a\",\"" + new string('k', 200_000) + "\":[" + string.Join(",", Enumerable.Repeat("0", 200_000)) + "]}";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        PackJson.Check(Encoding.UTF8.GetBytes(json)).Should().BeNull();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        PackJson.Check("{\"a\":[1,{\"b\":9007199254740993}]}"u8).Should().Contain("at $.a[1].b");
    }

    [Fact]
    public void A_ledger_with_a_field_this_host_does_not_know_is_refused_not_half_read()
    {
        var path = Path.Combine(Path.GetTempPath(), "ledger-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"format\":\"agentmemory.pack.ledger/1.1\",\"packs\":{},\"revocationsIssuedAt\":\"2026-10-27T00:00:00Z\"}");
            FluentActions.Invoking(() => HostLedger.Load(path)).Should().Throw<InvalidDataException>().WithMessage("*revocationsIssuedAt*");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_policy_key_on_another_curve_is_refused()
    {
        using var p384 = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP384);
        var pem = p384.ExportSubjectPublicKeyInfoPem();
        var policy = "{\"format\":\"agentmemory.pack.trust/1.1\",\"keys\":[{\"keyId\":\"" + PackKey.KeyIdOf(pem) + "\",\"publicKey\":"
            + System.Text.Json.JsonSerializer.Serialize(pem) + ",\"idPrefixes\":[\"io.example\"],\"roles\":[\"publisher\"]}]}";
        FluentActions.Invoking(() => TrustPolicy.Parse(Encoding.UTF8.GetBytes(policy))).Should().Throw<InvalidDataException>().WithMessage("*P-256*");
    }

    [Fact]
    public void A_policy_is_read_strictly_one_BOM_strict_UTF8_and_typed_revoked_keys()
    {
        var key = PackKey.Create();
        string Policy(string extra = "") => "{\"format\":\"agentmemory.pack.trust/1.1\",\"keys\":[{\"keyId\":\"" + key.KeyId + "\",\"publicKey\":"
            + System.Text.Json.JsonSerializer.Serialize(key.PublicKeyPem) + ",\"idPrefixes\":[\"io.example\"],\"roles\":[\"publisher\"]}]" + extra + "}";
        TrustPolicy.Parse(Encoding.UTF8.GetBytes("\uFEFF" + Policy())).Keys.Should().ContainSingle();
        FluentActions.Invoking(() => TrustPolicy.Parse(Encoding.UTF8.GetBytes("\uFEFF\uFEFF" + Policy()))).Should().Throw<InvalidDataException>();
        var bytes = Encoding.UTF8.GetBytes(Policy()).ToList();
        bytes.Insert(bytes.Count - 3, 0xFF);
        FluentActions.Invoking(() => TrustPolicy.Parse([.. bytes])).Should().Throw<InvalidDataException>();
        FluentActions.Invoking(() => TrustPolicy.Parse(Encoding.UTF8.GetBytes(Policy(",\"revoked\":{\"keys\":[{\"keyId\":null}]}")))).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Base64_with_whitespace_or_outside_the_alphabet_is_refused()
    {
        Dsse.FromBase64("QUJD").Should().Equal("ABC"u8.ToArray());
        Dsse.FromBase64("QU JD").Should().BeNull();
        Dsse.FromBase64("QUJD\n").Should().BeNull();
        Dsse.FromBase64("QUJ*").Should().BeNull();
        Dsse.FromBase64("QUJ").Should().BeNull();
    }

    [Fact]
    public void A_deflate_stream_without_its_final_block_is_refused()
    {
        var content = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("a line, again\n", 100)));
        using var packed = new MemoryStream();
        using var deflater = new DeflateStream(packed, CompressionLevel.Optimal, leaveOpen: true);
        deflater.Write(content);
        deflater.Flush(); // a sync flush: every byte of the data is out, the final block is not
        var withoutFinalBlock = packed.ToArray();

        FluentActions.Invoking(() => StrictZip.Read(HardenArchiveTests.Archive("records/a.jsonl", content, withoutFinalBlock), PackLimits.Default))
            .Should().Throw<InvalidDataException>().WithMessage("*final block*");
    }

    [Fact]
    public void Versions_take_ASCII_digits_only()
    {
        SemVer.TryParse("1.0.0", out _).Should().BeTrue();
        SemVer.TryParse("1٣.0.0", out _).Should().BeFalse();
        SemVer.TryParse("99999999999999999999.0.0", out _).Should().BeFalse();
    }

    [Fact]
    public void A_policy_with_a_null_prefix_or_a_key_listed_twice_is_malformed()
    {
        var key = _sample.Key;
        string Policy(string prefixes, int copies) => "{\"format\":\"agentmemory.pack.trust/1.1\",\"keys\":[" + string.Join(",", Enumerable.Repeat(
            $"{{\"keyId\":\"{key.KeyId}\",\"publicKey\":{JsonSerializer.Serialize(key.PublicKeyPem)},\"idPrefixes\":{prefixes},\"roles\":[\"publisher\"]}}", copies)) + "]}";

        FluentActions.Invoking(() => TrustPolicy.Parse(Encoding.UTF8.GetBytes(Policy("[null]", 1)))).Should().Throw<InvalidDataException>();
        FluentActions.Invoking(() => TrustPolicy.Parse(Encoding.UTF8.GetBytes(Policy("[\"io.example\"]", 2)))).Should().Throw<InvalidDataException>()
            .WithMessage("*twice*");
    }

    private static JsonNode?[] Values() =>
    [
        null, JsonValue.Create(1), JsonValue.Create(true), JsonValue.Create("x"), new JsonArray(), new JsonObject(),
        JsonNode.Parse("-9223372036854775808"), JsonNode.Parse("1e300"), new JsonArray(JsonValue.Create(5)), JsonValue.Create(-1),
    ];

    [Fact]
    public void Every_JSON_type_in_every_pack_json_field_is_refused_or_read_and_never_crashes()
    {
        foreach (var field in PackManifest.Fields.Append("extra"))
            foreach (var value in Values())
                FluentActions.Invoking(() => VerifyWith(s => WithManifest(s, m => m[field] = value?.DeepClone())))
                    .Should().NotThrow($"pack.json {field} = {value?.ToJsonString() ?? "null"}");
    }

    [Fact]
    public void Every_JSON_type_in_every_files_list_entry_field_is_refused_and_never_crashes()
    {
        var files = _sample.Signed();
        var list = JsonNode.Parse(files[PackPaths.FilesList])!.AsObject();
        foreach (var field in new[] { "path", "length", "sha256", "extra" })
            foreach (var value in Values())
            {
                var copy = list.DeepClone().AsObject();
                copy["files"]![0]!.AsObject()[field] = value?.DeepClone();
                // Canonical where the value allows it, so the entry checks are reached; otherwise as written.
                byte[] bytes;
                try { bytes = Jcs.Canonical(copy); }
                catch (InvalidDataException) { bytes = Encoding.UTF8.GetBytes(copy.ToJsonString()); }
                var changed = new Dictionary<string, byte[]>(files) { [PackPaths.FilesList] = bytes };
                FluentActions.Invoking(() => _sample.Verify(changed).IsVerified.Should().BeFalse()).Should().NotThrow($"files list {field}");
            }
    }

    [Fact]
    public void Every_JSON_type_in_every_statement_field_of_an_extra_envelope_never_crashes_and_never_counts()
    {
        var files = _sample.Signed();
        var envelope = files.Single(f => PackPaths.IsSignature(f.Key));
        var statement = JsonNode.Parse(Convert.FromBase64String(JsonNode.Parse(envelope.Value)!["payload"]!.GetValue<string>()))!.AsObject();
        var paths = new[] { "_type", "predicateType", "subject", "predicate" };
        foreach (var path in paths)
            foreach (var value in Values())
            {
                var changed = statement.DeepClone().AsObject();
                changed[path] = value?.DeepClone();
                var forged = new JsonObject
                {
                    ["payloadType"] = Dsse.PayloadType, ["payload"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(changed.ToJsonString())),
                    ["signatures"] = new JsonArray(new JsonObject { ["keyid"] = _sample.Key.KeyId, ["sig"] = "AAAA" }),
                };
                var withExtra = new Dictionary<string, byte[]>(files) { [PackPaths.SignaturesFolder + "zz.dsse.json"] = Encoding.UTF8.GetBytes(forged.ToJsonString()) };
                FluentActions.Invoking(() => _sample.Verify(withExtra)).Should().NotThrow($"statement {path} = {value?.ToJsonString() ?? "null"}");
                _sample.Verify(withExtra).Pack?.Signatures.Should().ContainSingle("only the real signature counts");
            }
    }
}
