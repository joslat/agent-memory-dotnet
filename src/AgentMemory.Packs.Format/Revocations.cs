using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentMemory.Packs.Format;

/// <summary>
/// A signed revocation document (pack format v1.2 §8; PACK-harden, review 1.10): pack digests and keys that no longer count,
/// with when it was issued and when it goes stale. A DSSE envelope signed by a key the host's trust policy names in the role
/// <c>revoker</c>. A host applies it to its policy: the revocations are added, never removed, and its expiry becomes the
/// policy's, so after it nothing verifies until the host fetches a fresh document.
/// </summary>
internal static partial class Revocations
{
    internal const string Format = "agentmemory.pack.revocations/1.2";
    internal const string PayloadType = "application/vnd.agentmemory.revocations+json";
    internal const string Role = "revoker";

    /// <summary>Writes and signs a document.</summary>
    internal static byte[] Sign(IReadOnlyList<string> digests, IReadOnlyList<RevokedKey> keys, DateTimeOffset issuedAt, DateTimeOffset expires,
        string privateKeyPem)
    {
        using var signer = PackSigner.FromPem(privateKeyPem);
        return Sign(digests, keys, issuedAt, expires, signer);
    }

    /// <summary>Writes and signs a document with any signer.</summary>
    internal static byte[] Sign(IReadOnlyList<string> digests, IReadOnlyList<RevokedKey> keys, DateTimeOffset issuedAt, DateTimeOffset expires,
        PackSigner signer)
    {
        if (expires <= issuedAt) throw new ArgumentException("A revocation document expires after it is issued.", nameof(expires));
        if (digests.FirstOrDefault(d => !DigestPattern().IsMatch(d)) is { } bad) throw new ArgumentException($"'{bad}' is not a pack digest.", nameof(digests));
        if (keys.FirstOrDefault(k => !KeyIdPattern().IsMatch(k.KeyId)) is { } badKey) throw new ArgumentException($"'{badKey.KeyId}' is not a key id.", nameof(keys));
        var document = new JsonObject
        {
            ["format"] = Format,
            ["issuedAt"] = Time(issuedAt),
            ["expires"] = Time(expires),
            ["digests"] = new JsonArray([.. digests.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(d => (JsonNode)d)]),
            ["keys"] = new JsonArray([.. keys.DistinctBy(k => k.KeyId).OrderBy(k => k.KeyId, StringComparer.Ordinal).Select(k => (JsonNode)(k.CompromisedAt is { } at
                ? new JsonObject { ["keyId"] = k.KeyId, ["compromisedAt"] = Time(at) } : new JsonObject { ["keyId"] = k.KeyId }))]),
        };
        var payload = Jcs.Canonical(document);
        var signature = signer.Sign(Dsse.Pae(PayloadType, payload));
        return Jcs.Canonical(new JsonObject
        {
            ["payloadType"] = PayloadType,
            ["payload"] = Convert.ToBase64String(payload),
            ["signatures"] = new JsonArray(new JsonObject { ["keyid"] = signer.KeyId, ["sig"] = Convert.ToBase64String(signature) }),
        });
    }

    /// <summary>Largest revocation document read.</summary>
    internal const int MaximumDocumentBytes = 4 << 20;

    /// <summary>The archive limit for reading a revocation document from a file.</summary>
    internal static PackLimits DocumentLimits { get; } = new() { MaxArchiveBytes = MaximumDocumentBytes };

    /// <summary>
    /// <paramref name="policy"/> with the document applied: verified by a revoker key it trusts and that is valid now, issued no
    /// later than now (five minutes' grace), its revocations added and its expiry the policy's when earlier. With a
    /// <paramref name="ledger"/>, what it revokes is kept by the host for good, so an older list, or none, later removes nothing.
    /// </summary>
    /// <exception cref="InvalidDataException">The document is malformed, signed by no trusted revoker, or issued in the future.</exception>
    internal static TrustPolicy Apply(TrustPolicy policy, ReadOnlySpan<byte> envelopeBytes, DateTimeOffset now, HostLedger? ledger = null)
    {
        if (envelopeBytes.Length > MaximumDocumentBytes) throw Bad($"is larger than {MaximumDocumentBytes >> 20} MiB");
        ArgumentNullException.ThrowIfNull(policy);
        if (PackJson.Check(envelopeBytes) is { } problem) throw Bad($"is not a JSON envelope: it {problem}");
        byte[] payload;
        JsonElement signatures;
        using (var envelope = JsonDocument.Parse(envelopeBytes.ToArray()))
        {
            var root = envelope.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("payloadType", out var type) || type.ValueKind != JsonValueKind.String
                || type.GetString() != PayloadType || !root.TryGetProperty("payload", out var text) || text.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("signatures", out var sigs) || sigs.ValueKind != JsonValueKind.Array)
                throw Bad("is not a DSSE envelope of a revocation document");
            payload = Dsse.FromBase64(text.GetString()) ?? throw Bad("has a payload that is not base64");
            signatures = sigs.Clone();
        }
        if (signatures.GetArrayLength() > Dsse.MaximumSignatures) throw Bad($"holds more than {Dsse.MaximumSignatures} signatures");
        // Hashed once, and at most MaximumSignatures entries tried (fifth review).
        var hash = SHA256.HashData(Dsse.Pae(PayloadType, payload));
        if (!signatures.EnumerateArray().Any(s => Verifies(policy, s, hash, now)))
            throw Bad("is not signed by a key the trust policy names as revoker and that is valid now");

        if (PackJson.Check(payload) is { } payloadProblem) throw Bad($"has a payload that {payloadProblem}");
        using var document = JsonDocument.Parse(payload);
        var d = document.RootElement;
        if (d.ValueKind != JsonValueKind.Object || d.EnumerateObject().Any(p => p.Name is not ("format" or "issuedAt" or "expires" or "digests" or "keys"))
            || !d.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != Format)
            throw Bad($"is not a '{Format}' document: format, issuedAt, expires, digests and keys, nothing else");
        if (!d.TryGetProperty("issuedAt", out var issuedText) || !PackTime.TryParse(issuedText.ValueKind == JsonValueKind.String ? issuedText.GetString() : null, out var issuedAt)
            || !d.TryGetProperty("expires", out var expiresText) || !PackTime.TryParse(expiresText.ValueKind == JsonValueKind.String ? expiresText.GetString() : null, out var expires)
            || expires <= issuedAt)
            throw Bad("needs issuedAt and expires, RFC 3339, expiring after it was issued");
        if (issuedAt > now.AddMinutes(5)) throw Bad($"claims to be issued at {issuedAt:u}, in the future");

        var digests = new List<string>();
        if (!d.TryGetProperty("digests", out var digestArray) || digestArray.ValueKind != JsonValueKind.Array) throw Bad("needs a digests list");
        foreach (var digest in digestArray.EnumerateArray())
        {
            if (digest.ValueKind != JsonValueKind.String || !DigestPattern().IsMatch(digest.GetString()!)) throw Bad("has a digest that is not sha256: and 64 hex digits");
            digests.Add(digest.GetString()!);
        }
        var keys = new List<RevokedKey>();
        if (!d.TryGetProperty("keys", out var keyArray) || keyArray.ValueKind != JsonValueKind.Array) throw Bad("needs a keys list");
        foreach (var key in keyArray.EnumerateArray())
        {
            if (key.ValueKind != JsonValueKind.Object || key.EnumerateObject().Any(p => p.Name is not ("keyId" or "compromisedAt"))
                || !key.TryGetProperty("keyId", out var keyId) || keyId.ValueKind != JsonValueKind.String || !KeyIdPattern().IsMatch(keyId.GetString()!))
                throw Bad("has a key that is not {keyId, compromisedAt?} with a 64-hex key id");
            DateTimeOffset? compromisedAt = null;
            if (key.TryGetProperty("compromisedAt", out var at))
            {
                if (!PackTime.TryParse(at.ValueKind == JsonValueKind.String ? at.GetString() : null, out var time)) throw Bad("has a compromisedAt that is not RFC 3339");
                compromisedAt = time;
            }
            keys.Add(new RevokedKey(keyId.GetString()!, compromisedAt));
        }

        // Added, never removed: a key revoked outright stays revoked; of two compromise times the earlier holds.
        var merged = policy.RevokedKeys.ToDictionary(k => k.KeyId, StringComparer.Ordinal);
        foreach (var key in keys)
            merged[key.KeyId] = merged.TryGetValue(key.KeyId, out var known) && (known.CompromisedAt is null || key.CompromisedAt is { } later && later > known.CompromisedAt)
                ? known : key;
        // What it revokes is kept by the host for good: a later run without this list, or with an older one, still holds it.
        ledger?.Record(digests, keys);
        return policy with
        {
            RevokedDigests = [.. policy.RevokedDigests.Union(digests, StringComparer.Ordinal)],
            RevokedKeys = [.. merged.Values.OrderBy(k => k.KeyId, StringComparer.Ordinal)],
            RevocationsExpire = policy.RevocationsExpire is { } current && current < expires ? current : expires,
        };
    }

    // One signature entry by a key the policy trusts as revoker, valid now and not itself revoked, that verifies the hash.
    private static bool Verifies(TrustPolicy policy, JsonElement entry, byte[] hash, DateTimeOffset now)
    {
        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("keyid", out var keyId) || keyId.ValueKind != JsonValueKind.String
            || !entry.TryGetProperty("sig", out var sig) || sig.ValueKind != JsonValueKind.String)
            return false;
        if (policy.Find(keyId.GetString()!) is not { } key || !key.Roles.Contains(Role)) return false;
        if (key.NotBefore is { } from && now < from || key.NotAfter is { } until && now > until) return false;
        if (policy.RevokedKeys.Any(r => r.KeyId == key.KeyId)) return false;
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(key.PublicKeyPem);
            return Dsse.FromBase64(sig.GetString()) is { } signature && ecdsa.VerifyHash(hash, signature, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (FormatException) { return false; }
    }

    private static string Time(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static InvalidDataException Bad(string what) => new($"The revocation document {what}.");

    [GeneratedRegex("\\Asha256:[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex DigestPattern();

    [GeneratedRegex("\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyIdPattern();
}
