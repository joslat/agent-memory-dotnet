using System.Globalization;
using System.Text.Json;

namespace AgentMemory.Packs.Format;

/// <summary>
/// One trusted key, scoped (v1.1 H3): the pack ids it may sign for, the roles it may sign in, and when; and, for federated
/// packs, the publisher WebID the operator bound it to after reading that WebID's profile (federated packs §4).
/// </summary>
internal sealed record TrustedKey(string KeyId, string PublicKeyPem, IReadOnlyList<string> IdPrefixes, IReadOnlyList<string> Roles,
    DateTimeOffset? NotBefore, DateTimeOffset? NotAfter, string? WebId = null);

/// <summary>A revoked key. With <see cref="CompromisedAt"/>, signatures from before that moment may still count once a timestamp proves them.</summary>
internal sealed record RevokedKey(string KeyId, DateTimeOffset? CompromisedAt);

/// <summary>
/// The host's trust policy: scoped keys, revocations, and the roles a pack must be signed in. Set by the host's operator,
/// never by a pack, a request, an MCP tool or a model. There is no "allow unsigned" setting.
/// </summary>
internal sealed partial record TrustPolicy
{
    internal const string Format = "agentmemory.pack.trust/1.1";

    /// <summary>The roles a trusted key may hold: the three that sign packs, and <c>revoker</c>, which signs revocation documents only.</summary>
    internal static readonly IReadOnlySet<string> KeyRoles = new HashSet<string>(Dsse.Roles, StringComparer.Ordinal) { Revocations.Role, PackCertificate.Role };

    internal IReadOnlyList<TrustedKey> Keys { get; init; } = [];
    internal IReadOnlyList<string> RevokedDigests { get; init; } = [];
    internal IReadOnlyList<RevokedKey> RevokedKeys { get; init; } = [];
    /// <summary>When the revocation information goes stale; after it, nothing verifies until the host refreshes it.</summary>
    internal DateTimeOffset? RevocationsExpire { get; init; }
    /// <summary>The policy file's own <c>revoked.expires</c>, which caps every list's freshness.</summary>
    internal DateTimeOffset? StaticRevocationsExpire { get; init; }
    /// <summary>When the newest revocation list behind <see cref="RevocationsExpire"/> was issued; null when no list was applied.</summary>
    internal DateTimeOffset? RevocationsIssued { get; init; }
    /// <summary>The ledger whose kept revocations this policy already holds, and at which of its versions (so a set's members do not apply it again).</summary>
    internal HostLedger? KeptFrom { get; init; }
    internal int KeptVersion { get; init; }
    internal IReadOnlyList<string> RequiredRoles { get; init; } = ["publisher"];

    /// <summary>The clock difference allowed between a signer and a reader.</summary>
    internal static readonly TimeSpan ClockAllowance = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether a claimed time is past the allowance ahead of now: the one rule the readers apply to signing and issue times and
    /// the writer applies before it signs (round 10, A-M2: a future SOURCE_DATE_EPOCH wrote a pack every reader refuses).
    /// </summary>
    internal static bool InTheFuture(DateTimeOffset claimed, DateTimeOffset now) => claimed > now + ClockAllowance;
    /// <summary>The WebIDs this host acts as (<c>host.webIds</c>): a federated pack loads only if its audience names one, or is public.</summary>
    internal IReadOnlyList<string> HostWebIds { get; init; } = [];

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
        if (InTheFuture(signedAt, now)) return $"the signature by {shortId} claims a time in the future.";
        if (RevokedKeys.FirstOrDefault(r => r.KeyId == key.KeyId) is { } revoked)
            // A timestamp authority's token is the only proof a signature predates a compromise; until the token is checked, refuse.
            return revoked.CompromisedAt is { } at
                ? $"key {shortId} was compromised at {at:u}; a signature counts only with a timestamp token proving it is older."
                : $"key {shortId} is revoked.";
        return null;
    }

    /// <summary>The largest trust policy read, here and by every caller (pack format v1.2, appendix; review 6, M2).</summary>
    internal const int MaximumBytes = 4 << 20;

    /// <summary>
    /// Reads a policy file, checking that every key id is the SHA-256 of its key. A policy is host configuration that people
    /// edit, so a byte order mark and CRLF line ends are accepted here; everything else in the JSON profile holds.
    /// </summary>
    internal static TrustPolicy Parse(ReadOnlySpan<byte> raw)
    {
        if (raw.Length > MaximumBytes) throw new InvalidDataException($"The trust policy is larger than {MaximumBytes} bytes.");
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
        // Strict UTF-8, and one BOM tolerated, as the Python verifier reads it (third review).
        var text = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(raw);
        if (text.StartsWith('\uFEFF')) text = text[1..];
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        if (PackJson.Check(bytes) is { } problem) throw new InvalidDataException($"The trust policy {problem}.");
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        // Review 1, H3: every level is closed, as every other document of the format is. A misspelt field (requiredRoles,
        // notafter, revoked.digest) would otherwise be ignored and the policy would silently trust more than its author meant.
        Closed(root, "The trust policy", "format", "keys", "revoked", "requireRoles", "host");
        if (!root.TryGetProperty("format", out var format) || format.GetString() != Format)
            throw new InvalidDataException($"The trust policy must name format '{Format}'.");
        var keys = new List<TrustedKey>();
        if (root.TryGetProperty("keys", out var keyArray))
            foreach (var k in keyArray.EnumerateArray())
            {
                Closed(k, "A key in the trust policy", "keyId", "publicKey", "idPrefixes", "roles", "notBefore", "notAfter", "webId");
                var pem = k.GetProperty("publicKey").GetString() ?? throw new FormatException("A key's publicKey is a string.");
                var keyId = k.GetProperty("keyId").GetString() ?? throw new FormatException("A key's keyId is a string.");
                if (keys.Any(known => known.KeyId == keyId)) throw new InvalidDataException($"Key {Dsse.Short(keyId)} is listed twice.");
                // One PUBLIC KEY block and nothing else, as both verifiers read it (review 2): the PEM reader also takes a private
                // key, which then sits in a policy that gets shared, and the two readers took several blocks differently.
                if (!IsPublicKeyPem(pem)) throw new InvalidDataException($"Key {Dsse.Short(keyId)}: publicKey must be one PEM 'PUBLIC KEY' block and nothing else.");
                if (!IsCanonicalP256(pem))
                    throw new InvalidDataException($"Key {Dsse.Short(keyId)}: publicKey must be a P-256 key in its one form (an uncompressed point, canonical base64).");
                if (PackKey.KeyIdOf(pem) != keyId) throw new InvalidDataException($"Key {Dsse.Short(keyId)}: the id is not the SHA-256 of its public key.");
                // P-256 only, as the format signs and as the Python verifier reads a policy (fourth review: .NET took any curve).
                using (var curve = System.Security.Cryptography.ECDsa.Create())
                {
                    curve.ImportFromPem(pem);
                    if (curve.ExportParameters(false).Curve.Oid?.Value != "1.2.840.10045.3.1.7") throw new InvalidDataException($"Key {Dsse.Short(keyId)} is not a P-256 key.");
                }
                var prefixes = Strings(k, "idPrefixes");
                if (prefixes.Count == 0) throw new InvalidDataException($"Key {Dsse.Short(keyId)} names no pack id prefixes: a key that may sign anything is refused.");
                if (prefixes.FirstOrDefault(p => !PackManifest.IsValidPrefix(p)) is { } badPrefix)
                    throw new InvalidDataException($"Key {Dsse.Short(keyId)} names '{badPrefix}', which is not a pack id prefix (lowercase segments such as io.example).");
                var roles = Strings(k, "roles");
                if (roles.Count == 0 || roles.Any(r => !KeyRoles.Contains(r))) throw new InvalidDataException($"Key {Dsse.Short(keyId)} needs known roles.");
                // A revoker signs revocation lists only: a key that also signs packs would let a revocation of it, read as a
                // revocation of a revoker, decide what its own packs are worth (eighth review).
                if (roles.Contains(Revocations.Role) && roles.Count > 1)
                    throw new InvalidDataException($"Key {Dsse.Short(keyId)} is a revoker and also signs packs; a revoker key holds that role alone.");
                // An evaluator signs certificates only (v1.2 §16): the key that signs a pack never certifies it as well.
                if (roles.Contains(PackCertificate.Role) && roles.Count > 1)
                    throw new InvalidDataException($"Key {Dsse.Short(keyId)} is an evaluator and also signs packs; an evaluator key holds that role alone.");
                string? webId = null;
                if (k.TryGetProperty("webId", out var webIdValue))
                {
                    webId = webIdValue.ValueKind == JsonValueKind.String ? webIdValue.GetString() : throw new FormatException("A key's webId is a string.");
                    if (WebId.Problem(webId, $"Key {Dsse.Short(keyId)}'s webId") is { } webIdProblem) throw new InvalidDataException(webIdProblem);
                    // A revoker signs revocation lists only, and revocation authority stays where v1.2 settled it.
                    if (roles.Contains(Revocations.Role)) throw new InvalidDataException($"Key {Dsse.Short(keyId)} is a revoker; a WebID binds publishing keys only.");
                }
                keys.Add(new TrustedKey(keyId, pem, prefixes, roles, Time(k, "notBefore"), Time(k, "notAfter"), webId));
            }
        var revokedDigests = new List<string>();
        var revokedKeys = new List<RevokedKey>();
        DateTimeOffset? expires = null;
        if (root.TryGetProperty("revoked", out var revoked))
        {
            Closed(revoked, "'revoked'", "digests", "keys", "expires");
            revokedDigests.AddRange(Strings(revoked, "digests"));
            // A revoked digest or key id that is not in its grammar would never match anything: refused, not ignored.
            if (revokedDigests.FirstOrDefault(d => !DigestPattern().IsMatch(d)) is { } badDigest)
                throw new InvalidDataException($"The revoked digest '{badDigest}' is not sha256: and 64 lowercase hexadecimal digits.");
            if (revoked.TryGetProperty("keys", out var rk))
                foreach (var r in rk.EnumerateArray())
                {
                    Closed(r, "A revoked key", "keyId", "compromisedAt");
                    var revokedId = r.GetProperty("keyId") is { ValueKind: JsonValueKind.String } id ? id.GetString()! : throw new FormatException("A revoked key's keyId is a string.");
                    if (!KeyIdPattern().IsMatch(revokedId)) throw new InvalidDataException($"The revoked key id '{revokedId}' is not 64 lowercase hexadecimal digits.");
                    revokedKeys.Add(new RevokedKey(revokedId, Time(r, "compromisedAt")));
                }
            expires = Time(revoked, "expires");
        }
        var required = root.TryGetProperty("requireRoles", out _) ? Strings(root, "requireRoles") : ["publisher"];
        if (required.Count == 0 || required.Any(r => !Dsse.Roles.Contains(r))) throw new InvalidDataException("requireRoles must name known roles.");
        IReadOnlyList<string> hostWebIds = [];
        if (root.TryGetProperty("host", out var host))
        {
            if (host.ValueKind != JsonValueKind.Object || host.EnumerateObject().Any(p => p.Name != "webIds"))
                throw new InvalidDataException("'host' is an object holding only 'webIds'.");
            hostWebIds = Strings(host, "webIds");
            if (hostWebIds.Count is 0 or > 16) throw new InvalidDataException("host.webIds lists 1 to 16 WebIDs.");
            if (hostWebIds.FirstOrDefault(w => !WebId.IsValid(w)) is not null) throw new InvalidDataException(WebId.Problem(null, "An entry of host.webIds"));
            if (hostWebIds.Distinct(StringComparer.Ordinal).Count() != hostWebIds.Count) throw new InvalidDataException("host.webIds names a WebID twice.");
        }
        return new TrustPolicy
        {
            Keys = keys, RevokedDigests = revokedDigests, RevokedKeys = revokedKeys, RevocationsExpire = expires, StaticRevocationsExpire = expires, RequiredRoles = required,
            HostWebIds = hostWebIds,
        };
    }

    // An object holding only the fields named; anything else in it makes the policy malformed.
    private static void Closed(JsonElement element, string what, params string[] fields)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{what} is not an object.");
        if (element.EnumerateObject().Select(p => p.Name).FirstOrDefault(n => !fields.Contains(n, StringComparer.Ordinal)) is { } unknown)
            throw new InvalidDataException($"{what} holds a field the trust policy does not define: '{unknown}' (it takes {string.Join(", ", fields)}).");
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\Asha256:[0-9a-f]{64}\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex DigestPattern();

    [System.Text.RegularExpressions.GeneratedRegex(@"\A[0-9a-f]{64}\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex KeyIdPattern();

    // The SubjectPublicKeyInfo of a P-256 key up to its point: SEQUENCE, the ecPublicKey and prime256v1 OIDs, a BIT STRING of 66.
    private static readonly byte[] P256SpkiPrefix = Convert.FromHexString("3059301306072A8648CE3D020106082A8648CE3D030107034200");

    /// <summary>
    /// Whether <paramref name="pem"/> holds a P-256 key in the one form both verifiers read alike (review 3): the 91-byte
    /// SubjectPublicKeyInfo of an uncompressed point (04, X, Y), in canonical base64 (no stray bits in the last quantum). A
    /// compressed or hybrid point, or base64 with stray bits, decoded differently in the two verifiers.
    /// </summary>
    internal static bool IsCanonicalP256(string pem)
    {
        var body = string.Concat(pem.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => !line.StartsWith("-----", StringComparison.Ordinal)));
        byte[] der;
        try { der = Convert.FromBase64String(body); }
        catch (FormatException) { return false; }
        return der.Length == 91 && der.AsSpan(0, P256SpkiPrefix.Length).SequenceEqual(P256SpkiPrefix) && der[P256SpkiPrefix.Length] == 0x04
            && Convert.ToBase64String(der) == body;
    }

    /// <summary>Whether <paramref name="pem"/> is exactly one PEM <c>PUBLIC KEY</c> block (base64 lines, LF or CRLF), and nothing else.</summary>
    internal static bool IsPublicKeyPem(string pem) => PublicKeyPemPattern().IsMatch(pem);

    [System.Text.RegularExpressions.GeneratedRegex(@"\A-----BEGIN PUBLIC KEY-----\r?\n(?:[A-Za-z0-9+/=]{1,76}\r?\n){1,16}-----END PUBLIC KEY-----(?:\r?\n)?\z",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex PublicKeyPemPattern();

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
