using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentMemory.Packs.Format;

/// <summary>A signing key pair, PEM encoded, and its id: the lowercase hex SHA-256 of the SubjectPublicKeyInfo (v1.1 H3).</summary>
internal sealed record PackKey(string PrivateKeyPem, string PublicKeyPem, string KeyId)
{
    internal static PackKey Create()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new PackKey(ecdsa.ExportPkcs8PrivateKeyPem(), ecdsa.ExportSubjectPublicKeyInfoPem(), KeyIdOf(ecdsa));
    }

    internal static string KeyIdOf(ECDsa key) => FilesList.Hex(key.ExportSubjectPublicKeyInfo());

    internal static string KeyIdOf(string publicKeyPem)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(publicKeyPem);
        return KeyIdOf(ecdsa);
    }
}

/// <summary>What a valid signature says: who signed which pack, in which role, and when (by its own claim).</summary>
internal sealed record PackSignature(string Role, string KeyId, string Id, string Version, DateTimeOffset SignedAt, string File);

/// <summary>
/// Signatures (v1.1 §3): a DSSE envelope whose payload is an in-toto Statement v1. Its subject is <c>META/files.json</c> with
/// its SHA-256; its predicate type names the role; its predicate carries the pack id, version and signing time. The role and
/// the id are inside what is signed, never in a file name. ECDSA P-256 with SHA-256, DER signatures.
/// </summary>
internal static class Dsse
{
    internal const string PayloadType = "application/vnd.in-toto+json";
    internal const string StatementType = "https://in-toto.io/Statement/v1";
    internal static readonly IReadOnlySet<string> Roles = new HashSet<string>(StringComparer.Ordinal) { "publisher", "curator", "assurer" };

    internal static string PredicateType(string role) => $"urn:agentmemory:pack:{role}:v1";

    /// <summary>DSSE pre-authentication encoding: "DSSEv1 LEN(type) type LEN(body) body".</summary>
    internal static byte[] Pae(string payloadType, ReadOnlySpan<byte> payload)
    {
        var type = Encoding.UTF8.GetBytes(payloadType);
        var head = Encoding.ASCII.GetBytes($"DSSEv1 {type.Length} ");
        var middle = Encoding.ASCII.GetBytes($" {payload.Length.ToString(CultureInfo.InvariantCulture)} ");
        return [.. head, .. type, .. middle, .. payload];
    }

    /// <summary>The envelope file's path for a role and key.</summary>
    internal static string PathFor(string role, string keyId) => $"{PackPaths.SignaturesFolder}{role}-{keyId[..16]}.dsse.json";

    /// <summary>Signs the list's exact bytes as <paramref name="role"/>, and returns the envelope file's bytes.</summary>
    internal static byte[] Sign(byte[] filesList, string id, string version, string role, DateTimeOffset signedAt, string privateKeyPem)
    {
        if (!Roles.Contains(role)) throw new ArgumentException($"Unknown role '{role}'.", nameof(role));
        using var signer = PackSigner.FromPem(privateKeyPem);
        return Sign(filesList, id, version, role, signedAt, signer);
    }

    /// <summary>Signs the list's exact bytes as <paramref name="role"/> with any signer (a key in memory, a keychain, a KMS).</summary>
    internal static byte[] Sign(byte[] filesList, string id, string version, string role, DateTimeOffset signedAt, PackSigner signer)
    {
        if (!Roles.Contains(role)) throw new ArgumentException($"Unknown role '{role}'.", nameof(role));
        var statement = new JsonObject
        {
            ["_type"] = StatementType,
            ["subject"] = new JsonArray(new JsonObject { ["name"] = PackPaths.FilesList, ["digest"] = new JsonObject { ["sha256"] = FilesList.Hex(filesList) } }),
            ["predicateType"] = PredicateType(role),
            ["predicate"] = new JsonObject { ["id"] = id, ["version"] = version, ["role"] = role, ["signedAt"] = signedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) },
        };
        var payload = Jcs.Canonical(statement);
        var signature = signer.Sign(Pae(PayloadType, payload));
        var envelope = new JsonObject
        {
            ["payloadType"] = PayloadType,
            ["payload"] = Convert.ToBase64String(payload),
            ["signatures"] = new JsonArray(new JsonObject { ["keyid"] = signer.KeyId, ["sig"] = Convert.ToBase64String(signature) }),
        };
        return Jcs.Canonical(envelope);
    }

    /// <summary>
    /// Checks one envelope against the list and the trust policy. Returns the valid signatures in it, and the problems with
    /// the ones that are not.
    /// </summary>
    /// <returns>The valid signatures; the problems; and whether the file is not a DSSE envelope at all, which refuses the pack.</returns>
    internal static (List<PackSignature> Valid, List<string> Problems, bool Malformed) Verify(string file, ReadOnlySpan<byte> envelopeBytes,
        string filesListHex, FilesList list, TrustPolicy policy, DateTimeOffset now)
    {
        var valid = new List<PackSignature>();
        var problems = new List<string>();
        // Bounded before anything is parsed: every check below costs at most the envelope's size (fifth review).
        if (envelopeBytes.Length > MaximumEnvelopeBytes) { problems.Add($"{file} is larger than {MaximumEnvelopeBytes / 1024} KiB: a signature envelope is small."); return (valid, problems, true); }
        if (PackJson.Check(envelopeBytes) is { } malformed) { problems.Add($"{file} {malformed}."); return (valid, problems, true); }
        using var document = JsonDocument.Parse(envelopeBytes.ToArray());
        var envelope = document.RootElement;
        if (envelope.ValueKind != JsonValueKind.Object
            || !envelope.TryGetProperty("payloadType", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != PayloadType
            || !envelope.TryGetProperty("payload", out var payloadText) || payloadText.ValueKind != JsonValueKind.String
            || !envelope.TryGetProperty("signatures", out var signatures) || signatures.ValueKind != JsonValueKind.Array)
        { problems.Add($"{file} is not a DSSE envelope of an in-toto statement."); return (valid, problems, true); }

        if (signatures.GetArrayLength() > MaximumSignatures) { problems.Add($"{file} holds more than {MaximumSignatures} signatures."); return (valid, problems, true); }
        // Review 2, L7: closed shapes, so nothing unsigned rides in an envelope. The envelope holds its three fields; every entry
        // a key id (64 lowercase hex) and a signature (strict base64, at most 72 bytes: a P-256 DER signature), and nothing else.
        if (envelope.EnumerateObject().Any(p => p.Name is not ("payloadType" or "payload" or "signatures")))
        { problems.Add($"{file} holds a field a DSSE envelope does not have."); return (valid, problems, true); }
        if (signatures.EnumerateArray().Any(e => !IsEntry(e))) { problems.Add($"{file} has a signature entry that is not a key id and a signature."); return (valid, problems, true); }
        // Review 1, L7: an envelope signs this pack or it does not belong here. One with no signature entries, or whose statement
        // is not a pack role's statement for this list, is unlisted content riding beside the pack: it refuses the pack.
        if (signatures.GetArrayLength() == 0) { problems.Add($"{file} holds no signature."); return (valid, problems, true); }
        if (FromBase64(payloadText.GetString()) is not { } payload) { problems.Add($"{file} has a payload that is not base64."); return (valid, problems, true); }
        var (claim, claimProblem) = ReadStatement(payload, filesListHex, list);
        if (claim is null) { problems.Add($"{file} {claimProblem}"); return (valid, problems, true); }

        // The pre-authentication encoding is hashed once, and each of at most MaximumSignatures entries is checked against it.
        var hash = SHA256.HashData(Pae(PayloadType, payload));
        foreach (var entry in signatures.EnumerateArray())
        {
            var keyId = entry.GetProperty("keyid").GetString()!;
            if (policy.Find(keyId) is not { } key) { problems.Add($"{file}: key {Short(keyId)} is not in the trust policy."); continue; }
            var sig = FromBase64(entry.GetProperty("sig").GetString())!;
            if (!IsStrictDer(sig)) { problems.Add($"{file}: the signature by {Short(keyId)} is not strict DER."); continue; }
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(key.PublicKeyPem);
            if (!ecdsa.VerifyHash(hash, sig, DSASignatureFormat.Rfc3279DerSequence))
            { problems.Add($"{file}: the signature by {Short(keyId)} does not verify."); continue; }
            if (policy.Refusal(key, claim.Role, claim.Id, claim.SignedAt, now) is { } refusal)
            { problems.Add($"{file}: {refusal}"); continue; }
            valid.Add(claim with { KeyId = keyId, File = file });
        }
        return (valid, problems, false);
    }

    // A signature entry's shape (review 2, L7): exactly a key id and a signature, each of the form the format writes.
    private static bool IsEntry(JsonElement entry) =>
        entry.ValueKind == JsonValueKind.Object && entry.EnumerateObject().All(p => p.Name is "keyid" or "sig")
        && entry.TryGetProperty("keyid", out var keyId) && keyId.ValueKind == JsonValueKind.String && keyId.GetString() is { Length: 64 } id
        && id.All(char.IsAsciiHexDigitLower)
        && entry.TryGetProperty("sig", out var sig) && sig.ValueKind == JsonValueKind.String && FromBase64(sig.GetString()) is { Length: > 0 and <= 72 };

    /// <summary>Largest signature envelope: one statement and a few signatures fit many times over.</summary>
    internal const int MaximumEnvelopeBytes = 64 * 1024;
    /// <summary>Most signature entries in one envelope.</summary>
    internal const int MaximumSignatures = 16;
    /// <summary>Most signature envelopes one pack carries.</summary>
    internal const int MaximumEnvelopes = 16;

    private static (PackSignature? Claim, string? Problem) ReadStatement(byte[] payload, string filesListHex, FilesList list)
    {
        if (PackJson.Check(payload) is { } malformed) return (null, $"has a statement that {malformed}.");
        using var document = JsonDocument.Parse(payload);
        var statement = document.RootElement;
        if (Str(statement, "_type") != StatementType) return (null, "does not carry an in-toto Statement v1.");
        // Closed, as the envelope (review 2, L7): the statement, its subject and the predicate hold what the format writes.
        if (!Closed(statement, "_type", "subject", "predicateType", "predicate")) return (null, "holds a statement field the format does not define.");
        if (!statement.TryGetProperty("subject", out var subjects) || subjects.ValueKind != JsonValueKind.Array || subjects.GetArrayLength() != 1)
            return (null, "must name exactly one subject.");
        var subject = subjects[0];
        if (Str(subject, "name") != PackPaths.FilesList || !subject.TryGetProperty("digest", out var digest) || Str(digest, "sha256") != filesListHex)
            return (null, $"signs another {PackPaths.FilesList} than this pack's.");
        if (!Closed(subject, "name", "digest") || !Closed(digest, "sha256")) return (null, "holds a subject field the format does not define.");
        if (!statement.TryGetProperty("predicate", out var predicate) || Str(predicate, "role") is not { } roleName || !Roles.Contains(roleName))
            return (null, "names no known role.");
        if (Str(statement, "predicateType") != PredicateType(roleName))
            return (null, "has a predicate type that does not match its role.");
        if (!Closed(predicate, "role", "id", "version", "signedAt")) return (null, "holds a predicate field the format does not define.");
        if (Str(predicate, "id") != list.Id || Str(predicate, "version") != list.Version)
            return (null, "was made for another pack id or version.");
        if (Str(predicate, "signedAt") is not { } signedAtText
            || !DateTimeOffset.TryParseExact(signedAtText, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var signedAt))
            return (null, "has no signing time in UTC (yyyy-MM-ddTHH:mm:ssZ).");
        return (new PackSignature(roleName, "", list.Id, list.Version, signedAt, ""), null);
    }

    internal static string Short(string keyId) => keyId.Length > 16 ? keyId[..16] : keyId;

    private static readonly byte[] P256Order = Convert.FromHexString("FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551");

    /// <summary>
    /// Strict DER (X.690) for a P-256 signature: a SEQUENCE of exactly two minimal, positive INTEGERs r and s with
    /// 1 &lt;= r, s &lt; n, nothing after. OpenSSL takes only this; .NET alone also took an INTEGER whose sign pad was dropped,
    /// so one signature could verify in one verifier and not the other (sixth review).
    /// </summary>
    internal static bool IsStrictDer(ReadOnlySpan<byte> signature)
    {
        if (signature.Length < 8 || signature[0] != 0x30 || signature[1] >= 0x80 || signature[1] != signature.Length - 2) return false;
        var rest = signature[2..];
        return Integer(ref rest) && Integer(ref rest) && rest.IsEmpty;

        static bool Integer(ref ReadOnlySpan<byte> rest)
        {
            if (rest.Length < 3 || rest[0] != 0x02 || rest[1] is 0 or > 33 || rest[1] > rest.Length - 2) return false;
            var value = rest.Slice(2, rest[1]);
            rest = rest[(2 + value.Length)..];
            if (value[0] >= 0x80) return false;
            if (value[0] == 0 && (value.Length == 1 || value[1] < 0x80)) return false;
            var magnitude = value[0] == 0 ? value[1..] : value;
            return magnitude.Length < 32 || magnitude.Length == 32 && magnitude.SequenceCompareTo(P256Order) < 0;
        }
    }

    /// <summary>
    /// Strict base64 (RFC 4648 §4): the alphabet and its padding only, length a multiple of four. Whitespace, which .NET's decoder
    /// skips and Python's validating decoder refuses, is refused, so two verifiers read one signature the same way.
    /// </summary>
    internal static byte[]? FromBase64(string? text)
    {
        if (text is null || text.Length % 4 != 0) return null;
        var padding = text.EndsWith("==", StringComparison.Ordinal) ? 2 : text.EndsWith('=') ? 1 : 0;
        for (var i = 0; i < text.Length - padding; i++)
            if (!(char.IsAsciiLetterOrDigit(text[i]) || text[i] is '+' or '/')) return null;
        try { return Convert.FromBase64String(text); }
        catch (FormatException) { return null; }
    }

    private static bool Closed(JsonElement element, params string[] fields) =>
        element.ValueKind == JsonValueKind.Object && element.EnumerateObject().All(p => fields.Contains(p.Name, StringComparer.Ordinal));

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
