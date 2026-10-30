using System.Text;

namespace AgentMemory.Packs.Format;

/// <summary>A pack that passed every check, with what the host needs next.</summary>
internal sealed record VerifiedPack(string Id, string Version, string Digest, PackManifest Manifest, IReadOnlyList<PackSignature> Signatures,
    IReadOnlyDictionary<string, byte[]> Files)
{
    /// <summary>What travels beside the pack in <c>META/attestations/</c>, shaped and bounded but not trusted (§16).</summary>
    internal IReadOnlyDictionary<string, byte[]> Attestations { get; init; } = new Dictionary<string, byte[]>(StringComparer.Ordinal);
}

/// <summary>One step of a verification, as <c>ampack verify --explain</c> shows it: what it checked, whether it held, and the details.</summary>
internal sealed record PackStep(string Name, bool Passed, IReadOnlyList<string> Lines);

/// <summary>The verification result: the pack, or every reason it was refused.</summary>
internal sealed record PackVerification(VerifiedPack? Pack, IReadOnlyList<string> Problems)
{
    internal bool IsVerified => Pack is not null;
}

/// <summary>
/// Verifies a pack before anything in it is parsed into a store (v1.1 FIG. 2, step 200), authenticating first (review 5, H1:
/// every listed JSON file was parsed before any signature was checked, so an unsigned pack cost a host a full parse). Before
/// the signatures nothing is parsed but the signed list and the signature envelopes, each bounded:
/// <list type="number">
/// <item>every path keeps the grammar;</item>
/// <item><c>META/files.json</c>, at most 2 MiB, is canonical and well formed;</item>
/// <item>the pack holds exactly the listed files, each with its length and SHA-256 (CRLF and Git LFS are named as causes), and
/// only shaped and bounded attestations beside them;</item>
/// <item>every required role has a valid signature from a key scoped to this id;</item>
/// </list>
/// and only then, for a pack whose signatures hold:
/// <list type="number">
/// <item>every listed JSON file, at most 1 MiB, and every JSONL line, at most 1 MiB, keeps the JSON profile;</item>
/// <item><c>pack.json</c> names the same id and version as the list, and the kind holds what it must;</item>
/// <item>the digest is not revoked, the revocations are fresh, and the pack is still valid;</item>
/// <item>a federated pack's publisher and audience hold, and the host ledger allows this id, version and digest.</item>
/// </list>
/// A step that fails stops what depends on it; the problems of the steps after the signatures are reported together.
/// </summary>
internal static class PackVerifier
{
    /// <summary>The largest signed list read before any signature holds: 4,096 entries of the longest paths fit.</summary>
    internal const int MaximumFilesListBytes = 2 << 20;
    /// <summary>The largest listed JSON file, and the longest JSONL line, a verifier parses (review 5, H1: a parse tree is many
    /// times the bytes, in either verifier).</summary>
    internal const int MaximumJsonBytes = 1 << 20;

    /// <summary>
    /// One pack's own list, signatures and content: a set's outer signature only, never its members (review 8, F3: a door that
    /// acts only on a verified pack took this for "verified", and countersigned and pinned sets whose members its host refuses).
    /// A door that acts on a pack takes <see cref="VerifyWhole"/>; this is for the steps that report a set member by member.
    /// </summary>
    // explain: when given, told the outcome of every step in order (ampack verify --explain); the verdict is the same.
    internal static PackVerification VerifyOne(PackContent content, TrustPolicy policy, DateTimeOffset now, HostLedger? ledger = null, bool allowDowngrade = false,
        Action<PackStep>? explain = null)
    {
        var (integrity, list, listBytes) = CheckSigned(content, explain);
        if (integrity.Count > 0) return new(null, integrity);
        // What this host revoked before still holds, whatever lists this run was given (fifth review).
        if (ledger is not null) policy = ledger.Apply(policy);
        var (signatures, refusal) = Signatures(content, list!, listBytes!, policy, now, explain);
        if (refusal is not null) return new(null, refusal);
        return Signed(content, list!, listBytes!, signatures, policy, now, ledger, allowDowngrade, explain);
    }

    /// <summary>
    /// What a caller needs to choose how to load a pack, authenticated first (review 6, H1: the load paths parsed an unsigned
    /// pack.json of any size to pick the loader): the paths, the signed list, exactly the content it lists, and the signatures
    /// the policy requires; only then pack.json, bounded. The chosen loader verifies everything. The host's kept revocations
    /// hold here too, as in <see cref="VerifyOne"/> (review 7, D10: a key the ledger revoked passed this step, so its pack.json was
    /// parsed and shown as authenticated before the loader refused it).
    /// </summary>
    internal static (PackManifest? Manifest, IReadOnlyList<string> Problems) Authenticated(PackContent content, TrustPolicy policy, DateTimeOffset now,
        HostLedger? ledger = null)
    {
        var (integrity, list, listBytes) = CheckSigned(content, null);
        if (integrity.Count > 0) return (null, integrity);
        if (ledger is not null) policy = ledger.Apply(policy);
        var (_, refusal) = Signatures(content, list!, listBytes!, policy, now, null);
        if (refusal is not null) return (null, refusal);
        if (!content.Files.TryGetValue(PackManifest.FileName, out var bytes)) return (null, [$"There is no {PackManifest.FileName}."]);
        var (manifest, problem) = PackManifest.Parse(bytes);
        return manifest is null ? (null, [problem!]) : (manifest, []);
    }

    // The signatures over the files list: those that count, and why the pack is refused when the required roles do not hold
    // or an envelope is junk (null when it is not).
    private static (List<PackSignature> Signatures, List<string>? Refusal) Signatures(PackContent content, FilesList list, byte[] listBytes, TrustPolicy policy,
        DateTimeOffset now, Action<PackStep>? explain)
    {
        var signatures = new List<PackSignature>();
        var junk = new List<string>();
        var envelopeProblems = new List<string>();
        var envelopes = content.Files.Keys.Where(PackPaths.IsEnvelope).Order(StringComparer.Ordinal).ToList();
        // Bounded: every envelope is checked against one hash of the files list (fifth review). The folder's own rules (at most
        // 16 envelopes; timestamp tokens, §2.1, beside an envelope and small, not judged by this reader) are one function, also
        // run with the content, so lint and build hold them too (review 7, F3).
        junk.AddRange(PackPaths.SignatureFolderProblems(content.Files));
        var tokens = content.Files.Keys.Where(PackPaths.IsTimestamp).ToList();
        var listHex = FilesList.Hex(listBytes);
        foreach (var file in envelopes.Take(Dsse.MaximumEnvelopes))
        {
            var (valid, invalid, malformed) = Dsse.Verify(file, content.Files[file], listHex, list, policy, now);
            signatures.AddRange(valid);
            (malformed ? junk : envelopeProblems).AddRange(invalid);
        }
        var missingRoles = policy.RequiredRoles.Where(r => signatures.All(s => s.Role != r)).ToList();
        explain?.Invoke(new("signatures", missingRoles.Count == 0 && junk.Count == 0, [
            .. signatures.Select(s => $"counted: {s.Role} by key {Dsse.Short(s.KeyId)} at {s.SignedAt:u} ({s.File})"),
            .. envelopeProblems.Select(p => (missingRoles.Count == 0 ? "ignored (the required roles hold): " : "not counted: ") + p),
            .. junk.Select(p => "refuses the pack: " + p),
            .. missingRoles.Select(r => $"missing: no valid {r} signature from a key the policy trusts for '{list.Id}'"),
            .. tokens.Count > 0 ? [$"timestamp tokens beside {tokens.Count} envelope(s): kept, not judged (trusted time is format 1.3)"] : (string[])[],
            $"required roles: {string.Join(", ", policy.RequiredRoles)}"]));
        // A pack whose signatures do not hold is refused here, before anything else in it is parsed. Invalid extra envelopes are
        // reported only then; a file that is not an envelope at all is unlisted content travelling with the pack (PACK-review 1.9).
        if (missingRoles.Count > 0 || junk.Count > 0)
            return (signatures, [
                .. missingRoles.Count > 0 ? envelopeProblems : [],
                .. missingRoles.Select(role => $"No valid {role} signature: a pack is refused unless a key the host trusts for '{list.Id}' signed it as {role}."),
                .. junk]);
        return (signatures, null);
    }

    // Once the signatures hold: the content, the records, the lifetime, the federation and the ledger.
    private static PackVerification Signed(PackContent content, FilesList list, byte[] listBytes, List<PackSignature> signatures, TrustPolicy policy,
        DateTimeOffset now, HostLedger? ledger, bool allowDowngrade, Action<PackStep>? explain)
    {
        var (problems, manifest) = CheckContent(content, list, explain, records: false);
        if (manifest is null) return new(null, problems);
        var recordProblems = LineProblems(content).ToList();
        problems.AddRange(recordProblems);
        explain?.Invoke(new("records", recordProblems.Count == 0, recordProblems.Count > 0 ? recordProblems
            : ["every JSONL file: UTF-8, LF, one JSON value per line of at most 1 MiB, no repeated key, NFC strings, integers within 2^53"]));
        var digest = FilesList.DigestOf(listBytes);
        var lifetime = new List<string>();
        if (policy.RevokedDigests.Contains(digest)) lifetime.Add($"The pack {digest} is revoked.");
        if (manifest.ValidUntil is { } until && until < now) lifetime.Add($"The pack was valid until {until:u}.");
        if (policy.RevocationsExpire is { } expires && expires < now) lifetime.Add($"The host's revocation list expired at {expires:u}; refresh it before verifying.");
        problems.AddRange(lifetime);
        explain?.Invoke(new("revocation and expiry", lifetime.Count == 0, lifetime.Count > 0 ? lifetime : [
            $"{digest} is not revoked" + (policy.RevocationsExpire is { } fresh ? $"; the revocation list is fresh until {fresh:u}" : ""),
            manifest.ValidUntil is { } validUntil ? $"valid until {validUntil:u}" : "no validUntil"]));

        if (manifest.Federation is { } federation)
        {
            var federationProblems = FederationProblems(federation, signatures, policy);
            problems.AddRange(federationProblems);
            explain?.Invoke(new("federation", federationProblems.Count == 0, federationProblems.Count > 0 ? federationProblems : [
                $"published by {federation.Publisher}, signed by a key this host bound to that WebID",
                federation.IsPublic ? "for everyone" : $"for {federation.Audience!.Count} WebID(s); this host is one of them",
                .. federation.Purpose is { } purpose ? [$"purpose: {purpose}"] : Array.Empty<string>()]));
        }

        var ledgerRefusal = ledger?.Refusal(list.Id, list.Version, digest, allowDowngrade);
        if (ledgerRefusal is not null) problems.Add(ledgerRefusal);
        if (ledger is not null)
            explain?.Invoke(new("ledger", ledgerRefusal is null, [ledgerRefusal ?? $"{list.Id} {list.Version} with {digest[..23]}… is allowed by the ledger"]));
        return problems.Count > 0
            ? new(null, problems)
            : new(new VerifiedPack(list.Id, list.Version, digest, manifest, signatures,
                content.Files.Where(f => FilesList.IsListed(f.Key)).ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal))
            {
                Attestations = content.Files.Where(f => PackPaths.IsAttestation(f.Key)).ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal),
            }, []);
    }

    /// <summary>
    /// The federated profile (federated packs §3): the publisher WebID is the signer's, by the host's own binding, and the host
    /// is in the audience. A trusted key bound to another WebID cannot publish in this one's name.
    /// </summary>
    internal static List<string> FederationProblems(PackFederation federation, IReadOnlyList<PackSignature> signatures, TrustPolicy policy)
    {
        var problems = new List<string>();
        var publishers = signatures.Where(s => s.Role == "publisher").Select(s => policy.Find(s.KeyId)).OfType<TrustedKey>().ToList();
        foreach (var other in publishers.Where(k => k.WebId is not null && k.WebId != federation.Publisher))
            problems.Add($"The pack names publisher {federation.Publisher}, but key {Dsse.Short(other.KeyId)} that signed it as publisher is bound to {other.WebId}.");
        if (!publishers.Any(k => k.WebId == federation.Publisher))
            problems.Add($"No publisher signature from a key this host bound to {federation.Publisher}: a federated pack needs one (trust entry 'webId').");
        if (!federation.Admits(policy.HostWebIds))
            problems.Add(policy.HostWebIds.Count == 0
                ? "The pack is published for named WebIDs, and this host names none of its own (trust policy 'host.webIds')."
                : $"The pack is published for {PackFederation.Describe(federation.Audience)}; this host is {string.Join(", ", policy.HostWebIds)}.");
        return problems;
    }

    /// <summary>
    /// Every check that needs no trust, in order. <c>ampack lint</c>, the writers' read-back and every door that reads a pack before
    /// it is trusted use it alone, on their own input. It holds what a reader refuses without a key (review 8, F1 and F2: the
    /// envelope shapes and a set's structure lived only where a key was at hand, so the writers wrote, and lint passed, packs
    /// every reader refuses): each envelope signs this list for this pack, and a set carries exactly the members it names, none
    /// with its own id, each the pack its folder names, none a set, and each passing these checks itself.
    /// </summary>
    internal static (List<string> Problems, FilesList? List, byte[]? ListBytes, PackManifest? Manifest) CheckIntegrity(PackContent content,
        Action<PackStep>? explain = null, bool records = true)
    {
        var (problems, list, listBytes) = CheckSigned(content, explain);
        if (problems.Count > 0) return (problems, null, null, null);
        var listHex = FilesList.Hex(listBytes!);
        var envelopes = content.Files.Keys.Where(PackPaths.IsEnvelope).Order(StringComparer.Ordinal).Take(Dsse.MaximumEnvelopes)
            .Select(file => Dsse.ShapeProblem(file, content.Files[file], listHex, list!)).OfType<string>().ToList();
        explain?.Invoke(new("envelopes", envelopes.Count == 0, envelopes.Count > 0 ? envelopes
            : ["every envelope a DSSE envelope of a pack role's statement signing this list (whose keys count is judged once trust is applied)"]));
        if (envelopes.Count > 0) return (envelopes, null, null, null);
        var (rest, manifest) = CheckContent(content, list!, explain, records);
        if (manifest is { Kind: "set" })
        {
            var (structure, members) = SetStructure(list!.Id, manifest, content);
            rest.AddRange(structure);
            foreach (var (folder, files) in (members ?? []).OrderBy(m => m.Key, StringComparer.Ordinal))
            {
                var member = CheckIntegrity(new PackContent(files, content.Source + "#" + folder), records: records);
                rest.AddRange(member.Problems.Select(p => $"packs/{folder}/: {p}"));
                if (member.Manifest is { } carried && MemberProblem(folder, member.List!.Id, carried.Kind) is { } wrong) rest.Add(wrong);
                if (member.Manifest is { } inner && manifest.Federation is { } federation && MemberFederationProblem(federation, member.List!.Id, inner) is { } outside)
                    rest.Add(outside);
            }
            if (rest.Count > 0) return (rest, null, null, null);
        }
        return manifest is null ? (rest, null, null, null) : (rest, list, listBytes, manifest);
    }

    // A set's structure, which needs no trust: every file under packs/ in a member folder, exactly the members pack.json names,
    // none with the set's id (review 1, L5: the two would collide in a host's ledger).
    private static (List<string> Problems, Dictionary<string, Dictionary<string, byte[]>>? Members) SetStructure(string setId, PackManifest manifest,
        PackContent content)
    {
        Dictionary<string, Dictionary<string, byte[]>> split;
        try { split = PackSets.Members(content.Files); }
        catch (InvalidDataException e) { return ([e.Message], null); }
        if (!split.Keys.Order(StringComparer.Ordinal).SequenceEqual(manifest.Contains.Order(StringComparer.Ordinal)))
            return (["The set's pack.json names other members than it carries."], null);
        if (split.ContainsKey(setId)) return ([$"The set {setId} carries a member with its own id."], null);
        return ([], split);
    }

    // What a member is, signed or not: the pack its folder names, and a pack, not a set (review 1, L5: a set inside a set would
    // leave its own members unchecked).
    private static string? MemberProblem(string folder, string id, string kind) =>
        id != folder ? $"The member in packs/{folder}/ is {id}." : kind == "set" ? $"packs/{folder}/ is a pack set: a set carries packs, not sets." : null;

    // Before any signature holds: the paths, the signed list (bounded), the pack holding exactly what it lists, and shaped,
    // bounded attestations beside it. Nothing else is parsed.
    private static (List<string> Problems, FilesList? List, byte[]? ListBytes) CheckSigned(PackContent content, Action<PackStep>? explain)
    {
        var problems = PackPaths.Problems(content.Files.Keys).ToList();
        explain?.Invoke(new("paths", problems.Count == 0, problems.Count > 0 ? problems : [$"{content.Files.Count} files, every path in the grammar"]));
        if (problems.Count > 0) return (problems, null, null);
        if (!content.Files.TryGetValue(PackPaths.FilesList, out var listBytes))
        {
            explain?.Invoke(new("files list", false, [$"There is no {PackPaths.FilesList}: build the pack first."]));
            return ([$"There is no {PackPaths.FilesList}: build the pack first."], null, null);
        }
        if (listBytes.Length > MaximumFilesListBytes)
        {
            var large = $"{PackPaths.FilesList} is larger than {MaximumFilesListBytes >> 20} MiB.";
            explain?.Invoke(new("files list", false, [large]));
            return ([large], null, null);
        }
        var (list, listProblem) = FilesList.Parse(listBytes);
        explain?.Invoke(new("files list", list is not null, list is null ? [listProblem!]
            : [$"{list.Id} {list.Version}, {list.Files.Count} files; RFC 8785 canonical", $"digest {FilesList.DigestOf(listBytes)}"]));
        if (list is null) return ([listProblem!], null, null);

        var listed = list.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        foreach (var path in content.Files.Keys.Where(p => FilesList.IsListed(p) && !listed.ContainsKey(p)).Order(StringComparer.Ordinal))
            problems.Add($"'{path}' is not in {PackPaths.FilesList}.");
        foreach (var file in list.Files)
        {
            if (!content.Files.TryGetValue(file.Path, out var bytes)) { problems.Add($"'{file.Path}' is listed but missing."); continue; }
            if (bytes.LongLength == file.Length && FilesList.Hex(bytes) == file.Sha256) continue;
            problems.Add($"'{file.Path}' does not match {PackPaths.FilesList}{Cause(bytes, file)}.");
        }
        // Beside the listed files, only signatures and attestations, each shaped and bounded (§16, §2.1 rule 7).
        problems.AddRange(PackPaths.SignatureFolderProblems(content.Files));
        problems.AddRange(PackAttestations.Problems(content.Files));
        explain?.Invoke(new("content", problems.Count == 0, problems.Count > 0 ? problems
            : [$"exactly the listed files, each with its length and SHA-256 ({list.Files.Sum(f => f.Length):N0} bytes)"]));
        return problems.Count > 0 ? (problems, null, null) : (problems, list, listBytes);
    }

    // Once the signatures hold (or for lint and build, on their own input): the JSON profile, each listed JSON file at most
    // MaximumJsonBytes; pack.json and the kind.
    private static (List<string> Problems, PackManifest? Manifest) CheckContent(PackContent content, FilesList list, Action<PackStep>? explain, bool records)
    {
        var problems = new List<string>();
        foreach (var (path, bytes) in content.Files.Where(f => FilesList.IsListed(f.Key) && f.Key.EndsWith(".json", StringComparison.Ordinal))
                     .OrderBy(f => f.Key, StringComparer.Ordinal))
            if (bytes.Length > MaximumJsonBytes) problems.Add($"'{path}' is larger than {MaximumJsonBytes >> 20} MiB: a JSON file in a pack is at most that.");
            else if (PackJson.Check(bytes, integersOnly: path == PackPaths.LockFile) is { } problem) problems.Add($"'{path}' {problem}.");
        if (records) problems.AddRange(LineProblems(content));
        explain?.Invoke(new("JSON profile", problems.Count == 0, problems.Count > 0 ? problems
            : [records ? "every JSON file (at most 1 MiB) and JSONL line (at most 1 MiB): UTF-8, LF, no repeated key, NFC strings, integers within 2^53"
                : "every JSON file, at most 1 MiB: UTF-8, LF, no repeated key, NFC strings, integers within 2^53 (JSONL files next)"]));
        var profileProblems = problems.Count;
        if (!content.Files.TryGetValue(PackManifest.FileName, out var manifestBytes))
            problems.Add($"There is no {PackManifest.FileName}.");
        else if (manifestBytes.Length <= MaximumJsonBytes)
        {
            var (manifest, manifestProblem) = PackManifest.Parse(manifestBytes);
            if (manifest is null) problems.Add(manifestProblem!);
            else if (manifest.Id != list.Id || manifest.Version != list.Version)
                problems.Add($"{PackManifest.FileName} names {manifest.Id} {manifest.Version}, but {PackPaths.FilesList} names {list.Id} {list.Version}.");
            else
            {
                problems.AddRange(KindProblems(manifest, content.Files));
                explain?.Invoke(new("pack.json and kind", problems.Count == profileProblems, problems.Count > profileProblems ? problems.Skip(profileProblems).ToList()
                    : [$"{manifest.Kind} pack, tier {manifest.Tier}, protection {manifest.Protection}, code {manifest.Code}",
                        .. manifest.FormatMinor > PackManifest.Minor
                            ? [$"format 1.{manifest.FormatMinor}, newer than this reader's {PackManifest.Format}: read as far as it knows it" +
                                (manifest.IgnoredFields.Count > 0 ? $"; ignored {string.Join(", ", manifest.IgnoredFields)}, none named critical" : "")]
                            : (string[])[]]));
                return problems.Count == 0 ? (problems, manifest) : (problems, null);
            }
        }
        explain?.Invoke(new("pack.json and kind", false, problems.Skip(profileProblems).ToList()));
        return (problems, null);
    }

    // Every listed JSONL file, line by line, in the JSON profile.
    private static IEnumerable<string> LineProblems(PackContent content) =>
        content.Files.Where(f => FilesList.IsListed(f.Key) && f.Key.EndsWith(".jsonl", StringComparison.Ordinal)).OrderBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => PackJson.CheckLines(f.Value, MaximumJsonBytes) is { } problem ? $"'{f.Key}' {problem}." : null).OfType<string>();

    /// <summary>What each kind must and must not hold (v1.2 §3).</summary>
    private static IEnumerable<string> KindProblems(PackManifest manifest, IReadOnlyDictionary<string, byte[]> files)
    {
        var records = PackLayout.RecordFiles(files.Keys).Count;
        var all = files.Keys.ToList();
        switch (manifest.Kind)
        {
            case "knowledge" or "snapshot" when records == 0:
                yield return $"A {manifest.Kind} pack holds records under {PackLayout.Records}; this one holds none.";
                break;
            case "definition" when records > 0:
                yield return $"A definition pack declares only; its records come from the host's importer, not from {PackLayout.Records}.";
                break;
            case "composite" when !all.Contains(PackPaths.LockFile) || manifest.Contains.Count == 0:
                yield return $"A composite names what it contains and pins it in {PackPaths.LockFile}.";
                break;
            case "set" when !all.Any(p => p.StartsWith("packs/", StringComparison.Ordinal)):
                yield return "A pack set carries its members under packs/.";
                break;
        }
        // A composite or a set loads its members, not declarations of its own: a restriction it declares would load as if it
        // were absent, so it is refused (review 7, F11: sdk, requires and containableBy were verified, then dropped).
        if (manifest.Kind is "composite" or "set")
            foreach (var field in (string[])["sdk", "requires", "containableBy"])
                if (manifest.Present.Contains(field)) yield return $"A {manifest.Kind} pack declares no {field}: its members carry their own.";
        // Review 1, L6: a composite pins packs and holds no records of its own (spec §3).
        if (manifest.Kind == "composite" && records > 0) yield return $"A composite pack holds no records of its own under {PackLayout.Records}.";
        if (manifest.Kind == "composite" && all.Contains(PackPaths.LockFile))
            foreach (var problem in CompositeProblems(manifest, files)) yield return problem;
        if (manifest.Protection != "open" && !all.Contains("META/seal.json"))
            yield return $"A pack marked {manifest.Protection} must carry META/seal.json.";
        // The pack card is read strictly by every verifier, so it shows the same everywhere (§15).
        if (files.TryGetValue(PackCard.FileName, out var card) && PackCard.Parse(card).Problem is { } cardProblem) yield return cardProblem;
    }

    /// <summary>A composite names the same members in pack.json, members.json and its lock (v1.2 §5).</summary>
    private static IEnumerable<string> CompositeProblems(PackManifest manifest, IReadOnlyDictionary<string, byte[]> files)
    {
        var (entries, lockProblem) = PackLock.Parse(files[PackPaths.LockFile]);
        if (entries is null) { yield return lockProblem!; yield break; }
        if (!files.TryGetValue(PackLayout.Members, out var membersBytes)) { yield return $"A composite pack carries {PackLayout.Members}."; yield break; }
        if (MemberIds(membersBytes) is not { } named) { yield return $"{PackLayout.Members} is malformed."; yield break; }
        var contains = manifest.Contains.Order(StringComparer.Ordinal).ToList();
        if (!named.Order(StringComparer.Ordinal).SequenceEqual(contains))
            yield return $"{PackLayout.Members} and {PackManifest.FileName} name different members.";
        if (!entries.Select(e => e.Id).Order(StringComparer.Ordinal).SequenceEqual(contains))
            yield return $"{PackPaths.LockFile} pins other members than the composite contains.";
    }

    private static readonly HashSet<string> MemberFields = new(StringComparer.Ordinal) { "id", "version", "priority", "enabled", "contains" };

    // A member's values typed as the loader reads them (review 3): an id and a version range as text, a 32-bit priority, a
    // Boolean switch, and a list of ids it contains.
    private static bool IsMember(System.Text.Json.JsonElement m)
    {
        static bool Text(System.Text.Json.JsonElement e) => e.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrEmpty(e.GetString());
        return m.TryGetProperty("id", out var id) && Text(id)
            && m.TryGetProperty("version", out var version) && Text(version)
            && (!m.TryGetProperty("priority", out var priority) || priority.ValueKind == System.Text.Json.JsonValueKind.Number && priority.TryGetInt32(out _))
            && (!m.TryGetProperty("enabled", out var enabled) || enabled.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
            && (!m.TryGetProperty("contains", out var contains)
                || contains.ValueKind == System.Text.Json.JsonValueKind.Array && contains.EnumerateArray().All(Text));
    }

    private static string[]? MemberIds(byte[] membersBytes)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(membersBytes);
            // Review 1, L6: members.json holds its members and nothing else, as every reader takes it (spec §2).
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object || document.RootElement.EnumerateObject().Any(p => p.Name != "members")) return null;
            var members = document.RootElement.GetProperty("members").EnumerateArray().ToList();
            // Each member holds what a composite member is and nothing else, as the loader reads it (review 2, L6).
            if (members.Any(m => m.ValueKind != System.Text.Json.JsonValueKind.Object || m.EnumerateObject().Any(p => !MemberFields.Contains(p.Name)) || !IsMember(m)))
                return null;
            return [.. members.Select(m => m.GetProperty("id").GetString()!)];
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    /// <summary>
    /// A federated set's members (federated packs §3): each is federated itself, by the same publisher, for an audience within
    /// the set's. Otherwise a member taken out of the set would load on a host the set was never published for (FED-review M5).
    /// </summary>
    /// <remarks>It needs no key, so the trust-free check holds it too (review 9, A1: only the verifier did, so the writers wrote,
    /// and lint passed, federated sets every reader refuses).</remarks>
    internal static string? MemberFederationProblem(PackFederation set, string memberId, PackManifest member)
    {
        if (member.Federation is not { } own)
            return $"The member {memberId} of a federated set is not federated itself: out of the set it would reach any host.";
        if (own.Publisher != set.Publisher) return $"The member {memberId} names publisher {own.Publisher}, not the set's {set.Publisher}.";
        if (!set.IsPublic && (own.Audience is null || own.Audience.Any(a => !set.Audience!.Contains(a))))
            return $"The member {memberId} is published for a wider audience than its set.";
        return null;
    }

    /// <summary>
    /// The policy a pack is verified under before a signature in <paramref name="role"/> is added to it (review 8, F4): the host's
    /// own, apart from requiring that role, which the signature being added supplies. When that role is the only one required,
    /// the policy stays as it is, so a pack is never countersigned on no signature at all.
    /// </summary>
    internal static TrustPolicy Before(TrustPolicy policy, string role) =>
        policy.RequiredRoles.Where(r => r != role).ToList() is { Count: > 0 } others && others.Count < policy.RequiredRoles.Count
            ? policy with { RequiredRoles = others } : policy;

    /// <summary>
    /// A pack verified in full: a single pack as <see cref="VerifyOne"/> does, a set together with every member it carries. What
    /// fetches and receipts rely on, so a set is never taken on its outer signature alone (FED-review H2).
    /// </summary>
    internal static PackVerification VerifyWhole(PackContent content, TrustPolicy policy, DateTimeOffset now, HostLedger? ledger = null, bool allowDowngrade = false)
    {
        var first = VerifyOne(content, policy, now, ledger, allowDowngrade);
        if (first.Pack is not { Manifest.Kind: "set" }) return first;
        var (set, members) = VerifySet(content, policy, now, ledger, allowDowngrade, first);
        var refused = members.Where(m => !m.Value.IsVerified).OrderBy(m => m.Key, StringComparer.Ordinal)
            .SelectMany(m => m.Value.Problems.Select(p => $"packs/{m.Key}/: {p}")).ToList();
        return !set.IsVerified ? set : refused.Count > 0 ? new PackVerification(null, refused) : set;
    }

    /// <summary>
    /// A pack set and each of its members, verified independently: the set's own signature binds the members together, and
    /// each member keeps its own list and signatures (v1.2 §5).
    /// </summary>
    internal static (PackVerification Set, IReadOnlyDictionary<string, PackVerification> Members) VerifySet(PackContent content, TrustPolicy policy,
        DateTimeOffset now, HostLedger? ledger = null, bool allowDowngrade = false, PackVerification? verified = null)
    {
        // The ledger's kept revocations once for the whole set, not again for each member (sixth review).
        if (ledger is not null) policy = ledger.Apply(policy);
        // The set as a whole, verified once: a caller that already did passes its result (review 1, I2).
        var set = verified ?? VerifyOne(content, policy, now, ledger, allowDowngrade);
        var members = new Dictionary<string, PackVerification>(StringComparer.Ordinal);
        if (set.Pack is not { } outer) return (set, members);
        if (outer.Manifest.Kind != "set") return (new PackVerification(null, [$"{outer.Id} is a {outer.Manifest.Kind} pack, not a set."]), members);
        // The same set rules CheckIntegrity holds without trust (review 8, F2), here once the set's own signatures hold.
        var (structure, split) = SetStructure(outer.Id, outer.Manifest, content);
        if (split is null) return (new PackVerification(null, structure), members);
        foreach (var (id, files) in split)
        {
            var member = VerifyOne(new PackContent(files, content.Source + "#" + id), policy, now, ledger, allowDowngrade);
            members[id] = member.Pack is { } carried && MemberProblem(id, carried.Id, carried.Manifest.Kind) is { } wrong
                ? new PackVerification(null, [wrong])
                : member.Pack is { } inner && outer.Manifest.Federation is { } federation && MemberFederationProblem(federation, inner.Id, inner.Manifest) is { } problem
                    ? new PackVerification(null, [problem]) : member;
        }
        return (set, members);
    }

    /// <summary>The usual reasons a checked-out pack stops matching its list.</summary>
    private static string Cause(byte[] bytes, ListedFile file)
    {
        if (bytes.AsSpan().StartsWith("version https://git-lfs.github.com/spec/"u8))
            return " (it is a Git LFS pointer: fetch the real file, or keep packs out of LFS)";
        if (bytes.AsSpan().IndexOf("\r\n"u8) >= 0)
        {
            var lf = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal));
            if (lf.LongLength == file.Length && FilesList.Hex(lf) == file.Sha256)
                return " (Git converted its line endings to CRLF: add '* -text -filter -diff' to the pack's .gitattributes)";
        }
        return "";
    }
}
