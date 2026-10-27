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
    /// <paramref name="policy"/> with the document applied: verified by a revoker key it trusts, valid now and not revoked,
    /// issued no later than now (five minutes' grace), its revocations added. With a <paramref name="ledger"/>, what it revokes
    /// is kept by the host for good, and so is the newest list's freshness (see <see cref="ApplyAll"/>).
    /// </summary>
    /// <exception cref="InvalidDataException">The document is malformed, signed by no trusted revoker that is valid now and not
    /// revoked, or issued in the future.</exception>
    internal static TrustPolicy Apply(TrustPolicy policy, ReadOnlySpan<byte> envelopeBytes, DateTimeOffset now, HostLedger? ledger = null)
    {
        return ApplyAll(policy, [envelopeBytes.ToArray()], now, ledger, out _);
    }

    /// <summary>
    /// The policy with every document of one run applied, in order (one per revoker, say). With a <paramref name="ledger"/>, what
    /// the host revoked before is applied first, so a revoker revoked in an earlier run signs nothing now (sixth review).
    /// </summary>
    /// <remarks>
    /// <para>Fail closed within a run, authority only from live revokers (seventh review). Every authentic list from a key the
    /// policy names as revoker, live or not, adds its revocations of packs and of non-revoker keys for this run: they can only
    /// deny, and a revoker could deny while it was live. Only a list with a live signer (valid now, revoked by nobody: not the
    /// policy, not this host before, not another such list now) also revokes revoker keys, sets freshness and is kept by the
    /// ledger. Without trusted timestamps a list's issue time is its signer's claim, so a revoked revoker's lists carry no
    /// authority at any date; when a revoker is rotated out, its successor re-issues what it revoked (carry-forward). A list from
    /// a revoker that is not live is named in <paramref name="withdrawn"/>. When lists revoke each other's signers and none is
    /// beyond doubt, nothing is applied: the operator decides which revoker to believe.</para>
    /// <para>Freshness is the newest list's: an older list adds its revocations but never its expiry, and with a ledger the
    /// newest list ever applied sets it, so leaving lists out, or giving only older ones, cannot keep a host fresh past the
    /// newest list's expiry (sixth review). Lists issued in the same second keep the earlier expiry.</para>
    /// </remarks>
    /// <exception cref="InvalidDataException">A document is malformed, not signed by a trusted revoker valid now, or issued in
    /// the future; or the lists revoke each other's signers.</exception>
    internal static TrustPolicy ApplyAll(TrustPolicy policy, IReadOnlyList<byte[]> documents, DateTimeOffset now, HostLedger? ledger,
        out IReadOnlyList<string> withdrawn)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(documents);
        if (ledger is not null) policy = ledger.Apply(policy);
        var read = documents.Select(d => Read(policy, d, now)).ToList();
        var revokedBefore = policy.RevokedKeys.Select(k => k.KeyId).ToHashSet(StringComparer.Ordinal);

        // Settle which lists count: one with a signer nobody now revokes counts; one whose every signer is revoked before or by
        // a counted list does not; what is left revokes itself in a circle.
        var state = new int[read.Count]; // 0 open, 1 counts, 2 withdrawn
        for (var changed = true; changed;)
        {
            changed = false;
            for (var i = 0; i < read.Count; i++)
            {
                if (state[i] != 0) continue;
                bool RevokedBy(string signer, Func<int, bool> which) =>
                    Enumerable.Range(0, read.Count).Any(j => j != i && which(j) && read[j].Keys.Any(k => k.KeyId == signer));
                var live = read[i].Signers.Where(s => !revokedBefore.Contains(s) && ValidNow(policy, s, now)).ToList();
                if (live.Any(s => !RevokedBy(s, j => state[j] != 2))) { state[i] = 1; changed = true; }
                else if (live.All(s => RevokedBy(s, j => state[j] == 1))) { state[i] = 2; changed = true; }
            }
        }
        if (state.Contains(0)) throw Bad("set given in this run revokes its own signers in a circle; give only the lists of the revokers you still trust");

        var notes = new List<string>();
        var revokers = policy.Keys.Where(k => k.Roles.Contains(Role)).Select(k => k.KeyId).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < read.Count; i++)
        {
            var document = read[i];
            var counts = state[i] == 1;
            // A list without a live signer still denies what it names, for this run; it never decides about a revoker.
            var keys = counts ? document.Keys : [.. document.Keys.Where(k => !revokers.Contains(k.KeyId))];
            if (!counts)
                notes.Add($"The revocation list issued at {document.IssuedAt:u} has no live signer ({string.Join(", ", document.Signers.Select(Dsse.Short))}): "
                    + "its revocations of packs and keys hold for this run only; its revocations of revokers and its freshness do not count.");
            // Added, never removed: a key revoked outright stays revoked; of two compromise times the earlier holds.
            var merged = policy.RevokedKeys.ToDictionary(k => k.KeyId, StringComparer.Ordinal);
            foreach (var key in keys)
                merged[key.KeyId] = merged.TryGetValue(key.KeyId, out var known) && (known.CompromisedAt is null || key.CompromisedAt is { } later && later > known.CompromisedAt)
                    ? known : key;
            policy = policy with
            {
                RevokedDigests = [.. policy.RevokedDigests.Union(document.Digests, StringComparer.Ordinal)],
                RevokedKeys = [.. merged.Values.OrderBy(k => k.KeyId, StringComparer.Ordinal)],
            };
            if (!counts) continue;
            policy = Fresher(policy, document.IssuedAt, document.Expires);
            // Kept by the host for good. An expired list leaves no freshness mark: replaying one must not hold a host back.
            ledger?.Record(document.Digests, document.Keys);
            if (document.Expires > now) ledger?.RecordFreshness(document.IssuedAt, document.Expires);
        }
        withdrawn = notes.AsReadOnly();
        return ledger is null ? policy : ledger.Apply(policy);
    }

    /// <summary>The policy's freshness after a list issued at <paramref name="issuedAt"/>: the newest list's expiry, capped by the policy's own.</summary>
    internal static TrustPolicy Fresher(TrustPolicy policy, DateTimeOffset issuedAt, DateTimeOffset expires)
    {
        if (policy.RevocationsIssued is { } newest && issuedAt < newest) return policy;
        // Before any list, the policy's expiry is its own; the cap is taken from it then, so one set in code is kept too.
        var cap = policy.RevocationsIssued is null ? policy.RevocationsExpire : policy.StaticRevocationsExpire;
        var listExpiry = policy.RevocationsIssued == issuedAt && policy.RevocationsExpire is { } same && same < expires ? same : expires;
        return policy with
        {
            RevocationsIssued = issuedAt, StaticRevocationsExpire = cap,
            RevocationsExpire = cap is { } fixedExpiry && fixedExpiry < listExpiry ? fixedExpiry : listExpiry,
        };
    }

    // A revoker key within its window now; one outside it signs authentic lists without authority.
    private static bool ValidNow(TrustPolicy policy, string keyId, DateTimeOffset now) =>
        policy.Find(keyId) is { } key && !(key.NotBefore is { } from && now < from || key.NotAfter is { } until && now > until);

    private sealed record Document(IReadOnlyList<string> Signers, DateTimeOffset IssuedAt, DateTimeOffset Expires, IReadOnlyList<string> Digests, IReadOnlyList<RevokedKey> Keys);

    // One document read and its signatures checked: the revoker keys, trusted and valid now, whose signatures hold.
    private static Document Read(TrustPolicy policy, ReadOnlySpan<byte> envelopeBytes, DateTimeOffset now)
    {
        if (envelopeBytes.Length > MaximumDocumentBytes) throw Bad($"is larger than {MaximumDocumentBytes >> 20} MiB");
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
        var signers = signatures.EnumerateArray().Select(s => Signer(policy, s, hash)).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (signers.Count == 0) throw Bad("is not signed by a key the trust policy names as revoker");

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
        return new Document(signers, issuedAt, expires, digests, keys);
    }

    // The key id of one signature entry by a key the policy names as revoker, when it verifies the hash in strict DER; whether
    // that key is live (valid now, revoked by nobody) is decided for the whole run (ApplyAll).
    private static string? Signer(TrustPolicy policy, JsonElement entry, byte[] hash)
    {
        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("keyid", out var keyId) || keyId.ValueKind != JsonValueKind.String
            || !entry.TryGetProperty("sig", out var sig) || sig.ValueKind != JsonValueKind.String)
            return null;
        if (policy.Find(keyId.GetString()!) is not { } key || !key.Roles.Contains(Role)) return null;
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(key.PublicKeyPem);
            return Dsse.FromBase64(sig.GetString()) is { } signature && Dsse.IsStrictDer(signature)
                && ecdsa.VerifyHash(hash, signature, DSASignatureFormat.Rfc3279DerSequence) ? key.KeyId : null;
        }
        catch (FormatException) { return null; }
    }

    private static string Time(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static InvalidDataException Bad(string what) => new($"The revocation document {what}.");

    [GeneratedRegex("\\Asha256:[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex DigestPattern();

    [GeneratedRegex("\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyIdPattern();
}
