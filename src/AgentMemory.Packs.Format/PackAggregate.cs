using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentMemory.Packs.Format;

/// <summary>One target of an aggregate: the counts it rests on, what was required, the bound (without noise) and whether it held.</summary>
internal sealed record AggregateCheck(string Target, long Successes, long Trials, long RequiredMicro, long? BoundMicro, bool Met);

/// <summary>
/// A host's signed aggregate of a trial (feature 13, R-CORE-140): counts, bounds and verdicts per target, the number of tasks and
/// the window, optionally with integer discrete-Laplace noise, and nothing else. It names no task, question, record or path: the
/// registry's closed shapes leave no place for one. Signed by a key the reader trusts as evaluator for the pack's id.
/// </summary>
internal sealed record PackAggregate(string Id, string Version, string Digest, string Measure, int Tasks, DateTimeOffset From, DateTimeOffset To,
    string Mechanism, int? EpsilonMilli, string Decision, IReadOnlyList<AggregateCheck> Checks, bool Passed)
{
    internal static readonly string PredicateType = PackRegistry.AggregatePredicateType;
    internal static readonly IReadOnlySet<string> Roles = PackRegistry.AggregateRoles;
    internal const int MaximumChecks = 16;

    /// <summary>Builds and signs an aggregate; refuses one this reader would refuse.</summary>
    internal static byte[] Create(PackAggregate aggregate, PackSigner signer)
    {
        static string Instant(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var predicate = new JsonObject
        {
            ["id"] = aggregate.Id, ["version"] = aggregate.Version, ["digest"] = aggregate.Digest, ["measure"] = aggregate.Measure, ["tasks"] = aggregate.Tasks,
            ["window"] = new JsonObject { ["from"] = Instant(aggregate.From), ["to"] = Instant(aggregate.To) },
            ["mechanism"] = aggregate.Mechanism, ["decision"] = aggregate.Decision, ["passed"] = aggregate.Passed,
            ["checks"] = new JsonArray([.. aggregate.Checks.Select(c =>
            {
                var check = new JsonObject { ["target"] = c.Target, ["successes"] = c.Successes, ["trials"] = c.Trials, ["requiredMicro"] = c.RequiredMicro, ["met"] = c.Met };
                if (c.BoundMicro is { } bound) check["boundMicro"] = bound;
                return (JsonNode)check;
            })]),
        };
        if (aggregate.EpsilonMilli is { } epsilon) predicate["epsilonMilli"] = epsilon;
        var statement = new JsonObject
        {
            ["_type"] = Dsse.StatementType,
            ["subject"] = new JsonArray(new JsonObject { ["name"] = PackPaths.FilesList, ["digest"] = new JsonObject { ["sha256"] = aggregate.Digest["sha256:".Length..] } }),
            ["predicateType"] = PredicateType,
            ["predicate"] = predicate,
        };
        var payload = Jcs.Canonical(statement);
        if (Read(payload).Problem is { } problem) throw new InvalidDataException(problem);
        var signature = signer.Sign(Dsse.Pae(Dsse.PayloadType, payload));
        return Jcs.Canonical(new JsonObject
        {
            ["payloadType"] = Dsse.PayloadType, ["payload"] = Convert.ToBase64String(payload),
            ["signatures"] = new JsonArray(new JsonObject { ["keyid"] = signer.KeyId, ["sig"] = Convert.ToBase64String(signature) }),
        });
    }

    /// <summary>
    /// Verifies an aggregate of the pack whose file list has <paramref name="digest"/>: a DSSE envelope signed by a key the reader
    /// trusts as evaluator for the pack's id, its statement read strictly and its counts consistent.
    /// </summary>
    internal static (PackAggregate? Aggregate, IReadOnlyList<string> Problems) Verify(ReadOnlySpan<byte> envelopeBytes, string id, string version, string digest,
        TrustPolicy policy, DateTimeOffset now)
    {
        if (envelopeBytes.Length > Dsse.MaximumEnvelopeBytes) return (null, [$"The aggregate is larger than {Dsse.MaximumEnvelopeBytes / 1024} KiB."]);
        if (PackJson.Check(envelopeBytes) is { } malformed) return (null, [$"The aggregate {malformed}."]);
        using var document = JsonDocument.Parse(envelopeBytes.ToArray());
        var envelope = document.RootElement;
        if (envelope.ValueKind != JsonValueKind.Object || envelope.EnumerateObject().Any(p => !PackRegistry.Shape("envelope").Contains(p.Name))
            || Str(envelope, "payloadType") != Dsse.PayloadType || Str(envelope, "payload") is not { } payloadText
            || !envelope.TryGetProperty("signatures", out var signatures) || signatures.ValueKind != JsonValueKind.Array
            || signatures.GetArrayLength() is 0 or > Dsse.MaximumSignatures || signatures.EnumerateArray().Any(e => !Dsse.IsEntry(e)))
            return (null, ["The aggregate is not a DSSE envelope of an in-toto statement with 1 to 16 signatures, each a key id and a signature."]);
        if (Dsse.FromBase64(payloadText) is not { } payload) return (null, ["The aggregate's payload is not base64."]);
        var (aggregate, problem) = Read(payload);
        if (aggregate is null) return (null, [problem!]);
        // Of this pack in every name it gives (review 11-13, H6: a key trusted for one namespace vouched for any pack whose digest it
        // named, under another id, as a certificate's id and version are held to the pack's).
        if (aggregate.Digest != digest || aggregate.Id != id || aggregate.Version != version)
            return (null, [$"The aggregate is of {aggregate.Id} {aggregate.Version} ({aggregate.Digest}), not this pack, {id} {version} ({digest})."]);
        var hash = SHA256.HashData(Dsse.Pae(Dsse.PayloadType, payload));
        var refusals = new List<string>();
        foreach (var entry in signatures.EnumerateArray())
        {
            if (Str(entry, "keyid") is not { } keyId || Str(entry, "sig") is not { } sigText) continue;
            if (policy.Find(keyId) is not { } key) { refusals.Add($"Key {Dsse.Short(keyId)} that signed the aggregate is not in the trust policy."); continue; }
            if (Dsse.FromBase64(sigText) is not { } sig || !Dsse.IsStrictDer(sig)) { refusals.Add($"The aggregate's signature by {Dsse.Short(keyId)} is not strict DER in base64."); continue; }
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(key.PublicKeyPem);
            if (!ecdsa.VerifyHash(hash, sig, DSASignatureFormat.Rfc3279DerSequence)) { refusals.Add($"The aggregate's signature by {Dsse.Short(keyId)} does not verify."); continue; }
            var roleRefusals = Roles.Order(StringComparer.Ordinal).Select(role => policy.Refusal(key, role, aggregate.Id, aggregate.To, now)).ToList();
            if (roleRefusals.Any(r => r is null)) return (aggregate, []);
            refusals.Add("The aggregate: " + roleRefusals.First());
        }
        return (null, refusals.Count > 0 ? refusals : ["The aggregate carries no signature by a key this host trusts as evaluator for the pack."]);
    }

    /// <summary>Reads the statement strictly: the registry's closed shapes, each field of its type and range, the counts consistent.</summary>
    internal static (PackAggregate? Aggregate, string? Problem) Read(byte[] payload)
    {
        if (PackJson.Check(payload, integersOnly: true) is { } malformed) return (null, $"The aggregate's statement {malformed}.");
        using var document = JsonDocument.Parse(payload);
        var statement = document.RootElement;
        if (statement.ValueKind != JsonValueKind.Object || statement.EnumerateObject().Any(p => !PackRegistry.Shape("statement").Contains(p.Name))
            || Str(statement, "_type") != Dsse.StatementType || Str(statement, "predicateType") != PredicateType)
            return (null, $"The aggregate is not an in-toto statement of {PredicateType}.");
        if (!statement.TryGetProperty("predicate", out var p) || p.ValueKind != JsonValueKind.Object
            || p.EnumerateObject().Any(f => !PackRegistry.Shape("aggregatePredicate").Contains(f.Name)))
            return (null, "The aggregate's predicate holds only the registry's fields: no task, question, record or path has a place in it.");
        if (Str(p, "id") is not { } id || !PackManifest.IsValidId(id) || Str(p, "version") is not { } version || PackText.Length(version) is 0 or > 256)
            return (null, "The aggregate names a valid pack id and version.");
        if (Str(p, "digest") is not { } digest || !IsDigest(digest)) return (null, "The aggregate's digest is sha256: and 64 lowercase hexadecimal characters.");
        if (!statement.TryGetProperty("subject", out var subject) || subject.ValueKind != JsonValueKind.Array || subject.GetArrayLength() != 1
            || subject[0].ValueKind != JsonValueKind.Object || Str(subject[0], "name") != PackPaths.FilesList
            || !subject[0].TryGetProperty("digest", out var sd) || sd.ValueKind != JsonValueKind.Object || sd.EnumerateObject().Count() != 1
            || Str(sd, "sha256") != digest["sha256:".Length..])
            return (null, "The aggregate's subject is the pack's file list, by the digest its predicate names.");
        if (Str(p, "measure") is not { } measure || !PackRegistry.Measures.Contains(measure)) return (null, "The aggregate names a known measure.");
        if (Long(p, "tasks") is not { } tasks || tasks is < 1 or > 1_000_000) return (null, "The aggregate's tasks are 1 to 1,000,000.");
        if (!p.TryGetProperty("window", out var w) || w.ValueKind != JsonValueKind.Object || w.EnumerateObject().Any(f => !PackRegistry.Shape("aggregateWindow").Contains(f.Name))
            || Time(w, "from") is not { } from || Time(w, "to") is not { } to || from > to)
            return (null, "The aggregate's window is from and to, instants in UTC to the second, from not after to.");
        if (Str(p, "mechanism") is not { } mechanism || !PackRegistry.AggregateMechanisms.Contains(mechanism))
            return (null, "The aggregate's mechanism is " + string.Join(" or ", PackRegistry.AggregateMechanisms.Order(StringComparer.Ordinal)) + ".");
        var noisy = mechanism != "none";
        int? epsilon = null;
        if (p.TryGetProperty("epsilonMilli", out _))
        {
            if (!noisy || Long(p, "epsilonMilli") is not { } e || e is < 1 or > 100_000) return (null, "The aggregate's epsilonMilli, 1 to 100,000, goes with noise only.");
            epsilon = (int)e;
        }
        else if (noisy) return (null, "An aggregate with noise names its epsilonMilli.");
        if (Str(p, "decision") is not { } decision || !IsDigest(decision)) return (null, "The aggregate names its decision by sha256: and 64 lowercase hexadecimal characters.");
        if (!p.TryGetProperty("checks", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() is 0 or > MaximumChecks)
            return (null, $"The aggregate lists 1 to {MaximumChecks} checks.");
        var checks = new List<AggregateCheck>();
        foreach (var c in list.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object || c.EnumerateObject().Any(f => !PackRegistry.Shape("aggregateCheck").Contains(f.Name))
                || Str(c, "target") is not { } target || !PackRegistry.Targets.Contains(target)
                || Long(c, "successes") is not { } successes || Long(c, "trials") is not { } trials || Long(c, "requiredMicro") is not { } required
                || !c.TryGetProperty("met", out var met) || met.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return (null, "An aggregate check names a known target, its successes, trials and requiredMicro as integers, and met.");
            long? bound = null;
            if (c.TryGetProperty("boundMicro", out _))
            {
                if (noisy || Long(c, "boundMicro") is not { } b) return (null, "An aggregate check's boundMicro is an integer, without noise only: noise makes it approximate.");
                bound = b;
            }
            else if (!noisy) return (null, "An aggregate without noise gives each check its boundMicro.");
            // Counts never below zero; without noise, successes within trials, and a bound that says what met says.
            if (successes < 0 || trials < 0) return (null, "An aggregate's counts are never negative.");
            if (!noisy && successes > trials) return (null, "An aggregate check's successes are within its trials.");
            var atMost = PackRegistry.AtMost.Contains(target);
            if (bound is { } holds && (atMost ? holds <= required : holds >= required) != met.GetBoolean())
                return (null, "An aggregate check's met disagrees with its bound and its requiredMicro.");
            checks.Add(new AggregateCheck(target, successes, trials, required, bound, met.GetBoolean()));
        }
        if (checks.Select(c => c.Target).Distinct(StringComparer.Ordinal).Count() != checks.Count) return (null, "An aggregate checks each target once.");
        if (!p.TryGetProperty("passed", out var passed) || passed.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || passed.GetBoolean() != checks.All(c => c.Met))
            return (null, "The aggregate's passed is whether every check was met.");
        return (new PackAggregate(id, version, digest, measure, (int)tasks, from, to, mechanism, epsilon, decision, checks, passed.GetBoolean()), null);
    }

    private static bool IsDigest(string s) => s.Length == 71 && s.StartsWith("sha256:", StringComparison.Ordinal) && s[7..].All(char.IsAsciiHexDigitLower);

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Long(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : null;

    private static DateTimeOffset? Time(JsonElement element, string name) =>
        Str(element, name) is { } text && DateTimeOffset.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;
}
