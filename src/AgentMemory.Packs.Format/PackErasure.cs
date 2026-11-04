// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentMemory.Packs.Format;

/// <summary>One record an erasure names: by its key, or by the salted hash of its key, so the notice does not reveal it.</summary>
internal sealed record ErasureRecord(string Type, string? Key, string? KeyHash);

/// <summary>
/// An erasure (feature 08, R-CORE-131 to 134): a publisher's or revoker's signed statement that records of a pack, by type and
/// key or salted key hash, are withdrawn from every version and copy. A host applies it before any import and keeps a
/// tombstone, so no copy brings the record back. Its predicate is the registry's <c>erasurePredicate</c>, read strictly.
/// </summary>
internal sealed partial record PackErasure(string Id, DateTimeOffset IssuedAt, string Reason, string Salt, IReadOnlyList<ErasureRecord> Records)
{
    internal const string PredicateType = "urn:agentmemory:pack:erasure:v1";
    internal const string Suffix = ".erasure.dsse.json";
    internal const int MaximumRecords = 256;
    internal static readonly IReadOnlySet<string> Reasons = PackRegistry.ErasureReasons;
    internal static readonly IReadOnlySet<string> Roles = PackRegistry.ErasureRoles;

    /// <summary>The salted hash of a key: the SHA-256 of the salt, a colon and the key in UTF-8, in lowercase hexadecimal.</summary>
    internal static string HashOf(string salt, string key) => ErasureHash.Of(salt, key);

    /// <summary>Whether it erases the record of this type and key.</summary>
    internal bool Erases(string type, string key) =>
        Records.Any(r => r.Type == type && (r.Key == key || r.KeyHash is { } hash && hash == HashOf(Salt, key)));

    /// <summary>The salted hash this erasure keeps as the tombstone of a record it erases: never the key itself.</summary>
    internal string TombstoneOf(string key) => HashOf(Salt, key);

    /// <summary>The file an erasure travels as in a pack's attestations: its signer's key and issue time.</summary>
    internal static string PathFor(string keyId, DateTimeOffset issuedAt) =>
        PackPaths.AttestationsFolder + PackAttestations.StemFor(keyId, issuedAt) + Suffix;

    /// <summary>The erasures a pack carries, by stem, in stem order.</summary>
    internal static IReadOnlyList<(string Stem, byte[] Envelope)> Carried(IReadOnlyDictionary<string, byte[]> files) =>
        [.. files.Where(f => File().Match(f.Key).Success).Select(f => (Stem: File().Match(f.Key).Groups[1].Value, f.Value)).OrderBy(e => e.Stem, StringComparer.Ordinal)];

    [GeneratedRegex(@"^META/attestations/([a-z0-9][a-z0-9-]{0,63})\.erasure\.dsse\.json$")]
    internal static partial Regex File();

    /// <summary>
    /// Verifies an erasure for the pack <paramref name="packId"/>: a DSSE envelope of the erasure statement, signed by a key the host
    /// trusts as publisher or revoker for the pack's id (R-CORE-132). Returns the erasure and its signer, or the problems.
    /// </summary>
    // With packId null, for the pack the erasure names (a receipt's check).
    internal static (PackErasure? Erasure, string? SignedBy, IReadOnlyList<string> Problems) Verify(ReadOnlySpan<byte> envelopeBytes, string? packId, TrustPolicy policy, DateTimeOffset now,
        string? publisherWebId = null)
    {
        if (envelopeBytes.Length > Dsse.MaximumEnvelopeBytes) return (null, null, [$"The erasure is larger than {Dsse.MaximumEnvelopeBytes / 1024} KiB."]);
        if (PackJson.Check(envelopeBytes) is { } malformed) return (null, null, [$"The erasure {malformed}."]);
        using var document = JsonDocument.Parse(envelopeBytes.ToArray());
        var envelope = document.RootElement;
        if (envelope.ValueKind != JsonValueKind.Object || envelope.EnumerateObject().Any(p => !PackRegistry.Shape("envelope").Contains(p.Name))
            || Str(envelope, "payloadType") != Dsse.PayloadType || Str(envelope, "payload") is not { } payloadText
            || !envelope.TryGetProperty("signatures", out var signatures) || signatures.ValueKind != JsonValueKind.Array
            || signatures.GetArrayLength() is 0 or > Dsse.MaximumSignatures || signatures.EnumerateArray().Any(e => !Dsse.IsEntry(e)))
            return (null, null, ["The erasure is not a DSSE envelope of an in-toto statement with 1 to 16 signatures, each a key id and a signature."]);
        if (Dsse.FromBase64(payloadText) is not { } payload) return (null, null, ["The erasure's payload is not base64."]);
        var (erasure, problem) = Read(payload);
        if (erasure is null) return (null, null, [problem!]);
        if (packId is not null && erasure.Id != packId) return (null, null, [$"The erasure is for {erasure.Id}, not {packId}."]);
        packId = erasure.Id;
        var hash = SHA256.HashData(Dsse.Pae(Dsse.PayloadType, payload));
        var refusals = new List<string>();
        foreach (var entry in signatures.EnumerateArray())
        {
            if (Str(entry, "keyid") is not { } keyId || Str(entry, "sig") is not { } sigText) continue;
            if (policy.Find(keyId) is not { } key) { refusals.Add($"Key {Dsse.Short(keyId)} that signed the erasure is not in the trust policy."); continue; }
            if (Dsse.FromBase64(sigText) is not { } sig || !Dsse.IsStrictDer(sig)) { refusals.Add($"The erasure's signature by {Dsse.Short(keyId)} is not strict DER in base64."); continue; }
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(key.PublicKeyPem);
            if (!ecdsa.VerifyHash(hash, sig, DSASignatureFormat.Rfc3279DerSequence)) { refusals.Add($"The erasure's signature by {Dsse.Short(keyId)} does not verify."); continue; }
            // A publisher or a revoker for the pack's id may erase (owner question 1: both, in their roles).
            var roleRefusals = Roles.Order(StringComparer.Ordinal).Select(role => policy.Refusal(key, role, packId, erasure.IssuedAt, now)).ToList();
            if (publisherWebId is not null && key.WebId != publisherWebId)
            {
                refusals.Add($"The erasure: key {Dsse.Short(keyId)} is not bound to the pack's publisher {publisherWebId}.");
                continue;
            }
            if (roleRefusals.Any(r => r is null)) return (erasure, keyId, []);
            refusals.Add("The erasure: " + roleRefusals.First());
        }
        return (null, null, refusals.Count > 0 ? refusals : ["The erasure carries no signature by a key this host trusts as publisher or revoker for the pack."]);
    }

    /// <summary>Reads the statement strictly: exactly the fields this format defines, each of its type and range.</summary>
    internal static (PackErasure? Erasure, string? Problem) Read(byte[] payload)
    {
        if (PackJson.Check(payload) is { } malformed) return (null, $"The erasure's statement {malformed}.");
        using var document = JsonDocument.Parse(payload);
        var statement = document.RootElement;
        if (statement.ValueKind != JsonValueKind.Object || statement.EnumerateObject().Any(p => !PackRegistry.Shape("statement").Contains(p.Name))
            || Str(statement, "_type") != Dsse.StatementType || Str(statement, "predicateType") != PredicateType)
            return (null, $"The erasure is not an in-toto statement of {PredicateType}.");
        if (!statement.TryGetProperty("predicate", out var p) || p.ValueKind != JsonValueKind.Object
            || p.EnumerateObject().Any(f => !PackRegistry.Shape("erasurePredicate").Contains(f.Name)))
            return (null, "The erasure's predicate holds exactly id, issuedAt, reason, salt and records.");
        if (Str(p, "id") is not { } id || !PackManifest.IsValidId(id)) return (null, "The erasure names no valid pack id.");
        if (Str(p, "issuedAt") is not { } issued || !DateTimeOffset.TryParseExact(issued, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var issuedAt))
            return (null, "The erasure's issuedAt is an instant in UTC to the second.");
        if (Str(p, "reason") is not { } reason || !Reasons.Contains(reason)) return (null, "The erasure's reason is " + string.Join(", ", Reasons.Order(StringComparer.Ordinal)) + ".");
        if (Str(p, "salt") is not { Length: 32 } salt || !salt.All(char.IsAsciiHexDigitLower)) return (null, "The erasure's salt is 32 lowercase hexadecimal characters.");
        if (!p.TryGetProperty("records", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() is 0 or > MaximumRecords)
            return (null, $"The erasure lists 1 to {MaximumRecords} records.");
        var records = new List<ErasureRecord>();
        foreach (var r in list.EnumerateArray())
        {
            // Exactly one of key and keyHash present, and a string (review F08, H2: a null key beside a hash read as absent).
            if (r.ValueKind != JsonValueKind.Object || r.EnumerateObject().Any(f => !PackRegistry.Shape("erasureRecord").Contains(f.Name))
                || Str(r, "type") is not { Length: > 0 } type || PackText.Length(type) > 256
                || r.TryGetProperty("key", out _) == r.TryGetProperty("keyHash", out _)
                || r.TryGetProperty("key", out var k) && k.ValueKind != JsonValueKind.String
                || r.TryGetProperty("keyHash", out var kh) && kh.ValueKind != JsonValueKind.String)
                return (null, "An erased record is its type and exactly one of key and keyHash.");
            if (Str(r, "key") is { } key && PackText.Length(key) is 0 or > 1024) return (null, "An erased record's key is 1 to 1,024 characters.");
            if (Str(r, "keyHash") is { } h && (h.Length != 64 || !h.All(char.IsAsciiHexDigitLower))) return (null, "An erased record's keyHash is 64 lowercase hexadecimal characters.");
            records.Add(new ErasureRecord(type, Str(r, "key"), Str(r, "keyHash")));
        }
        if (records.Select(r => (r.Type, r.Key ?? r.KeyHash)).Distinct().Count() != records.Count) return (null, "The erasure names one record twice.");
        return (new PackErasure(id, issuedAt, reason, salt, records), null);
    }

    /// <summary>
    /// Builds and signs an erasure of <paramref name="records"/> (type and key) of the pack <paramref name="id"/>: each key travels
    /// as its salted hash unless <paramref name="plainKeys"/>, under a fresh salt, so the notice does not reveal what it erases.
    /// </summary>
    internal static byte[] Create(string id, IReadOnlyList<(string Type, string Key)> records, string reason, DateTimeOffset issuedAt, PackSigner signer, bool plainKeys = false)
    {
        var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var statement = new System.Text.Json.Nodes.JsonObject
        {
            ["_type"] = Dsse.StatementType,
            ["subject"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = id, ["digest"] = new System.Text.Json.Nodes.JsonObject { ["sha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant() },
            }),
            ["predicateType"] = PredicateType,
            ["predicate"] = new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = id, ["issuedAt"] = issuedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), ["reason"] = reason, ["salt"] = salt,
                ["records"] = new System.Text.Json.Nodes.JsonArray([.. records.Select(r => (System.Text.Json.Nodes.JsonNode)(plainKeys
                    ? new System.Text.Json.Nodes.JsonObject { ["type"] = r.Type, ["key"] = r.Key }
                    : new System.Text.Json.Nodes.JsonObject { ["type"] = r.Type, ["keyHash"] = HashOf(salt, r.Key) }))]),
            },
        };
        var payload = Jcs.Canonical(statement);
        if (Read(payload).Problem is { } problem) throw new InvalidDataException(problem);
        return Envelope(payload, signer);
    }

    /// <summary>The predicate type of a host's receipt that it applied an erasure (feature 08, R-CORE-135).</summary>
    internal const string ReceiptPredicateType = "urn:agentmemory:pack:erasure-receipt:v1";

    /// <summary>
    /// A host's signed receipt that it applied an erasure (R-CORE-135): naming the erasure statement's identity and when it was
    /// applied, and nothing else, so it reveals no record. Issued on request.
    /// </summary>
    internal static byte[] Receipt(byte[] erasureEnvelope, DateTimeOffset appliedAt, PackSigner host)
    {
        var identity = PackCertificate.IdentityOf(erasureEnvelope) ?? throw new InvalidDataException("The erasure is not an envelope.");
        var statement = new System.Text.Json.Nodes.JsonObject
        {
            ["_type"] = Dsse.StatementType,
            ["subject"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = "erasure", ["digest"] = new System.Text.Json.Nodes.JsonObject { ["sha256"] = identity[7..] },
            }),
            ["predicateType"] = ReceiptPredicateType,
            ["predicate"] = new System.Text.Json.Nodes.JsonObject { ["erasure"] = identity, ["appliedAt"] = appliedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) },
        };
        return Envelope(Jcs.Canonical(statement), host);
    }

    private static byte[] Envelope(byte[] payload, PackSigner signer)
    {
        var signature = signer.Sign(Dsse.Pae(Dsse.PayloadType, payload));
        return Jcs.Canonical(new System.Text.Json.Nodes.JsonObject
        {
            ["payloadType"] = Dsse.PayloadType,
            ["payload"] = Convert.ToBase64String(payload),
            ["signatures"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["keyid"] = signer.KeyId, ["sig"] = Convert.ToBase64String(signature) }),
        });
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
