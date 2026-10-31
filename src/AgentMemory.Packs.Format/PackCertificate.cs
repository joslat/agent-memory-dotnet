using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentMemory.Packs.Format;

/// <summary>
/// What one arm of an evaluation measured: counts, the p95 time in microseconds, the most items it could offer one task, and
/// each task's offered and right items: the evidence a host derives the verdict from again (review 5, root RB).
/// </summary>
internal sealed record CertificateArm(int Tasks, int Hits, int Offered, int Correct, long P95Micros, int MaximumOffered, int Spacing, IReadOnlyList<CertificateTask> PerTask);

/// <summary>One task of an arm: the items offered, how many were right, and the bytes it offered when measured (feature 05).</summary>
internal sealed record CertificateTask(int Offered, int Correct, long? Bytes = null);

/// <summary>
/// One target an evaluation checked, in millionths of the target's unit (feature 02: a rate of 0.304 is 304,000, the trial's own
/// integer; a time target is in millionths of a millisecond): what was required, what was used (a confidence bound when the trial asks for one), whether it held, and the
/// plain estimate beside a bound. Canonical JSON in this format holds integers only.
/// </summary>
internal sealed record CertificateCheck(string Target, long RequiredMicro, long ObservedMicro, bool Met, long? EstimateMicro);

/// <summary>
/// An evaluation certificate (pack format v1.2 §16): an evaluator's signed statement of how one pack did on the evaluator's
/// own tasks, against the trial the pack declares. A DSSE envelope of an in-toto Statement v1 whose subject is the pack's
/// <c>META/files.json</c> and whose predicate names the evaluator and its tool, the trial (measure, baseline, confidence, where
/// the tasks came from), every check, both arms, whether every target was met, and the AEF run it summarises, by AEF's own
/// run id and run hash. It travels beside the pack in <c>META/attestations/</c>, outside the files list, so certificates can
/// be added after release; a host trusts one only from a key it names as <c>evaluator</c> for the pack's id.
/// </summary>
internal sealed partial record PackCertificate(string Id, string Version, string PackDigest, string Evaluator, string Tool, string ToolVersion,
    DateTimeOffset EvaluatedAt, string Measure, string Baseline, int? ConfidencePermille, string TaskSource, string Statistic, IReadOnlyList<CertificateCheck> Checks,
    CertificateArm With, CertificateArm? Without, bool Passed, string? AefRunId, string? AefRunHash)
{
    internal const string PredicateType = "urn:agentmemory:pack:certificate:v1";
    /// <summary>The trust-policy role a key needs to issue certificates; it never signs packs.</summary>
    internal const string Role = "evaluator";
    /// <summary>A trial needs at least this many tasks, as the host's own gate does.</summary>
    internal const int MinimumTasks = PackIds.PackNamespace.MinimumTrialTasks;
    internal static readonly IReadOnlySet<string> Measures = PackRegistry.Measures;
    internal static readonly IReadOnlySet<string> Baselines = PackRegistry.Baselines;
    /// <summary>
    /// The targets a certificate states: a trial's declared ones. No regression is a host's comparison with the version that
    /// passed on that host, which a certificate carries no arm for, so a certificate never states it (review 7, G3: one issued
    /// from an upgrade trial carried those checks and could never be admitted).
    /// </summary>
    internal static readonly IReadOnlySet<string> Targets = PackRegistry.Targets.Concat(PackRegistry.CostFields.Keys).ToHashSet(StringComparer.Ordinal);
    /// <summary>The cost targets (feature 05), whose checks show an exact bound: met exactly when it reaches the target.</summary>
    internal static readonly IReadOnlySet<string> CostTargets = PackRegistry.CostFields.Keys.ToHashSet(StringComparer.Ordinal);
    /// <summary>The most bytes one task may record offering.</summary>
    internal const long MaximumTaskBytes = 1_000_000_000;

    /// <summary>The history a trial's tasks come from, as the host's gate holds it: the most recent 2,000 eligible commits.</summary>
    internal const int HistoryWindow = 2000;
    internal static readonly IReadOnlySet<int> Confidences = PackRegistry.Confidences;
    /// <summary>The most tasks a trial runs, so a certificate's per-task evidence is bounded (a trial declares 10 to 200).</summary>
    internal const int MaximumTasks = 200;
    /// <summary>The most items one task may offer, the format's traversal limit.</summary>
    internal const int MaximumOffered = 16;
    internal const int MaximumSpacing = 2000;

    /// <summary>Signs this certificate as its evaluator; what is signed is read back by the reader's own rules first.</summary>
    internal byte[] Sign(PackSigner signer)
    {
        var trial = new JsonObject { ["measure"] = Measure, ["baseline"] = Baseline, ["taskSource"] = TaskSource };
        if (ConfidencePermille is { } confidence) trial["confidencePermille"] = confidence;
        var predicate = new JsonObject
        {
            ["id"] = Id, ["version"] = Version, ["evaluator"] = Evaluator,
            ["tool"] = new JsonObject { ["name"] = Tool, ["version"] = ToolVersion },
            ["evaluatedAt"] = EvaluatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["trial"] = trial,
            ["statistic"] = Statistic,
            ["checks"] = new JsonArray([.. Checks.Select(c =>
            {
                var check = new JsonObject { ["target"] = c.Target, ["requiredMicro"] = c.RequiredMicro, ["observedMicro"] = c.ObservedMicro, ["met"] = c.Met };
                if (c.EstimateMicro is { } estimate) check["estimateMicro"] = estimate;
                return (JsonNode)check;
            })]),
            ["with"] = Arm(With),
            ["passed"] = Passed,
        };
        if (Without is { } without) predicate["without"] = Arm(without);
        if (AefRunId is { } runId) predicate["aef"] = new JsonObject { ["runId"] = runId, ["runHash"] = AefRunHash };
        var statement = new JsonObject
        {
            ["_type"] = Dsse.StatementType,
            ["subject"] = new JsonArray(new JsonObject { ["name"] = PackPaths.FilesList, ["digest"] = new JsonObject { ["sha256"] = PackDigest[7..] } }),
            ["predicateType"] = PredicateType,
            ["predicate"] = predicate,
        };
        var payload = Jcs.Canonical(statement);
        if (Read(payload).Problem is { } problem) throw new InvalidDataException(problem);
        var signature = signer.Sign(Dsse.Pae(Dsse.PayloadType, payload));
        return Jcs.Canonical(new JsonObject
        {
            ["payloadType"] = Dsse.PayloadType,
            ["payload"] = Convert.ToBase64String(payload),
            ["signatures"] = new JsonArray(new JsonObject { ["keyid"] = signer.KeyId, ["sig"] = Convert.ToBase64String(signature) }),
        });
    }

    private static JsonObject Arm(CertificateArm arm) => new()
    {
        ["tasks"] = arm.Tasks, ["hits"] = arm.Hits, ["offered"] = arm.Offered, ["correct"] = arm.Correct, ["p95Micros"] = arm.P95Micros,
        ["maximumOffered"] = arm.MaximumOffered, ["spacing"] = arm.Spacing,
        ["perTask"] = new JsonArray([.. arm.PerTask.Select(t => (JsonNode)(t.Bytes is { } b ? new JsonArray(t.Offered, t.Correct, b) : new JsonArray(t.Offered, t.Correct)))]),
    };

    /// <summary>
    /// Verifies a certificate for <paramref name="pack"/>: a valid signature by a key the host trusts as <c>evaluator</c> for
    /// the pack's id, valid at the evaluation time; the pack's digest, id and version; and, when the AEF run it names travels
    /// in the pack, that run's files hash to the run hash it names (AEF 1.0 SEAL-1 to SEAL-4); and, for one the pack carries
    /// under <paramref name="stem"/>, that the stem is a key that signed it and its evaluation time (§16; review 6, M3: only the
    /// grammar was checked, so a copy could travel under any name). Returns the key that signed it.
    /// </summary>
    internal static (PackCertificate? Certificate, string? SignedBy, IReadOnlyList<string> Problems) Verify(ReadOnlySpan<byte> envelopeBytes, VerifiedPack pack,
        TrustPolicy policy, DateTimeOffset now, IReadOnlyDictionary<string, byte[]>? run = null, string? stem = null)
    {
        var problems = new List<string>();
        if (envelopeBytes.Length > Dsse.MaximumEnvelopeBytes) return (null, null, [$"The certificate is larger than {Dsse.MaximumEnvelopeBytes / 1024} KiB."]);
        if (PackJson.Check(envelopeBytes) is { } malformed) return (null, null, [$"The certificate {malformed}."]);
        using var document = JsonDocument.Parse(envelopeBytes.ToArray());
        var envelope = document.RootElement;
        if (envelope.ValueKind != JsonValueKind.Object || envelope.EnumerateObject().Any(p => !PackRegistry.Shape("envelope").Contains(p.Name))
            || Str(envelope, "payloadType") != Dsse.PayloadType || Str(envelope, "payload") is not { } payloadText
            || !envelope.TryGetProperty("signatures", out var signatures) || signatures.ValueKind != JsonValueKind.Array
            || signatures.GetArrayLength() is 0 or > Dsse.MaximumSignatures || signatures.EnumerateArray().Any(e => !Dsse.IsEntry(e)))
            return (null, null, ["The certificate is not a DSSE envelope of an in-toto statement with 1 to 16 signatures, each a key id and a signature."]);
        if (Dsse.FromBase64(payloadText) is not { } payload) return (null, null, ["The certificate's payload is not base64."]);
        var (certificate, readProblem) = Read(payload);
        if (certificate is null) return (null, null, [readProblem!]);

        if (certificate.PackDigest != pack.Digest)
            problems.Add($"The certificate is for {Dsse.Short(certificate.PackDigest[7..])}…, not this pack ({Dsse.Short(pack.Digest[7..])}…).");
        if (certificate.Id != pack.Id || certificate.Version != pack.Version)
            problems.Add($"The certificate names {certificate.Id} {certificate.Version}, not {pack.Id} {pack.Version}.");
        if (run is not null)
        {
            if (certificate.AefRunHash is null) problems.Add("An AEF run travels with the certificate, but the certificate names none.");
            else if (PackAttestations.RunHash(run) != certificate.AefRunHash) problems.Add("The AEF run beside the certificate is not the run it names (its run hash differs).");
            else if (PackAttestations.RunId(run) is { } runId && runId != certificate.AefRunId)
                problems.Add($"The AEF run beside the certificate is {runId}, not {certificate.AefRunId}.");
            else if (!PackAttestations.RunIsOf(run, pack.Id, pack.Digest, pack.Version))
                problems.Add("The AEF run beside the certificate is the evidence of another decision: its subject is not this pack at this digest and version.");
        }

        var hash = SHA256.HashData(Dsse.Pae(Dsse.PayloadType, payload));
        string? signedBy = null;
        var signers = new List<string>();
        var signatureProblems = new List<string>();
        foreach (var entry in signatures.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || Str(entry, "keyid") is not { } keyId || Str(entry, "sig") is not { } sigText)
            { signatureProblems.Add("The certificate has a malformed signature entry."); continue; }
            if (policy.Find(keyId) is not { } key) { signatureProblems.Add($"Key {Dsse.Short(keyId)} that signed the certificate is not in the trust policy."); continue; }
            if (Dsse.FromBase64(sigText) is not { } sig || !Dsse.IsStrictDer(sig)) { signatureProblems.Add($"The certificate's signature by {Dsse.Short(keyId)} is not strict DER in base64."); continue; }
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(key.PublicKeyPem);
            if (!ecdsa.VerifyHash(hash, sig, DSASignatureFormat.Rfc3279DerSequence)) { signatureProblems.Add($"The certificate's signature by {Dsse.Short(keyId)} does not verify."); continue; }
            if (policy.Refusal(key, Role, pack.Id, certificate.EvaluatedAt, now) is { } refusal) { signatureProblems.Add("The certificate: " + refusal); continue; }
            signedBy ??= keyId;
            signers.Add(keyId);
        }
        if (signedBy is null) problems.AddRange(signatureProblems.Count > 0 ? signatureProblems : ["The certificate carries no signature."]);
        else if (stem is not null && !signers.Any(k => PackAttestations.StemFor(k, certificate.EvaluatedAt) == stem))
            problems.Add($"The certificate travels as {stem}, not as its evaluator's key and evaluation time ({PackAttestations.StemFor(signedBy, certificate.EvaluatedAt)}).");
        return problems.Count > 0 ? (null, null, problems) : (certificate, signedBy, []);
    }

    /// <summary>
    /// A certificate's identity: the SHA-256 of what its evaluator signed, the pre-authentication encoding of its payload, as a
    /// revocation list's (review 6, M3: it was the hash of the envelope's bytes, which anyone can respell without the key, so
    /// a copy could be ground to sort first). Null for bytes that are not an envelope.
    /// </summary>
    internal static string? IdentityOf(ReadOnlySpan<byte> envelopeBytes)
    {
        if (envelopeBytes.Length > Dsse.MaximumEnvelopeBytes || PackJson.Check(envelopeBytes) is not null) return null;
        using var document = JsonDocument.Parse(envelopeBytes.ToArray());
        if (document.RootElement.ValueKind != JsonValueKind.Object || Str(document.RootElement, "payload") is not { } text || Dsse.FromBase64(text) is not { } payload)
            return null;
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Dsse.Pae(Dsse.PayloadType, payload))).ToLowerInvariant();
    }

    /// <summary>Reads the statement, strictly: exactly the fields this format defines, each of its type and range.</summary>
    internal static (PackCertificate? Certificate, string? Problem) Read(byte[] payload)
    {
        if (PackJson.Check(payload) is { } malformed) return (null, $"The certificate's statement {malformed}.");
        using var document = JsonDocument.Parse(payload);
        var statement = document.RootElement;
        if (statement.ValueKind != JsonValueKind.Object || statement.EnumerateObject().Any(p => !PackRegistry.Shape("statement").Contains(p.Name)))
            return (null, "The certificate's statement holds other fields than an in-toto Statement v1.");
        if (Str(statement, "_type") != Dsse.StatementType) return (null, "The certificate does not carry an in-toto Statement v1.");
        if (Str(statement, "predicateType") != PredicateType) return (null, $"The certificate's predicate type is not {PredicateType}.");
        if (!statement.TryGetProperty("subject", out var subjects) || subjects.ValueKind != JsonValueKind.Array || subjects.GetArrayLength() != 1
            || subjects[0].ValueKind != JsonValueKind.Object || Str(subjects[0], "name") != PackPaths.FilesList
            || !subjects[0].TryGetProperty("digest", out var digest) || digest.ValueKind != JsonValueKind.Object || digest.EnumerateObject().Count() != 1
            || Str(digest, "sha256") is not { Length: 64 } hex || !hex.All(char.IsAsciiHexDigitLower)
            || subjects[0].EnumerateObject().Count() != 2)
            return (null, $"The certificate must name exactly one subject: {PackPaths.FilesList} and its SHA-256.");
        if (!statement.TryGetProperty("predicate", out var p) || p.ValueKind != JsonValueKind.Object
            || p.EnumerateObject().Any(f => !PackRegistry.Shape("certificatePredicate").Contains(f.Name)))
            return (null, "The certificate's predicate holds other fields than this format defines.");
        if (Str(p, "id") is not { } id || !PackManifest.IsValidId(id) || Str(p, "version") is not { } version || !SemVer.TryParse(version, out _))
            return (null, "The certificate names no valid pack id and version.");
        if (Str(p, "evaluator") is not { } evaluator || !PackCard.IsLine(evaluator, 200))
            return (null, "The certificate names its evaluator as one line of 1 to 200 characters.");
        if (!p.TryGetProperty("tool", out var tool) || tool.ValueKind != JsonValueKind.Object || tool.EnumerateObject().Any(f => !PackRegistry.Shape("certificateTool").Contains(f.Name))
            || Str(tool, "name") is not { } toolName || !IsLine(toolName, 100) || Str(tool, "version") is not { } toolVersion || !IsLine(toolVersion, 64))
            return (null, "The certificate names its tool as 'name' (1 to 100 characters) and 'version' (1 to 64).");
        if (Str(p, "evaluatedAt") is not { } evaluatedText
            || !DateTimeOffset.TryParseExact(evaluatedText, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var evaluatedAt))
            return (null, "The certificate has no evaluation time in UTC (yyyy-MM-ddTHH:mm:ssZ).");
        if (!p.TryGetProperty("trial", out var trial) || trial.ValueKind != JsonValueKind.Object
            || trial.EnumerateObject().Any(f => !PackRegistry.Shape("certificateTrial").Contains(f.Name))
            || Str(trial, "measure") is not { } measure || !Measures.Contains(measure)
            || Str(trial, "baseline") is not { } baseline || !Baselines.Contains(baseline)
            || Str(trial, "taskSource") is not { } taskSource || !IsLine(taskSource, 500))
            return (null, "The certificate's trial names its measure (test-impact or retrieval), its baseline (host or none) and where its tasks came from (1 to 500 characters).");
        if (Str(p, "statistic") is not { } statistic || !IsLine(statistic, 100))
            return (null, "The certificate names the statistic its checks were computed with (one line of 1 to 100 characters).");
        int? confidence = null;
        if (trial.TryGetProperty("confidencePermille", out var confidenceValue))
        {
            if (confidenceValue.ValueKind != JsonValueKind.Number || !confidenceValue.TryGetInt32(out var c) || !Confidences.Contains(c))
                return (null, "The certificate's confidencePermille is 900, 950, 975 or 990.");
            confidence = c;
        }
        if (!p.TryGetProperty("checks", out var checksValue) || checksValue.ValueKind != JsonValueKind.Array || checksValue.GetArrayLength() is 0 or > 16)
            return (null, "The certificate lists 1 to 16 checks.");
        var checks = new List<CertificateCheck>();
        foreach (var c in checksValue.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object || c.EnumerateObject().Any(f => !PackRegistry.Shape("certificateCheck").Contains(f.Name))
                || Str(c, "target") is not { } target || !Targets.Contains(target)
                || Long(c, "requiredMicro") is not { } required || Long(c, "observedMicro") is not { } observed
                || !c.TryGetProperty("met", out var met) || met.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return (null, "A certificate check names a known target, requiredMicro and observedMicro as integers, and met.");
            long? estimate = null;
            if (c.TryGetProperty("estimateMicro", out _))
            {
                if (Long(c, "estimateMicro") is not { } e) return (null, "A certificate check's estimateMicro is an integer.");
                estimate = e;
            }
            checks.Add(new CertificateCheck(target, required, observed, met.ValueKind == JsonValueKind.True, estimate));
        }
        if (checks.Select(c => c.Target).Distinct(StringComparer.Ordinal).Count() != checks.Count) return (null, "The certificate checks one target twice.");
        const string ArmShape = "arm is tasks (at most 200), hits, offered, correct, p95Micros, maximumOffered (0 to 16), spacing (1 to 2000) and perTask, one [offered, correct] or [offered, correct, bytes] a task "
            + "within the cap, summing to offered and correct, with a right item on exactly its hits.";
        if (ReadArm(p, "with") is not { } with) return (null, "The certificate's 'with' " + ArmShape);
        CertificateArm? without = null;
        if (p.TryGetProperty("without", out _) && (without = ReadArm(p, "without")) is null)
            return (null, "The certificate's 'without' " + ArmShape);
        if (!p.TryGetProperty("passed", out var passedValue) || passedValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return (null, "The certificate says whether every target was met ('passed').");
        string? runId = null, runHash = null;
        if (p.TryGetProperty("aef", out var aef))
        {
            if (aef.ValueKind != JsonValueKind.Object || aef.EnumerateObject().Any(f => !PackRegistry.Shape("certificateAef").Contains(f.Name))
                || Str(aef, "runId") is not { } id2 || !RunIdPattern().IsMatch(id2) || Str(aef, "runHash") is not { Length: 64 } h || !h.All(char.IsAsciiHexDigitLower))
                return (null, "The certificate's 'aef' names an AEF runId and its runHash (64 lowercase hexadecimal characters).");
            (runId, runHash) = (id2, h);
        }
        var certificate = new PackCertificate(id, version, "sha256:" + hex, evaluator, toolName, toolVersion, evaluatedAt, measure, baseline, confidence,
            taskSource, statistic, checks, with, without, passedValue.ValueKind == JsonValueKind.True, runId, runHash);
        return ShapeProblem(certificate) is { } problem ? (null, problem) : (certificate, null);
    }

    // What signing and reading both require of a certificate's values.
    private static string? ShapeProblem(PackCertificate c)
    {
        // Each task's bytes (feature 05): on every task of both arms or on none, and present for a cost check, whose met is its
        // exact bound at or above its target (R-TRIAL-056), so no statistic is needed to check it.
        var tasks = c.With.PerTask.Concat(c.Without?.PerTask ?? []).ToList();
        if (tasks.Any(t => t.Bytes is null) && tasks.Any(t => t.Bytes is not null))
            return "The certificate records the bytes of some tasks and not others: every task of both arms, or none.";
        if (c.Checks.Any(k => CostTargets.Contains(k.Target)) && (c.Without is null || tasks.Any(t => t.Bytes is null)))
            return "A cost check needs both arms and each task's bytes.";
        if (new[] { c.With, c.Without }.Any(arm => arm is { MaximumOffered: 0 } && arm.PerTask.Any(t => t.Bytes is > 0)))
            return "An arm that offers nothing records no bytes.";
        if (c.Checks.FirstOrDefault(k => CostTargets.Contains(k.Target) && k.Met != (k.ObservedMicro >= k.RequiredMicro)) is { } wrong)
            return $"The certificate's {wrong.Target} check says {(wrong.Met ? "met" : "not met")}, but its bound is {(wrong.Met ? "below" : "at or above")} its target.";
        // The verdict agrees with the record: passed exactly when every check held on enough tasks.
        var holds = c.Checks.All(k => k.Met) && c.With.Tasks >= MinimumTasks;
        if (c.Passed != holds)
            return c.Passed ? "The certificate says the pack passed, but a check failed or fewer than 10 tasks were run."
                : "The certificate says the pack did not pass, but every check held on enough tasks.";
        if (c.Without is { } without && without.Tasks != c.With.Tasks) return "The certificate's arms ran different numbers of tasks.";
        if (c.Baseline == "host" && c.Without is null) return "A certificate measured against the host's retrieval records that arm.";
        // Review 7, G4 and G5: the parameters every bound rests on mean what they say. The arms ran the same tasks, so one
        // spacing, within the history a trial draws from; a baseline of none offers nothing, a host baseline can offer.
        if (c.Without is { } other && other.Spacing != c.With.Spacing) return "The certificate's arms were drawn at different spacings; they ran the same tasks.";
        if (c.With.Tasks > 1 && (long)c.With.Spacing * (c.With.Tasks - 1) >= HistoryWindow)
            return $"The certificate's {c.With.Tasks} tasks, drawn every {c.With.Spacing} eligible commits, span more than the {HistoryWindow} a trial draws from.";
        if (c.Baseline == "none" && c.Without is { } empty && (empty.MaximumOffered != 0 || empty.Offered != 0))
            return "A certificate measured against none records a baseline arm that offers nothing: a cap of 0.";
        if (c.Baseline == "host" && c.Without is { MaximumOffered: 0 })
            return "A certificate measured against the host's retrieval records that arm with a cap of 1 or more.";
        if (!c.PackDigest.StartsWith("sha256:", StringComparison.Ordinal) || c.PackDigest.Length != 71) return "The certificate's pack digest is not sha256:<hex>.";
        return null;
    }

    private static CertificateArm? ReadArm(JsonElement predicate, string name)
    {
        if (!predicate.TryGetProperty(name, out var arm) || arm.ValueKind != JsonValueKind.Object
            || arm.EnumerateObject().Any(f => !PackRegistry.Shape("certificateArm").Contains(f.Name))
            || Long(arm, "tasks") is not { } tasks || Long(arm, "hits") is not { } hits || Long(arm, "offered") is not { } offered
            || Long(arm, "correct") is not { } correct || Long(arm, "p95Micros") is not { } p95 || Long(arm, "maximumOffered") is not { } cap
            || Long(arm, "spacing") is not { } spacing
            || !arm.TryGetProperty("perTask", out var perTaskValue) || perTaskValue.ValueKind != JsonValueKind.Array)
            return null;
        // A cap of 0 is an arm that offers nothing by design, the baseline none (review 6, M5); the spacing is how far apart in
        // history the tasks were drawn (review 6, H4).
        if (tasks is < 0 or > MaximumTasks || cap is < 0 or > MaximumOffered || spacing is < 1 or > MaximumSpacing || p95 is < 0 or > 86_400_000_000
            || perTaskValue.GetArrayLength() != tasks)
            return null;
        var perTask = new List<CertificateTask>((int)tasks);
        foreach (var entry in perTaskValue.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() is not (2 or 3) || entry[0].ValueKind != JsonValueKind.Number || entry[1].ValueKind != JsonValueKind.Number
                || !entry[0].TryGetInt64(out var o) || !entry[1].TryGetInt64(out var c) || c < 0 || c > o || o > cap)
                return null;
            long? bytes = null;
            if (entry.GetArrayLength() == 3)
            {
                if (entry[2].ValueKind != JsonValueKind.Number || !entry[2].TryGetInt64(out var b) || b is < 0 or > MaximumTaskBytes) return null;
                bytes = b;
            }
            perTask.Add(new CertificateTask((int)o, (int)c, bytes));
        }
        // The totals are the evidence's own: a hit is a task with a right item (review 5, L9).
        return perTask.Sum(t => (long)t.Offered) != offered || perTask.Sum(t => (long)t.Correct) != correct || perTask.Count(t => t.Correct > 0) != hits
            ? null : new CertificateArm((int)tasks, (int)hits, (int)offered, (int)correct, p95, (int)cap, (int)spacing, perTask);
    }

    private static long? Long(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    // One line of text: no control characters at all.
    private static bool IsLine(string text, int maximum) => PackCard.IsLine(text, maximum);

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex("\\A[A-Za-z0-9._:-]{1,128}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex RunIdPattern();
}

/// <summary>
/// The attestations area (pack format v1.2 §16): <c>META/attestations/&lt;stem&gt;.certificate.dsse.json</c>, and beside a
/// certificate the AEF run it names, as files under <c>META/attestations/&lt;stem&gt;.aef/</c>. Outside the files list, so the
/// pack's digest never changes; bounded and shaped exactly, so nothing else travels with a pack unlisted.
/// </summary>
internal static partial class PackAttestations
{
    internal const int MaximumCertificates = 8;
    internal const int MaximumRunFiles = 256;
    internal const long MaximumRunBytes = 8L << 20;
    internal const string CertificateSuffix = ".certificate.dsse.json";
    internal const string RunSuffix = ".aef/";

    /// <summary>The certificate file for a stem.</summary>
    internal static string CertificatePath(string stem) => PackPaths.AttestationsFolder + stem + CertificateSuffix;

    /// <summary>A stem for a certificate: the evaluator key's first 16 hex digits and the evaluation time.</summary>
    internal static string StemFor(string keyId, DateTimeOffset evaluatedAt) =>
        keyId[..16] + "-" + evaluatedAt.UtcDateTime.ToString("yyyyMMdd't'HHmmss'z'", CultureInfo.InvariantCulture);

    /// <summary>The certificates a pack carries, by stem, in stem order.</summary>
    internal static IReadOnlyList<(string Stem, byte[] Envelope)> Certificates(IReadOnlyDictionary<string, byte[]> files) =>
        [.. files.Where(f => CertificateFile().Match(f.Key).Success).Select(f => (Stem: CertificateFile().Match(f.Key).Groups[1].Value, f.Value))
            .OrderBy(c => c.Stem, StringComparer.Ordinal)];

    /// <summary>The AEF run beside a certificate, by its run-relative paths; null when none travels with it.</summary>
    internal static IReadOnlyDictionary<string, byte[]>? Run(IReadOnlyDictionary<string, byte[]> files, string stem)
    {
        var folder = PackPaths.AttestationsFolder + stem + RunSuffix;
        var run = files.Where(f => f.Key.StartsWith(folder, StringComparison.Ordinal)).ToDictionary(f => f.Key[folder.Length..], f => f.Value, StringComparer.Ordinal);
        return run.Count == 0 ? null : run;
    }

    /// <summary>Every problem with the attestations a pack carries: their shape and their bounds. None of them is trusted here.</summary>
    internal static IEnumerable<string> Problems(IReadOnlyDictionary<string, byte[]> files)
    {
        var attestations = files.Keys.Where(PackPaths.IsAttestation).Order(StringComparer.Ordinal).ToList();
        var stems = new HashSet<string>(StringComparer.Ordinal);
        var erasures = 0;
        foreach (var path in attestations)
        {
            if (CertificateFile().Match(path) is { Success: true } certificate)
            {
                stems.Add(certificate.Groups[1].Value);
                if (files[path].LongLength > Dsse.MaximumEnvelopeBytes) yield return $"'{path}' is larger than {Dsse.MaximumEnvelopeBytes / 1024} KiB.";
            }
            else if (PackErasure.File().IsMatch(path))
            {
                erasures++;
                if (files[path].LongLength > Dsse.MaximumEnvelopeBytes) yield return $"'{path}' is larger than {Dsse.MaximumEnvelopeBytes / 1024} KiB.";
            }
            else if (!RunFile().IsMatch(path))
                yield return $"'{path}' is not an attestation: {PackPaths.AttestationsFolder} holds <stem>{CertificateSuffix}, <stem>{PackErasure.Suffix} and <stem>{RunSuffix}… only.";
        }
        if (stems.Count > MaximumCertificates) yield return $"The pack carries {stems.Count} certificates; at most {MaximumCertificates}.";
        if (erasures > MaximumCertificates) yield return $"The pack carries {erasures} erasures; at most {MaximumCertificates}.";
        foreach (var group in attestations.Select(p => RunFile().Match(p)).Where(m => m.Success).GroupBy(m => m.Groups[1].Value, StringComparer.Ordinal))
        {
            if (!stems.Contains(group.Key)) yield return $"The AEF run {group.Key}{RunSuffix} travels without its certificate.";
            var count = group.Count();
            var bytes = group.Sum(m => files[m.Value].LongLength);
            if (count > MaximumRunFiles || bytes > MaximumRunBytes)
                yield return $"The AEF run {group.Key}{RunSuffix} holds {count} files and {bytes:N0} bytes; at most {MaximumRunFiles} files and {MaximumRunBytes:N0} bytes.";
        }
    }

    /// <summary>
    /// An AEF run's run hash (AEF 1.0 SEAL-1 to SEAL-4): the SHA-256 of its manifest, one line per sealed file (every file but
    /// <c>seal.json</c>, <c>attestation.dsse.json</c> and <c>overlays/</c>), "&lt;sha256&gt;  &lt;size&gt;  &lt;path&gt;\n", ordered by the UTF-8
    /// bytes of the path.
    /// </summary>
    internal static string RunHash(IReadOnlyDictionary<string, byte[]> run)
    {
        var manifest = new StringBuilder();
        foreach (var (path, bytes) in run.Where(f => !PackRegistry.Shape("aefRunUnhashed").Contains(f.Key) && !f.Key.StartsWith("overlays/", StringComparison.Ordinal))
                     .OrderBy(f => f.Key, StringComparer.Ordinal))
            manifest.Append(FilesList.Hex(bytes)).Append("  ").Append(bytes.LongLength.ToString(CultureInfo.InvariantCulture)).Append("  ").Append(path).Append('\n');
        return FilesList.Hex(Encoding.UTF8.GetBytes(manifest.ToString()));
    }

    /// <summary>
    /// Whether an AEF run is the evidence of this pack's decision: its <c>run.json</c> subject names the pack at this digest and
    /// version (round 10, B10-M1: a run was checked by its id and hash only, so another version's run travelled as this one's).
    /// </summary>
    internal static bool RunIsOf(IReadOnlyDictionary<string, byte[]> run, string id, string digest, string version) =>
        RunJson(run, root => root.TryGetProperty("subject", out var subject) && subject.ValueKind == JsonValueKind.Object
            && subject.TryGetProperty("ref", out var reference) && reference.ValueKind == JsonValueKind.String && reference.GetString() == $"pack:{id}@{digest}"
            && subject.TryGetProperty("version", out var named) && named.ValueKind == JsonValueKind.String && named.GetString() == version);

    /// <summary>
    /// The run id <c>run.json</c> names, or null when it names none readably: read in the format's JSON profile, as every
    /// verifier reads it, so a run.json in another encoding or with repeated keys cannot make two verifiers disagree.
    /// </summary>
    internal static string? RunId(IReadOnlyDictionary<string, byte[]> run) =>
        RunJson(run, root => root.TryGetProperty("runId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null);

    // The one reading of a run's run.json: bounded, in the format's JSON profile, an object; null (or false) when it is not.
    private static T? RunJson<T>(IReadOnlyDictionary<string, byte[]> run, Func<JsonElement, T> read)
    {
        if (!run.TryGetValue("run.json", out var bytes) || bytes.Length > 1 << 20 || PackJson.Check(bytes) is not null) return default;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            return document.RootElement.ValueKind == JsonValueKind.Object ? read(document.RootElement) : default;
        }
        catch (JsonException) { return default; }
    }

    [GeneratedRegex("\\AMETA/attestations/([a-z0-9][a-z0-9-]{0,63})\\.certificate\\.dsse\\.json\\z", RegexOptions.CultureInvariant)]
    private static partial Regex CertificateFile();

    [GeneratedRegex("\\AMETA/attestations/([a-z0-9][a-z0-9-]{0,63})\\.aef/.+\\z", RegexOptions.CultureInvariant)]
    private static partial Regex RunFile();
}
