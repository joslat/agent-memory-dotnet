// SPDX-License-Identifier: Apache-2.0
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentMemory.Packs.Format;

namespace AgentMemory.Extensibility.Ampack;

/// <summary>
/// A portable memory bundle (<c>.mem</c>, MacPaw's open format, spec 1.1) imported (feature 15): verified first, whole or not at
/// all; its tombstones applied before anything is read and carried on as this pack's erasures; each record of a kind that holds
/// text becomes a note keyed by its id. Its own signatures are checked and their result recorded, never trusted as ampack's.
/// </summary>
public static partial class PackImports
{
    /// <summary>The kinds that become notes, and how their text is read.</summary>
    private static readonly Dictionary<string, Func<JsonObject, string?>> MemText = new(StringComparer.Ordinal)
    {
        ["episode"] = r => Str(r, "details") is { Length: > 0 } d ? d : Str(r, "summary"),
        ["fact"] = r => Str(r, "text") is { Length: > 0 } t ? t : Join(" ", Str(r, "subject"), Str(r, "predicate"), Str(r, "object")),
        ["entity"] = r => Join("", Str(r, "canonicalName"), Str(r, "type") is { Length: > 0 } t ? $" ({t})" : null, Str(r, "summary") is { Length: > 0 } s ? ": " + s : null),
        ["core"] = r => Str(r, "content"),
        ["preference"] = r => Str(r, "key") is { } k && Str(r, "value") is { } v ? k + ": " + v : null,
        ["procedure"] = r => Join("", Str(r, "name"), Str(r, "triggerPattern") is { Length: > 0 } t ? $" (when: {t})" : null,
            r["steps"] is JsonArray steps && steps.Count > 0 ? ": " + string.Join("; ", steps.Select(x => x is JsonValue v && v.TryGetValue<string>(out var text) ? text : x?.ToJsonString())) : null),
        ["resource"] = r => Join("", Str(r, "title"), Str(r, "summary") is { Length: > 0 } s ? ": " + s : null, Str(r, "uri") is { Length: > 0 } u ? $" <{u}>" : null),
        ["chunk"] = r => Str(r, "text"),
    };

    /// <summary>The fields each kind's note takes; every other field is reported as dropped.</summary>
    private static readonly Dictionary<string, string[]> MemTaken = new(StringComparer.Ordinal)
    {
        ["episode"] = ["id", "details", "summary", "eventTime"], ["fact"] = ["id", "text", "subject", "predicate", "object", "tValidFrom", "tValidTo"],
        ["entity"] = ["id", "canonicalName", "type", "summary"], ["core"] = ["id", "content"], ["preference"] = ["key", "value"],
        ["procedure"] = ["id", "name", "triggerPattern", "steps"], ["resource"] = ["id", "title", "summary", "uri"], ["chunk"] = ["id", "text"],
    };

    /// <summary>
    /// Imports a <c>.mem</c> bundle. <paramref name="files"/> holds the bundle's files by their bundle-relative path. The bundle is
    /// verified first (each file's digest and size against the manifest, <c>CHECKSUMS</c> exactly, <c>bundleDigest</c>) and
    /// refused whole on any failure. Signatures (<c>manifest.sig</c>, a tombstone's <c>signature</c>) are checked with
    /// <paramref name="ed25519"/>, which says whether a signature verifies under the public key its token carries; with
    /// <paramref name="trustedKeys"/> given, the manifest must be signed by one of them, as the spec requires of a verifier
    /// configured with trusted keys. A manifest signature that does not verify refuses the bundle.
    /// </summary>
    /// <exception cref="InvalidDataException">The bundle does not verify, or holds no text to import.</exception>
    public static ImportResult FromMem(IReadOnlyDictionary<string, byte[]> files, Func<byte[], byte[], byte[], bool> ed25519, IReadOnlySet<string>? trustedKeys,
        string id, string version, string owner, string signingKeyPem)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(ed25519);
        if (!files.TryGetValue("manifest.json", out var manifestBytes)) throw new InvalidDataException("A .mem bundle has a manifest.json at its root.");
        var manifest = Parse(manifestBytes, "manifest.json") as JsonObject ?? throw new InvalidDataException("manifest.json is an object.");
        // 1. Verify: every listed file by digest and size, CHECKSUMS exactly, the bundle digest, and no file that is not listed.
        if (manifest["files"] is not JsonArray listed) throw new InvalidDataException("manifest.json lists its files.");
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entry in listed)
        {
            if (entry is not JsonObject f || Str(f, "path") is not { Length: > 0 } path || Str(f, "sha256") is not { } sha || f["bytes"] is not JsonValue size || !size.TryGetValue<long>(out var bytes))
                throw new InvalidDataException("Each manifest file names its path, sha256 and bytes.");
            if (!paths.Add(path)) throw new InvalidDataException($"manifest.json lists '{Shown(path)}' twice.");
            if (!files.TryGetValue(path, out var content)) throw new InvalidDataException($"'{Shown(path)}' is listed but not in the bundle.");
            if (content.LongLength != bytes || Hex(SHA256.HashData(content)) != sha) throw new InvalidDataException($"'{Shown(path)}' does not match its digest or size in manifest.json.");
        }
        var checksums = string.Concat(paths.Select(p => $"{Hex(SHA256.HashData(files[p]))}  {p}\n"));
        if (!files.TryGetValue("CHECKSUMS", out var sums) || Encoding.UTF8.GetString(sums) != checksums)
            throw new InvalidDataException("CHECKSUMS does not list exactly the manifest's files and their digests.");
        if (Str(manifest, "bundleDigest") is { } bundleDigest && bundleDigest != Hex(SHA256.HashData(sums)))
            throw new InvalidDataException("bundleDigest is not the digest of CHECKSUMS.");
        if (files.Keys.FirstOrDefault(p => p is not ("manifest.json" or "CHECKSUMS" or "manifest.sig") && !paths.Contains(p)) is { } stray)
            throw new InvalidDataException($"'{Shown(stray)}' is in the bundle but not in its manifest.");
        var signature = ManifestSignature(files, manifestBytes, ed25519, trustedKeys);

        // 2. Tombstones first: their targets, and what they say was derived from them, are never imported, whatever arrives later.
        var removed = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        int trustedTombstones = 0, untrustedTombstones = 0, unsignedTombstones = 0;
        foreach (var t in Lines(files, paths, "audit/tombstones.jsonl"))
        {
            if (Str(t, "targetID") is not { Length: > 0 } target || Str(t, "op") is not ("delete" or "redact"))
                throw new InvalidDataException("A tombstone names its op (delete or redact) and its targetID.");
            // A tombstone names its target in the key a note of it has (review of 15-17, H2: a preference has no id, so its
            // tombstone names the bare key, and the note is preference/<key>). With no deletedAt, the manifest's createdAt,
            // so the same bundle always gives the same erasure.
            var at = Instant(Str(t, "deletedAt") ?? Str(manifest, "createdAt")) is { } when ? DateTimeOffset.Parse(when, System.Globalization.CultureInfo.InvariantCulture) : DateTimeOffset.UnixEpoch;
            removed[Str(t, "targetKind") == "preference" ? "preference/" + target : target] = at;
            if (t["derived"] is JsonObject derived)
                foreach (var list in new[] { "factIDs", "edgeIDs", "entityIDs", "chunkIDs", "sentenceIDs" })
                    foreach (var derivedId in (derived[list] as JsonArray ?? []).OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var x) ? x : null).OfType<string>())
                        removed[derivedId] = at;
            if (Str(t, "signature") is { } token)
            {
                var unsigned = (JsonObject)t.DeepClone();
                unsigned.Remove("signature");
                if (!Verifies(token, Jcs.Canonical(unsigned), ed25519, out var by)) throw new InvalidDataException($"The tombstone of '{Shown(target)}' carries a signature that does not verify.");
                // Verified only under a trusted key, as the manifest is (spec 1.3; review of 15-17, M3): a token's own key proves
                // only that the tombstone is consistent with itself.
                if (trustedKeys?.Contains(by) == true) trustedTombstones++; else untrustedTombstones++;
            }
            else unsignedTombstones++;
        }

        // 3. Records: each kind that holds text, as notes keyed by id; every other kind and field reported.
        var dropped = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var skipped = new List<string>();
        var entries = new List<(string Key, string Text, IReadOnlyDictionary<string, string>? Life)>();
        foreach (var path in paths.Where(p => p.StartsWith("items/", StringComparison.Ordinal) && p.EndsWith(".jsonl", StringComparison.Ordinal)))
        {
            var kind = path["items/".Length..^".jsonl".Length];
            if (!MemText.TryGetValue(kind, out var text))
            {
                dropped[$"kind '{kind}'"] = Lines(files, paths, path).Count();
                continue;
            }
            foreach (var r in Lines(files, paths, path))
            {
                var key = kind == "preference" ? Str(r, "key") is { } k ? "preference/" + k : null : Str(r, "id");
                if (key is null) { skipped.Add($"a {kind} without an id"); continue; }
                if (removed.ContainsKey(key) || kind == "chunk" && Str(r, "resourceID") is { } parent && removed.ContainsKey(parent))
                { skipped.Add($"'{Shown(key)}': deleted by a tombstone"); continue; }
                if (Str(r, "deletedAt") is not null) { skipped.Add($"'{Shown(key)}': marked deleted (deletedAt)"); continue; }
                // A pack may travel further than the bundle: only what its source marked low (or left unmarked) is imported.
                if (Str(r, "sensitivity") is { } sensitivity && sensitivity != "low") { skipped.Add($"'{Shown(key)}': sensitivity '{Shown(sensitivity)}' (not imported)"); continue; }
                foreach (var field in r.Select(f => f.Key).Where(f => !MemTaken[kind].Contains(f)))
                    dropped[$"{kind}.{field}"] = dropped.GetValueOrDefault($"{kind}.{field}") + 1;
                if (text(r) is not { Length: > 0 } body || string.IsNullOrWhiteSpace(body)) { skipped.Add($"'{Shown(key)}': no text"); continue; }
                var life = new Dictionary<string, string>(StringComparer.Ordinal);
                if (Instant(Str(r, kind == "episode" ? "eventTime" : "tValidFrom")) is { } from) life["validFrom"] = from;
                if (kind == "fact" && Instant(Str(r, "tValidTo")) is { } to) life["validTo"] = to;
                entries.Add((key, body, life.Count == 0 ? null : life));
            }
        }
        foreach (var other in paths.Where(p => !p.StartsWith("items/", StringComparison.Ordinal) && p != "audit/tombstones.jsonl"))
            dropped[$"file '{other}'"] = 1;
        var extra = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mem.tombstones"] = $"{removed.Count} id(s) removed; tombstone signatures: {trustedTombstones} verified under a trusted key, {untrustedTombstones} valid under a key not trusted here, {unsignedTombstones} unsigned",
        };
        // The manifest's own words, only when it states them: a derivation parameter is never empty.
        foreach (var field in new[] { "format", "generator" })
            if (Str(manifest, field) is { Length: > 0 } stated) extra["mem." + field] = stated;
        return Write(manifestBytes, entries, skipped, dropped, "mem", "a .mem bundle", id, version, owner, signingKeyPem, signature, extra,
            [.. removed.Select(r => (r.Key, r.Value))]);
    }

    // The manifest signature's result for the derivation: verified under a trusted key, valid under a key not trusted, or
    // absent. A signature that does not verify refuses the bundle; with trusted keys, so does one missing or by another key.
    private static string ManifestSignature(IReadOnlyDictionary<string, byte[]> files, byte[] manifest, Func<byte[], byte[], byte[], bool> ed25519, IReadOnlySet<string>? trusted)
    {
        if (!files.TryGetValue("manifest.sig", out var sig))
            return trusted is { Count: > 0 } ? throw new InvalidDataException("Trusted keys are given, and the bundle has no manifest.sig.") : "absent";
        if (!Verifies(Encoding.UTF8.GetString(sig).Trim(), manifest, ed25519, out var key)) throw new InvalidDataException("manifest.sig does not verify over manifest.json.");
        if (trusted is { Count: > 0 } && !trusted.Contains(key)) throw new InvalidDataException($"manifest.sig is by a key that is not trusted ({key[..16]}...).");
        return trusted is { Count: > 0 } ? $"verified: ed25519 {key} (trusted)" : $"valid: ed25519 {key} (not trusted here)";
    }

    // A signature token, ed25519:<public key hex>:<signature hex>, checked over the message under the key it carries.
    private static bool Verifies(string token, byte[] message, Func<byte[], byte[], byte[], bool> ed25519, out string key)
    {
        var m = MemToken().Match(token);
        key = m.Success ? m.Groups[1].Value : "";
        return m.Success && ed25519(Convert.FromHexString(m.Groups[1].Value), Convert.FromHexString(m.Groups[2].Value), message);
    }

    [GeneratedRegex("^ed25519:([0-9a-f]{64}):([0-9a-f]{128})$")]
    private static partial Regex MemToken();

    private static IEnumerable<JsonObject> Lines(IReadOnlyDictionary<string, byte[]> files, IReadOnlySet<string> paths, string path)
    {
        if (!paths.Contains(path)) yield break;
        var number = 0;
        foreach (var line in Decoded(files[path]).Split('\n'))
        {
            number++;
            if (line.Length == 0) continue;
            yield return Parse(Encoding.UTF8.GetBytes(line), $"{path} line {number}") as JsonObject ?? throw new InvalidDataException($"{path} line {number} is not an object.");
        }
    }

    private static JsonNode? Parse(byte[] bytes, string where)
    {
        try { return JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException e) { throw new InvalidDataException($"{where} is not JSON: {e.Message}"); }
    }

    private static string? Str(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string? Join(string separator, params string?[] parts) =>
        parts.Where(p => !string.IsNullOrEmpty(p)).ToList() is { Count: > 0 } kept ? string.Join(separator, kept) : null;

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
