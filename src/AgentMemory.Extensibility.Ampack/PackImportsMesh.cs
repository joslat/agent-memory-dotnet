// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace AgentMemory.Extensibility.Ampack;

/// <summary>
/// A Mesh Memory Protocol store imported (feature 15): the folder of entry files a node (sym, MMP 2.0) keeps, one Cognitive
/// Memory Block an entry. Each block becomes a note keyed by its key, its text the seven categories (focus, issue, intent,
/// motivation, commitment, perspective, mood) in that order, its time the block's own creation time. A block the store marks
/// superseded is not imported. A block signed under <c>mmp-sig-v2.0</c> is verified as the mesh verifies it: its key must be
/// the content address of the categories it carries, and its Ed25519 signature must verify over the suite's preimage under its
/// author's key, when that key is given (the mesh pins author keys out of band). A block that fails either refuses the store.
/// </summary>
public static partial class PackImports
{
    private static readonly string[] Cat7 = ["focus", "issue", "intent", "motivation", "commitment", "perspective", "mood"];

    /// <summary>Imports a Mesh Memory Protocol store: <paramref name="files"/> holds its entry files by name.</summary>
    /// <exception cref="InvalidDataException">An entry is not JSON, or the store holds no text to import.</exception>
    public static ImportResult FromMeshMemory(IReadOnlyDictionary<string, byte[]> files, string id, string version, string owner, string signingKeyPem,
        IReadOnlyDictionary<string, byte[]>? authorKeys = null, Func<byte[], byte[], byte[], bool>? ed25519 = null)
    {
        var entries = new List<(string Key, string Text, IReadOnlyDictionary<string, string>? Life)>();
        var skipped = new List<string>();
        var dropped = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var source = new List<byte>();
        int verified = 0, unkeyed = 0, otherSuite = 0, unsigned = 0, flat = 0;
        // The store keeps its entries at its top level; anything in a subfolder is not an entry.
        static bool IsEntry(string n) => !n.Contains('/') && n.EndsWith(".json", StringComparison.Ordinal);
        foreach (var name in files.Keys.Where(IsEntry).Order(StringComparer.Ordinal))
        {
            // The store's own markers are not blocks.
            if (name == "migration-v1.json") { dropped["file '" + name + "'"] = 1; continue; }
            foreach (var part in new[] { Encoding.UTF8.GetBytes(name), SHA256.HashData(files[name]) })
            {
                source.AddRange(BitConverter.GetBytes(part.Length));
                source.AddRange(part);
            }
            if (Parse(files[name], $"'{Shown(name)}'") is not JsonObject entry) throw new InvalidDataException($"'{Shown(name)}' is not an entry object.");
            // An entry holds its block under cmb; a file may also be the block itself, as the mesh sends one.
            // A file with metadata is a block, whatever else it lacks: one without categories is refused below, never read flat.
            var cmb = entry["cmb"] as JsonObject ?? (entry["metadata"] is JsonObject ? entry : null);
            var metadata = cmb?["metadata"] as JsonObject;
            // A two-section block reads its metadata first, as the mesh does: that is the section its signature binds.
            var key = (metadata is null ? null : Str(metadata, "key")) ?? (cmb is null ? null : Str(cmb, "key")) ?? Str(entry, "key");
            if (key is not { Length: > 0 }) { skipped.Add($"'{Shown(name)}': no key"); continue; }
            if (Str(entry, "lifecycle") is "superseded") { skipped.Add($"'{Shown(key)}': superseded"); continue; }
            // A two-section block is read only from its categories, the container its address and signature bind; only a flat,
            // pre-boundary block falls back to its own top level or the entry's content (review of 15-17, M2).
            var twoSection = metadata is not null;
            var categories = twoSection ? cmb!["categories"] as JsonObject : (cmb?["categories"] ?? cmb) as JsonObject;
            if (twoSection && categories is null) { skipped.Add($"'{Shown(key)}': a two-section block with no categories"); continue; }
            var text = Join("\n", [.. Cat7.Select(c => categories?[c] switch
            {
                JsonObject o when Str(o, "text") is { Length: > 0 } t => $"{c}: {t}",
                JsonValue v when v.TryGetValue<string>(out var t) && t.Length > 0 => $"{c}: {t}",
                _ => null,
            })]) ?? (twoSection ? null : Str(entry, "content"));
            if (text is null || string.IsNullOrWhiteSpace(text)) { skipped.Add($"'{Shown(key)}': no text"); continue; }
            foreach (var field in (categories ?? []).Select(f => f.Key).Where(f => !Cat7.Contains(f)))
                dropped[$"category '{field}'"] = dropped.GetValueOrDefault($"category '{field}'") + 1;
            if (!twoSection) flat++;
            else switch (MeshSignature(cmb!, metadata!, authorKeys, ed25519))
                {
                    case null: verified++; break;
                    case "unsigned": unsigned++; break;
                    case "unkeyed": unkeyed++; break;
                    case "suite": otherSuite++; break;
                    case var problem: throw new InvalidDataException($"'{Shown(key)}': {problem}.");
                }
            var created = metadata?["createdTimestamp"] ?? cmb?["createdAt"] ?? entry["originTimestamp"];
            var life = Millis(created) is { } from ? new Dictionary<string, string>(StringComparer.Ordinal) { ["validFrom"] = from } : null;
            foreach (var other in new[] { "lineage", "tags", "admission", "tether", "provenance" })
                if (entry[other] is JsonObject or JsonArray { Count: > 0 }) dropped["entry." + other] = dropped.GetValueOrDefault("entry." + other) + 1;
            entries.Add((key, text, life));
        }
        foreach (var other in files.Keys.Where(n => !IsEntry(n)).Order(StringComparer.Ordinal))
            dropped["file '" + Shown(other) + "'"] = 1;
        var signature = verified + unkeyed + otherSuite == 0 ? "absent"
            : $"mmp-sig-v2.0: {verified} block(s) verified under the author keys given, {unkeyed} signed by an author no key was given for (not checked), "
              + $"{otherSuite} under a suite or address scheme not checked here, {unsigned} unsigned, {flat} flat (pre-boundary, unsigned)";
        return Write([.. source], entries, skipped, dropped, "mesh-memory", "a Mesh Memory Protocol store", id, version, owner, signingKeyPem, signature);
    }

    // Null when the block verifies; "unsigned", "unkeyed" or "suite" when it cannot be checked here; otherwise why it fails.
    // What a block says of its own signature never takes it out of a check (review of 15-17, M1): its content address is
    // checked whenever its key and scheme are the published ones, signed or not, as that needs no key; and when its author's
    // key is given, it must be signed under mmp-sig-v2.0 and verify, or the store is refused.
    private static string? MeshSignature(JsonObject cmb, JsonObject m, IReadOnlyDictionary<string, byte[]>? keys, Func<byte[], byte[], byte[], bool>? ed25519)
    {
        var key = Str(m, "key") ?? "";
        var addressed = Str(m, "addressScheme") == "mmp-cmb-merkle-v2" && System.Text.RegularExpressions.Regex.IsMatch(key, "^cmb-[0-9a-f]{64}$");
        var categories = cmb["categories"] as JsonObject;
        if (addressed && key != "cmb-" + MerkleRoot([.. Cat7.Select(c => CategoryKey(c, CategoryText(categories?[c])))]))
            return "its key is not the address of the categories it carries";
        // Each category's own key, when it states one, is that category's address too: the signature does not cover it.
        if (Cat7.FirstOrDefault(c => (categories?[c] as JsonObject)?["meta"]?["key"] is JsonValue k && (!k.TryGetValue<string>(out var stated) || stated != CategoryKey(c, CategoryText(categories![c])))) is { } wrong)
            return $"its {wrong} category's key is not the address of its text";
        var author = Str(m, "createdByNodeId");
        byte[]? publicKey = null;
        var pinned = author is { Length: > 0 } && keys?.TryGetValue(author, out publicKey) == true && ed25519 is not null;
        if (m["sig"] is null) return pinned ? "its author's key is given, and it is unsigned" : "unsigned";
        if (Str(m, "sigAlg") != "ed25519") return "a signature that is not ed25519";
        if (!addressed || Str(m, "signatureSuite") != "mmp-sig-v2.0")
            return pinned ? "its author's key is given, and it is signed under a suite or address scheme this importer cannot check" : "suite";
        if (!pinned) return "unkeyed";
        var payload = MeshPayload(cmb, m, author!, key);
        return payload is null ? "metadata the suite cannot frame"
            : FromBase64Url(Str(m, "sig")) is not { Length: 64 } sig || !ed25519!(publicKey!, sig, payload) ? "its signature does not verify under its author's key" : null;
    }

    // The mmp-sig-v2.0 preimage: netstring-framed fields, the lineage parents sorted bytewise, the categories' parents and the
    // application section as commitments.
    private static byte[]? MeshPayload(JsonObject cmb, JsonObject m, string author, string key)
    {
        if (Str(m, "createdBy") is not { Length: > 0 } by || Str(m, "room") is not { Length: > 0 } room) return null;
        if (m["createdTimestamp"] is not JsonValue t || !t.TryGetValue<long>(out var at) || at < 0 || at > 9_007_199_254_740_991) return null;
        string to = "";
        if (m["to"] is JsonValue tv) { if (!tv.TryGetValue<string>(out var recipient)) return null; to = recipient; }
        else if (m["to"] is not null) return null;
        var parents = Refs((m["lineage"] as JsonObject)?["parents"]);
        var parts = new List<byte[]> { Encoding.UTF8.GetBytes("mmp-sig-v2.0\n"), Lp("2.0"), Lp("mmp-cmb-merkle-v2"), Lp(key), Lp(author),
            Lp(by.Normalize(NormalizationForm.FormC)), Lp(at.ToString(CultureInfo.InvariantCulture)), Lp(room.Normalize(NormalizationForm.FormC)),
            Lp(to), Lp(parents.Count.ToString(CultureInfo.InvariantCulture)) };
        parts.AddRange(parents.Select(Lp));
        var commitment = new List<byte[]> { Encoding.UTF8.GetBytes("mmp-fp-v1\n") };
        foreach (var c in Cat7)
        {
            var refs = Refs(((cmb["categories"] as JsonObject)?[c] as JsonObject)?["meta"]?["parents"]);
            commitment.Add(Lp(c));
            commitment.Add(Lp(refs.Count.ToString(CultureInfo.InvariantCulture)));
            commitment.AddRange(refs.Select(Lp));
        }
        parts.Add(Lp(Hex(SHA256.HashData([.. commitment.SelectMany(b => b)]))));
        if (Application(m["application"]) is not { } application) return null;
        parts.Add(Lp(application));
        return [.. parts.SelectMany(b => b)];
    }

    // mmp-app-v1: a fixed commitment when there is no application section; otherwise its declared fields, checked against its bytes.
    private static string? Application(JsonNode? node)
    {
        var domain = Encoding.UTF8.GetBytes("mmp-app-v1\n");
        if (node is null) return Hex(SHA256.HashData([.. domain, .. Lp("0")]));
        if (node is not JsonObject a || Str(a, "encoding") != "base64url" || Str(a, "data") is not { } data || data.Contains('=')
            || FromBase64Url(data) is not { } bytes || Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') != data
            || a["byteLength"] is not JsonValue n || !n.TryGetValue<long>(out var length) || length != bytes.Length || bytes.Length > 524_288
            || Str(a, "digest") is not { } digest || digest != "sha256-" + Hex(SHA256.HashData(bytes))
            || Str(a, "mediaType") is not { } media || !System.Text.RegularExpressions.Regex.IsMatch(media, "^[a-z0-9][a-z0-9!#$&^_.+/-]*$"))
            return null;
        var schema = (a["schema"] is JsonValue sv && sv.TryGetValue<string>(out var sc) ? sc : "").Normalize(NormalizationForm.FormC);
        return Hex(SHA256.HashData([.. domain, .. Lp("1"), .. Lp(media), .. Lp(schema), .. Lp("base64url"),
            .. Lp(length.ToString(CultureInfo.InvariantCulture)), .. Lp(digest)]));
    }

    // A category's key: the domain, its name and its text in NFC, each framed. The block's address is the root over the seven.
    private static string CategoryKey(string name, string text) =>
        Hex(SHA256.HashData([.. Encoding.UTF8.GetBytes("mmp-cmb-v1\n"), .. Lp(name), .. Lp(text.Normalize(NormalizationForm.FormC))]));

    private static string CategoryText(JsonNode? node) => node switch
    {
        JsonObject o => Str(o, "text") ?? "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => "",
    };

    // RFC 6962 domain separation, an odd node promoted unchanged, never duplicated.
    private static string MerkleRoot(IReadOnlyList<string> leaves)
    {
        var level = leaves.Select(h => SHA256.HashData([0x00, .. Convert.FromHexString(h)])).ToList();
        if (level.Count == 0) return Hex(SHA256.HashData([]));
        while (level.Count > 1)
            level = [.. Enumerable.Range(0, (level.Count + 1) / 2).Select(i => 2 * i + 1 < level.Count
                ? SHA256.HashData([0x01, .. level[2 * i], .. level[2 * i + 1]]) : level[2 * i])];
        return Hex(level[0]);
    }

    // A list of references, sorted by their UTF-8 bytes: a set, whose order never changes the bytes.
    private static List<string> Refs(JsonNode? node) =>
        [.. (node as JsonArray ?? []).Select(v => v is JsonValue s && s.TryGetValue<string>(out var r) ? r : v?.ToJsonString() ?? "")
            .OrderBy(r => Encoding.UTF8.GetBytes(r), Comparer<byte[]>.Create((x, y) => ((ReadOnlySpan<byte>)x).SequenceCompareTo(y)))];

    private static byte[] Lp(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return [.. Encoding.UTF8.GetBytes(bytes.Length.ToString(CultureInfo.InvariantCulture) + ":"), .. bytes];
    }

    private static byte[]? FromBase64Url(string? text)
    {
        if (text is null || text.Contains('=')) return null;
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        try { return Convert.FromBase64String(padded); } catch (FormatException) { return null; }
    }

    // A time the mesh writes as milliseconds since 1970, or as an ISO 8601 instant.
    private static string? Millis(JsonNode? value) => value switch
    {
        JsonValue v when v.TryGetValue<long>(out var ms) && ms > 0 && ms < 32_503_680_000_000 =>
            DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        JsonValue v when v.TryGetValue<double>(out var d) && d > 0 && d < 32_503_680_000_000 =>
            DateTimeOffset.FromUnixTimeMilliseconds((long)d).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        JsonValue v when v.TryGetValue<string>(out var s) => Instant(s),
        _ => null,
    };
}
