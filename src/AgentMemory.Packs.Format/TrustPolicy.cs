using System.Globalization;
using System.Text.Json;

namespace AgentMemory.Packs.Format;

/// <summary>One trusted key, scoped (v1.1 H3): the pack ids it may sign for, the roles it may sign in, and when.</summary>
internal sealed record TrustedKey(string KeyId, string PublicKeyPem, IReadOnlyList<string> IdPrefixes, IReadOnlyList<string> Roles,
    DateTimeOffset? NotBefore, DateTimeOffset? NotAfter);

/// <summary>A revoked key. With <see cref="CompromisedAt"/>, signatures from before that moment may still count once a timestamp proves them.</summary>
internal sealed record RevokedKey(string KeyId, DateTimeOffset? CompromisedAt);

/// <summary>
/// The host's trust policy: scoped keys, revocations, and the roles a pack must be signed in. Set by the host's operator,
/// never by a pack, a request, an MCP tool or a model. There is no "allow unsigned" setting.
/// </summary>
internal sealed record TrustPolicy
{
    internal const string Format = "agentmemory.pack.trust/1.1";

    /// <summary>The roles a trusted key may hold: the three that sign packs, and <c>revoker</c>, which signs revocation documents only.</summary>
    internal static readonly IReadOnlySet<string> KeyRoles = new HashSet<string>(Dsse.Roles, StringComparer.Ordinal) { Revocations.Role };

    internal IReadOnlyList<TrustedKey> Keys { get; init; } = [];
    internal IReadOnlyList<string> RevokedDigests { get; init; } = [];
    internal IReadOnlyList<RevokedKey> RevokedKeys { get; init; } = [];
    /// <summary>When the revocation information goes stale; after it, nothing verifies until the host refreshes it.</summary>
    internal DateTimeOffset? RevocationsExpire { get; init; }
    internal IReadOnlyList<string> RequiredRoles { get; init; } = ["publisher"];

    internal TrustedKey? Find(string keyId) => Keys.FirstOrDefault(k => k.KeyId == keyId);

    /// <summary>Why a signature by <paramref name="key"/> does not count, or null when it does.</summary>
    internal string? Refusal(TrustedKey key, string role, string id, DateTimeOffset signedAt, DateTimeOffset now)
    {
        var shortId = Dsse.Short(key.KeyId);
        if (!key.Roles.Contains(role)) return $"key {shortId} may not sign as {role}.";
        if (!key.IdPrefixes.Any(p => PackManifest.Covers(p, id))) return $"key {shortId} may not sign for '{id}'.";
        if (key.NotBefore is { } from && (signedAt < from || now < from)) return $"key {shortId} was not yet valid at the signing time.";
        if (key.NotAfter is { } until && signedAt > until) return $"key {shortId} had expired at the signing time.";
        // PACK-harden (review 4.7): the signing time is the signer's own claim and can be back-dated, so a key's expiry is
        // checked against now; only a verified timestamp token could prove a signature older than the expiry.
        if (key.NotAfter is { } expired && now > expired)
            return $"key {shortId} expired at {expired:u}; without a verified timestamp token its signatures no longer count.";
        if (signedAt > now.AddMinutes(5)) return $"the signature by {shortId} claims a time in the future.";
        if (RevokedKeys.FirstOrDefault(r => r.KeyId == key.KeyId) is { } revoked)
            // A timestamp authority's token is the only proof a signature predates a compromise; until the token is checked, refuse.
            return revoked.CompromisedAt is { } at
                ? $"key {shortId} was compromised at {at:u}; a signature counts only with a timestamp token proving it is older."
                : $"key {shortId} is revoked.";
        return null;
    }

    /// <summary>
    /// Reads a policy file, checking that every key id is the SHA-256 of its key. A policy is host configuration that people
    /// edit, so a byte order mark and CRLF line ends are accepted here; everything else in the JSON profile holds.
    /// </summary>
    internal static TrustPolicy Parse(ReadOnlySpan<byte> raw)
    {
        try { return ParseChecked(raw); }
        // A key that is not a PEM public key is malformed too (PACK-harden: found by fuzzing the policy reader).
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException or JsonException or ArgumentException
            or System.Security.Cryptography.CryptographicException or NotSupportedException)
        {
            throw new InvalidDataException($"The trust policy is malformed: {e.Message}", e);
        }
    }

    private static TrustPolicy ParseChecked(ReadOnlySpan<byte> raw)
    {
        var text = System.Text.Encoding.UTF8.GetString(raw).TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal);
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        if (PackJson.Check(bytes) is { } problem) throw new InvalidDataException($"The trust policy {problem}.");
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (!root.TryGetProperty("format", out var format) || format.GetString() != Format)
            throw new InvalidDataException($"The trust policy must name format '{Format}'.");
        var keys = new List<TrustedKey>();
        if (root.TryGetProperty("keys", out var keyArray))
            foreach (var k in keyArray.EnumerateArray())
            {
                var pem = k.GetProperty("publicKey").GetString() ?? throw new FormatException("A key's publicKey is a string.");
                var keyId = k.GetProperty("keyId").GetString() ?? throw new FormatException("A key's keyId is a string.");
                if (keys.Any(known => known.KeyId == keyId)) throw new InvalidDataException($"Key {Dsse.Short(keyId)} is listed twice.");
                if (PackKey.KeyIdOf(pem) != keyId) throw new InvalidDataException($"Key {Dsse.Short(keyId)}: the id is not the SHA-256 of its public key.");
                var prefixes = Strings(k, "idPrefixes");
                if (prefixes.Count == 0) throw new InvalidDataException($"Key {Dsse.Short(keyId)} names no pack id prefixes: a key that may sign anything is refused.");
                var roles = Strings(k, "roles");
                if (roles.Count == 0 || roles.Any(r => !KeyRoles.Contains(r))) throw new InvalidDataException($"Key {Dsse.Short(keyId)} needs known roles.");
                keys.Add(new TrustedKey(keyId, pem, prefixes, roles, Time(k, "notBefore"), Time(k, "notAfter")));
            }
        var revokedDigests = new List<string>();
        var revokedKeys = new List<RevokedKey>();
        DateTimeOffset? expires = null;
        if (root.TryGetProperty("revoked", out var revoked))
        {
            revokedDigests.AddRange(Strings(revoked, "digests"));
            if (revoked.TryGetProperty("keys", out var rk))
                foreach (var r in rk.EnumerateArray()) revokedKeys.Add(new RevokedKey(r.GetProperty("keyId").GetString()!, Time(r, "compromisedAt")));
            expires = Time(revoked, "expires");
        }
        var required = root.TryGetProperty("requireRoles", out _) ? Strings(root, "requireRoles") : ["publisher"];
        if (required.Count == 0 || required.Any(r => !Dsse.Roles.Contains(r))) throw new InvalidDataException("requireRoles must name known roles.");
        return new TrustPolicy { Keys = keys, RevokedDigests = revokedDigests, RevokedKeys = revokedKeys, RevocationsExpire = expires, RequiredRoles = required };
    }

    // A list of strings: absent is empty; anything but an array of strings makes the policy malformed.
    private static IReadOnlyList<string> Strings(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return [];
        if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String))
            throw new FormatException($"'{name}' is a list of strings.");
        return [.. value.EnumerateArray().Select(v => v.GetString()!)];
    }

    // A time in the policy: absent, or an RFC 3339 date-time (PackTime); anything else makes the policy malformed.
    private static DateTimeOffset? Time(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.String && PackTime.TryParse(value.GetString(), out var time)
            ? time : throw new FormatException($"'{name}' is not an RFC 3339 date-time.");
    }
}
