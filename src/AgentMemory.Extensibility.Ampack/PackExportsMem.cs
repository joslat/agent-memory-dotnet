using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMemory.Packs.Format;

namespace AgentMemory.Extensibility.Ampack;

/// <summary>A <c>.mem</c> bundle's files by their bundle-relative path, and what the export could not carry.</summary>
[Experimental("AMEXT001")]
public sealed record MemExport(IReadOnlyDictionary<string, byte[]> Files, int Episodes, int Tombstones, IReadOnlyList<string> Notes);

/// <summary>
/// Export to <c>.mem</c> (feature 15, R5): a verified knowledge pack as a portable-memory bundle (format 1.1). Each record
/// with text becomes an episode keyed by its record key, its provenance in the episode's metadata (the pack, its digest, the
/// record's type and the end of its validity); each erasure the pack carries becomes a tombstone for every record it names
/// by key. Records and manifest are Canonical JSON (the spec's §1.1), so the same pack
/// exports to the same bytes; the manifest is signed when a signer is given (§1.3).
/// </summary>
[Experimental("AMEXT001")]
public static class PackExports
{
    /// <summary>The format the bundle follows.</summary>
    public const string MemFormat = "1.1.0";

    /// <summary>The specification the bundle follows, as its manifest's <c>specURL</c>.</summary>
    public const string MemSpec = "https://github.com/MacPaw/portable-memory/blob/main/Spec/portable-memory-spec.md";

    /// <summary>
    /// Exports <paramref name="pack"/>. <paramref name="createdAt"/> is the manifest's <c>createdAt</c>, to the second;
    /// <paramref name="sign"/>, when given, returns the Ed25519 public key and signature over the manifest's bytes.
    /// </summary>
    /// <exception cref="InvalidOperationException">The pack holds no record with text.</exception>
    public static MemExport ToMem(LoadedPack pack, DateTimeOffset createdAt, string generator, Func<byte[], (byte[] PublicKey, byte[] Signature)>? sign = null)
    {
        var at = Second(createdAt);
        var notes = new List<string>();
        var episodes = new List<JsonObject>();
        foreach (var record in (pack.Knowledge?.Proposals ?? []).OrderBy(r => r.Key, StringComparer.Ordinal).ThenBy(r => r.Type, StringComparer.Ordinal))
        {
            if (!record.Properties.TryGetValue("text", out var text) || text.ValueKind != JsonValueKind.String || text.GetString() is not { Length: > 0 } body)
            {
                notes.Add($"'{record.Type}/{record.Key}': no text property, not exported");
                continue;
            }
            var when = record.Life?.ValidFromUtc is { } from ? Second(from) : at;
            var metadata = new JsonObject { ["ampack.pack"] = $"{pack.Id}@{pack.Version}", ["ampack.digest"] = pack.Digest, ["ampack.type"] = record.Type };
            if (record.Life?.ValidToUtc is { } to) metadata["ampack.validTo"] = Second(to);
            if (record.ConfidenceMicro is { } micro) metadata["ampack.confidenceMicro"] = micro.ToString(CultureInfo.InvariantCulture);
            episodes.Add(new JsonObject
            {
                ["id"] = record.Key, ["eventTime"] = when, ["mentionTime"] = when, ["ingestionTime"] = at, ["sourceType"] = "ampack",
                ["actors"] = new JsonArray(), ["summary"] = body, ["details"] = body, ["sensitivity"] = "low", ["metadata"] = metadata,
                ["categories"] = new JsonArray(), ["importance"] = 0.5m, ["confidence"] = (record.ConfidenceMicro ?? 1_000_000) / 1_000_000m,
                ["lifecycleState"] = "HOT", ["extractionState"] = "done", ["accessCount"] = 0, ["pinned"] = false, ["vaultRefs"] = new JsonArray(),
            });
        }
        if (episodes.Count == 0) throw new InvalidOperationException("The pack holds no record with text to export.");
        if (episodes.GroupBy(e => e["id"]!.GetValue<string>(), StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new InvalidOperationException($"Two records share the key '{twice.Key}', and a .mem id names one episode.");

        // An erasure names records by key, or only by a salted hash a receiver cannot match: those it cannot carry are said.
        var tombstones = new List<JsonObject>();
        foreach (var erasure in pack.Erasures.OrderBy(e => e.IssuedAt).ThenBy(e => e.Statement, StringComparer.Ordinal))
            foreach (var erased in erasure.Records)
            {
                if (erased.Key is not { } key)
                {
                    notes.Add($"an erasure ({erasure.Statement[..12]}) names a {erased.Type} record by salted hash only, which a .mem tombstone cannot name");
                    continue;
                }
                tombstones.Add(new JsonObject
                {
                    ["id"] = "tomb_" + Hex(SHA256.HashData(Encoding.UTF8.GetBytes(erasure.Statement + "\n" + erased.Type + "\n" + key)))[..16],
                    ["op"] = "delete", ["targetKind"] = "episode", ["targetID"] = key, ["deletedAt"] = Second(erasure.IssuedAt),
                    ["reason"] = erasure.Reason, ["actor"] = "publisher",
                    ["derived"] = new JsonObject
                    {
                        ["sentenceIDs"] = new JsonArray(), ["factIDs"] = new JsonArray(), ["edgeIDs"] = new JsonArray(), ["entityIDs"] = new JsonArray(),
                        ["chunkIDs"] = new JsonArray(), ["embeddingCacheKeys"] = new JsonArray(), ["episodeLinkCount"] = 0,
                    },
                });
            }

        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal) { ["items/episode.jsonl"] = Lines(episodes) };
        if (tombstones.Count > 0) files["audit/tombstones.jsonl"] = Lines(tombstones);
        var checksums = Encoding.UTF8.GetBytes(string.Concat(files.Select(f => $"{Hex(SHA256.HashData(f.Value))}  {f.Key}\n")));
        var times = episodes.Select(e => e["eventTime"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToList();
        var manifest = new JsonObject
        {
            ["format"] = MemFormat, ["specURL"] = MemSpec, ["generator"] = generator, ["conformanceLevel"] = "L0", ["createdAt"] = at,
            ["exportMode"] = "full", ["coverage"] = new JsonObject { ["from"] = times[0], ["to"] = times[^1] }, ["schemaVersion"] = 1,
            ["embeddingModel"] = "", ["embeddingDim"] = 0, ["embeddingsIncluded"] = false,
            ["capabilities"] = tombstones.Count > 0 ? new JsonArray("tombstones") : new JsonArray(),
            ["counts"] = tombstones.Count > 0 ? new JsonObject { ["episode"] = episodes.Count, ["tombstone"] = tombstones.Count } : new JsonObject { ["episode"] = episodes.Count },
            ["files"] = new JsonArray([.. files.Select(f => (JsonNode)new JsonObject { ["path"] = f.Key, ["sha256"] = Hex(SHA256.HashData(f.Value)), ["bytes"] = f.Value.Length })]),
            ["bundleDigest"] = Hex(SHA256.HashData(checksums)),
        };
        var bundle = new Dictionary<string, byte[]>(files, StringComparer.Ordinal) { ["CHECKSUMS"] = checksums, ["manifest.json"] = Canonical(manifest) };
        if (sign is not null)
        {
            var (publicKey, signature) = sign(bundle["manifest.json"]);
            bundle["manifest.sig"] = Encoding.UTF8.GetBytes($"ed25519:{Hex(publicKey)}:{Hex(signature)}\n");
        }
        return new MemExport(bundle, episodes.Count, tombstones.Count, notes);
    }

    // JSONL as the spec frames it: one Canonical JSON object a line, each line ending in a line feed.
    private static byte[] Lines(IEnumerable<JsonObject> records)
    {
        var buffer = new MemoryStream();
        foreach (var record in records)
        {
            buffer.Write(Canonical(record));
            buffer.WriteByte((byte)'\n');
        }
        return buffer.ToArray();
    }

    // Canonical JSON as the spec's §1.1 sets it: keys sorted (by code point; these keys are ASCII), no whitespace, strings
    // escaped as RFC 8785 escapes them, integers bare and the decimals this writer uses in fixed point, so never an exponent.
    private static byte[] Canonical(JsonNode node)
    {
        var text = new StringBuilder();
        void Write(JsonNode? n)
        {
            switch (n)
            {
                case JsonObject o:
                    text.Append('{');
                    var first = true;
                    foreach (var (k, v) in o.OrderBy(p => p.Key, StringComparer.Ordinal))
                    {
                        if (!first) text.Append(',');
                        first = false;
                        text.Append(Encoding.UTF8.GetString(Jcs.Canonical(JsonValue.Create(k)))).Append(':');
                        Write(v);
                    }
                    text.Append('}');
                    break;
                case JsonArray a:
                    text.Append('[');
                    for (var i = 0; i < a.Count; i++) { if (i > 0) text.Append(','); Write(a[i]); }
                    text.Append(']');
                    break;
                case JsonValue v when v.TryGetValue<string>(out var s): text.Append(Encoding.UTF8.GetString(Jcs.Canonical(JsonValue.Create(s)))); break;
                case JsonValue v when v.TryGetValue<bool>(out var b): text.Append(b ? "true" : "false"); break;
                case JsonValue v when v.TryGetValue<decimal>(out var d) && d != decimal.Truncate(d):
                    text.Append(d.ToString("0.############################", CultureInfo.InvariantCulture)); break;
                case JsonValue v when v.TryGetValue<int>(out var i): text.Append(i.ToString(CultureInfo.InvariantCulture)); break;
                case JsonValue v when v.TryGetValue<long>(out var l): text.Append(l.ToString(CultureInfo.InvariantCulture)); break;
                case JsonValue v when v.TryGetValue<decimal>(out var whole): text.Append(whole.ToString("0", CultureInfo.InvariantCulture)); break;
                default: throw new InvalidOperationException("A .mem record holds text, booleans, integers and decimals only.");
            }
        }
        Write(node);
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    private static string Second(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
