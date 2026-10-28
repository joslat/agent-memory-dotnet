using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentMemory.Packs.Format;

/// <summary>
/// A publication receipt (federated packs §5): one placement of a federated pack at a URL, signed apart from the pack so the
/// pack and its digest never change and one pack can have many placements. A DSSE envelope of an in-toto Statement v1 whose
/// subject is the pack's <c>META/files.json</c> and whose predicate names the exact archive bytes at that URL, the audience
/// the placement grants (never wider than the pack's), how the store enforces it, and what the publisher's tool probed.
/// </summary>
/// <param name="Id">The pack's id.</param>
/// <param name="Version">The pack's version.</param>
/// <param name="PackDigest">The pack's identity: <c>sha256:</c> over its <c>META/files.json</c>.</param>
/// <param name="Location">The exact URL the archive was placed at.</param>
/// <param name="ArchiveSha256">SHA-256 (lowercase hex) of the exact archive bytes at that URL.</param>
/// <param name="Audience">The WebIDs the placement grants read access to; null when public.</param>
/// <param name="Access">How the store enforces it: <c>wac</c>, <c>acp</c> or <c>public</c>.</param>
/// <param name="Denied">Who the publisher's probe found refused: <c>anonymous</c> or WebIDs.</param>
/// <param name="Allowed">WebIDs the probe found reading the exact bytes back.</param>
/// <param name="PublishedAt">When it was placed (UTC, whole seconds); checked against the signing key's window.</param>
internal sealed record PublicationReceipt(string Id, string Version, string PackDigest, string Location, string ArchiveSha256,
    IReadOnlyList<string>? Audience, string Access, IReadOnlyList<string> Denied, IReadOnlyList<string> Allowed, DateTimeOffset PublishedAt)
{
    internal const string PredicateType = "urn:agentmemory:pack:publication:v1";
    internal const string Anonymous = "anonymous";
    internal static readonly IReadOnlySet<string> AccessKinds = new HashSet<string>(StringComparer.Ordinal) { "wac", "acp", "public" };

    /// <summary>The receipt's resource name beside the archive: <c>sha256-&lt;hex&gt;.receipt.dsse.json</c>.</summary>
    internal static string FileNameFor(string packDigest) => $"{ResourceStem(packDigest)}.receipt.dsse.json";

    /// <summary>The archive's resource name: <c>sha256-&lt;hex&gt;.ampack</c>, named by the pack's identity.</summary>
    internal static string ArchiveNameFor(string packDigest) => $"{ResourceStem(packDigest)}.ampack";

    private static string ResourceStem(string packDigest) =>
        packDigest.StartsWith("sha256:", StringComparison.Ordinal) && packDigest.Length == 71 ? "sha256-" + packDigest[7..]
            : throw new ArgumentException("A pack digest is 'sha256:' and 64 hexadecimal characters.", nameof(packDigest));

    /// <summary>Signs this receipt; the signer must be a key the recipients trust as publisher for the id, bound to its WebID.</summary>
    internal byte[] Sign(PackSigner signer)
    {
        var predicate = new JsonObject
        {
            ["id"] = Id, ["version"] = Version, ["location"] = Location, ["archiveSha256"] = ArchiveSha256,
            ["audience"] = Audience is null ? PackFederation.Public : new JsonArray([.. Audience.Select(a => (JsonNode)a)]),
            ["access"] = Access,
            ["checked"] = new JsonObject
            {
                ["denied"] = new JsonArray([.. Denied.Select(d => (JsonNode)d)]),
                ["allowed"] = new JsonArray([.. Allowed.Select(a => (JsonNode)a)]),
            },
            ["publishedAt"] = PublishedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        };
        var statement = new JsonObject
        {
            ["_type"] = Dsse.StatementType,
            ["subject"] = new JsonArray(new JsonObject { ["name"] = PackPaths.FilesList, ["digest"] = new JsonObject { ["sha256"] = PackDigest[7..] } }),
            ["predicateType"] = PredicateType,
            ["predicate"] = predicate,
        };
        var payload = Jcs.Canonical(statement);
        // What is signed is read back by the reader's own rules first: a receipt nobody can verify is never signed (FED-review).
        if (Read(payload).Problem is { } problem) throw new InvalidDataException(problem);
        var signature = signer.Sign(Dsse.Pae(Dsse.PayloadType, payload));
        return Jcs.Canonical(new JsonObject
        {
            ["payloadType"] = Dsse.PayloadType,
            ["payload"] = Convert.ToBase64String(payload),
            ["signatures"] = new JsonArray(new JsonObject { ["keyid"] = signer.KeyId, ["sig"] = Convert.ToBase64String(signature) }),
        });
    }

    /// <summary>
    /// Verifies a receipt for <paramref name="pack"/>, fetched from <paramref name="fetchedFrom"/> as <paramref name="archive"/>:
    /// a valid signature by a key the host trusts as publisher for the pack's id and binds to the pack's publisher WebID; the
    /// pack's digest; that URL and those bytes; an audience within the pack's; and this host inside it. Returns the key that
    /// signed it too, so a fetch can re-check that key in the publisher's profile as it does the pack's (FED-review M1).
    /// </summary>
    internal static (PublicationReceipt? Receipt, string? SignedBy, IReadOnlyList<string> Problems) Verify(ReadOnlySpan<byte> envelopeBytes, VerifiedPack pack,
        ReadOnlySpan<byte> archive, string fetchedFrom, TrustPolicy policy, DateTimeOffset now)
    {
        var problems = new List<string>();
        if (pack.Manifest.Federation is not { } federation) return (null, null, ["A publication receipt is for a federated pack; this pack names no publisher or audience."]);
        if (envelopeBytes.Length > Dsse.MaximumEnvelopeBytes) return (null, null, [$"The receipt is larger than {Dsse.MaximumEnvelopeBytes / 1024} KiB."]);
        if (PackJson.Check(envelopeBytes) is { } malformed) return (null, null, [$"The receipt {malformed}."]);
        using var document = JsonDocument.Parse(envelopeBytes.ToArray());
        var envelope = document.RootElement;
        if (envelope.ValueKind != JsonValueKind.Object || envelope.EnumerateObject().Any(p => p.Name is not ("payloadType" or "payload" or "signatures"))
            || Str(envelope, "payloadType") != Dsse.PayloadType || Str(envelope, "payload") is not { } payloadText
            || !envelope.TryGetProperty("signatures", out var signatures) || signatures.ValueKind != JsonValueKind.Array
            || signatures.GetArrayLength() is 0 or > Dsse.MaximumSignatures)
            return (null, null, ["The receipt is not a DSSE envelope of an in-toto statement with 1 to 16 signatures."]);
        if (Dsse.FromBase64(payloadText) is not { } payload) return (null, null, ["The receipt's payload is not base64."]);
        var (receipt, readProblem) = Read(payload);
        if (receipt is null) return (null, null, [readProblem!]);

        if (receipt.PackDigest != pack.Digest) problems.Add($"The receipt is for {Dsse.Short(receipt.PackDigest[7..])}…, not this pack ({Dsse.Short(pack.Digest[7..])}…).");
        if (receipt.Id != pack.Id || receipt.Version != pack.Version) problems.Add($"The receipt names {receipt.Id} {receipt.Version}, not {pack.Id} {pack.Version}.");
        if (receipt.Location != fetchedFrom) problems.Add($"The receipt places the pack at {receipt.Location}, not at {fetchedFrom}, where it was fetched.");
        if (receipt.ArchiveSha256 != FilesList.Hex(archive.ToArray())) problems.Add("The archive fetched is not the archive the receipt names (its SHA-256 differs).");
        if (!federation.IsPublic && (receipt.Audience is null || receipt.Audience.Any(a => !federation.Audience!.Contains(a))))
            problems.Add("The receipt grants a wider audience than the pack allows: a placement can only narrow it.");
        if (receipt.Audience is not null && !receipt.Audience.Any(policy.HostWebIds.Contains))
            problems.Add(policy.HostWebIds.Count == 0
                ? "The placement is for named WebIDs, and this host names none of its own (trust policy 'host.webIds')."
                : $"The placement is for {PackFederation.Describe(receipt.Audience)}; this host is {string.Join(", ", policy.HostWebIds)}.");

        var hash = SHA256.HashData(Dsse.Pae(Dsse.PayloadType, payload));
        string? signedBy = null;
        var signatureProblems = new List<string>();
        foreach (var entry in signatures.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || Str(entry, "keyid") is not { } keyId || Str(entry, "sig") is not { } sigText)
            { signatureProblems.Add("The receipt has a malformed signature entry."); continue; }
            if (policy.Find(keyId) is not { } key) { signatureProblems.Add($"Key {Dsse.Short(keyId)} that signed the receipt is not in the trust policy."); continue; }
            if (Dsse.FromBase64(sigText) is not { } sig || !Dsse.IsStrictDer(sig)) { signatureProblems.Add($"The receipt's signature by {Dsse.Short(keyId)} is not strict DER in base64."); continue; }
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(key.PublicKeyPem);
            if (!ecdsa.VerifyHash(hash, sig, DSASignatureFormat.Rfc3279DerSequence)) { signatureProblems.Add($"The receipt's signature by {Dsse.Short(keyId)} does not verify."); continue; }
            if (policy.Refusal(key, "publisher", pack.Id, receipt.PublishedAt, now) is { } refusal) { signatureProblems.Add("The receipt: " + refusal); continue; }
            if (key.WebId != federation.Publisher) { signatureProblems.Add($"Key {Dsse.Short(keyId)} that signed the receipt is not bound to the pack's publisher {federation.Publisher}."); continue; }
            signedBy ??= keyId;
        }
        if (signedBy is null) problems.AddRange(signatureProblems.Count > 0 ? signatureProblems : ["The receipt carries no signature."]);
        return problems.Count > 0 ? (null, null, problems) : (receipt, signedBy, []);
    }

    /// <summary>Reads the statement, strictly: exactly the fields this format defines, each of its type.</summary>
    internal static (PublicationReceipt? Receipt, string? Problem) Read(byte[] payload)
    {
        if (PackJson.Check(payload) is { } malformed) return (null, $"The receipt's statement {malformed}.");
        using var document = JsonDocument.Parse(payload);
        var statement = document.RootElement;
        if (statement.ValueKind != JsonValueKind.Object || statement.EnumerateObject().Any(p => p.Name is not ("_type" or "subject" or "predicateType" or "predicate")))
            return (null, "The receipt's statement holds other fields than an in-toto Statement v1.");
        if (Str(statement, "_type") != Dsse.StatementType) return (null, "The receipt does not carry an in-toto Statement v1.");
        if (Str(statement, "predicateType") != PredicateType) return (null, $"The receipt's predicate type is not {PredicateType}.");
        if (!statement.TryGetProperty("subject", out var subjects) || subjects.ValueKind != JsonValueKind.Array || subjects.GetArrayLength() != 1
            || subjects[0].ValueKind != JsonValueKind.Object || Str(subjects[0], "name") != PackPaths.FilesList
            || !subjects[0].TryGetProperty("digest", out var digest) || digest.ValueKind != JsonValueKind.Object || digest.EnumerateObject().Count() != 1
            || Str(digest, "sha256") is not { Length: 64 } hex || !hex.All(char.IsAsciiHexDigitLower)
            || subjects[0].EnumerateObject().Count() != 2)
            return (null, $"The receipt must name exactly one subject: {PackPaths.FilesList} and its SHA-256.");
        if (!statement.TryGetProperty("predicate", out var predicate) || predicate.ValueKind != JsonValueKind.Object
            || predicate.EnumerateObject().Any(p => p.Name is not ("id" or "version" or "location" or "archiveSha256" or "audience" or "access" or "checked" or "publishedAt")))
            return (null, "The receipt's predicate holds other fields than this format defines.");
        if (Str(predicate, "id") is not { } id || !PackManifest.IsValidId(id) || Str(predicate, "version") is not { } version || !SemVer.TryParse(version, out _))
            return (null, "The receipt names no valid pack id and version.");
        if (Str(predicate, "location") is not { } location) return (null, "The receipt names no location.");
        if (Str(predicate, "archiveSha256") is not { Length: 64 } archive || !archive.All(char.IsAsciiHexDigitLower))
            return (null, "The receipt's archiveSha256 is not 64 lowercase hexadecimal characters.");
        if (!predicate.TryGetProperty("audience", out var audienceValue)) return (null, "The receipt names no audience.");
        var (audience, audienceProblem) = PackFederation.ReadAudience(audienceValue, "The receipt's audience");
        if (audienceProblem is not null) return (null, audienceProblem);
        if (Str(predicate, "access") is not { } access) return (null, "The receipt names no access kind.");
        if (!predicate.TryGetProperty("checked", out var check) || check.ValueKind != JsonValueKind.Object
            || check.EnumerateObject().Any(p => p.Name is not ("denied" or "allowed"))
            || Probes(check, "denied", allowAnonymous: true) is not { } denied || Probes(check, "allowed", allowAnonymous: false) is not { } allowed)
            return (null, "The receipt's 'checked' is an object of 'denied' and 'allowed', each a list of at most 64 WebIDs ('anonymous' may be denied).");
        if (Str(predicate, "publishedAt") is not { } publishedText
            || !DateTimeOffset.TryParseExact(publishedText, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var publishedAt))
            return (null, "The receipt has no publishing time in UTC (yyyy-MM-ddTHH:mm:ssZ).");
        var receipt = new PublicationReceipt(id, version, "sha256:" + hex, location, archive, audience, access, denied, allowed, publishedAt);
        return ShapeProblem(receipt) is { } problem ? (null, problem) : (receipt, null);
    }

    // What signing and reading both require of a receipt's values.
    private static string? ShapeProblem(PublicationReceipt r)
    {
        if (r.Audience is not null && (r.Audience.Count is 0 or > PackFederation.MaximumAudience || r.Audience.Any(a => !WebId.IsValid(a))
            || r.Audience.Distinct(StringComparer.Ordinal).Count() != r.Audience.Count))
            return "The receipt's audience is \"public\" or a list of 1 to 64 distinct WebIDs.";
        if ((ProbeProblem(r.Denied, allowAnonymous: true) ?? ProbeProblem(r.Allowed, allowAnonymous: false)) is { } probe) return probe;
        // The probe's record agrees with the placement (FED-review): anonymous is refused a placement for named readers and
        // admitted to a public one; whoever read the bytes back is in the audience; nobody in the audience was refused.
        if (r.Audience is not null)
        {
            if (!r.Denied.Contains(Anonymous)) return "A receipt for named readers records that anonymous was refused.";
            if (r.Allowed.Any(a => !r.Audience.Contains(a))) return "A receipt records a reader outside its audience as allowed.";
            if (r.Denied.Any(r.Audience.Contains)) return "A receipt records a reader in its audience as refused.";
        }
        else if (r.Denied.Contains(Anonymous)) return "A public receipt records that anonymous was refused.";
        // One grammar for every verifier: the WebID grammar, without a fragment (a resource, not an agent).
        if (!WebId.IsValid(r.Location) || r.Location.Contains('#'))
            return "The receipt's location is not an https URL (http only for localhost) in the WebID grammar, without a fragment.";
        if (!AccessKinds.Contains(r.Access)) return "The receipt's access is wac, acp or public.";
        if ((r.Access == "public") != (r.Audience is null)) return "A receipt's access is public exactly when its audience is public.";
        if (!r.PackDigest.StartsWith("sha256:", StringComparison.Ordinal) || r.PackDigest.Length != 71) return "The receipt's pack digest is not sha256:<hex>.";
        return null;
    }

    private static string? ProbeProblem(IReadOnlyList<string> probes, bool allowAnonymous) =>
        probes.Count > PackFederation.MaximumAudience || probes.Any(p => !(allowAnonymous && p == Anonymous) && !WebId.IsValid(p))
        || probes.Distinct(StringComparer.Ordinal).Count() != probes.Count
            ? "The receipt's 'checked' lists at most 64 distinct WebIDs each ('anonymous' may be denied)." : null;

    private static IReadOnlyList<string>? Probes(JsonElement check, string name, bool allowAnonymous)
    {
        if (!check.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > PackFederation.MaximumAudience) return null;
        var list = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) return null;
            var text = item.GetString()!;
            if (!(allowAnonymous && text == Anonymous) && !WebId.IsValid(text)) return null;
            list.Add(text);
        }
        return list.Distinct(StringComparer.Ordinal).Count() == list.Count ? list : null;
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
